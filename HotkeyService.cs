using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed record HotkeySignal(string ProfileId, string Shortcut, string Source, bool Diagnostic);

public sealed class HotkeyService : IDisposable
{
    public const string StopShortcut = "Ctrl+Alt+F10", PauseShortcut = "Ctrl+Alt+P";
    private readonly InputThread _thread = new("TinyMouseMacro hotkeys");
    private readonly Dictionary<int, Binding> _registered = [];
    private readonly Dictionary<KeyChord, Binding> _keyboard = [];
    private readonly Dictionary<int, Binding> _mouse = [];
    private readonly HashSet<int> _swallowed = [];
    private readonly KeyEventState _hookState = new(), _rawState = new();
    private readonly TriggerDeduplicator _dedup = new();
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private nint _keyboardHook, _mouseHook;
    private bool _rawEnabled, _disposed;
    private string? _configurationKey;
    private List<string> _registrationErrors = [];
    private readonly System.Windows.Forms.Timer _stateRecovery = new() { Interval = 500 };
    private long _lastKeyboardActivity;
    private nint _lastForeground;
    private int _registeredCount;
    private volatile bool _suspended, _diagnostic, _paused;
    public bool StopAvailable { get; private set; }
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value || _disposed) return;
            _suspended = value;
            _thread.Invoke(() =>
            {
                foreach (var (id, binding) in _registered.ToArray())
                {
                    if (binding.Id == "$stop") continue;
                    if (value) NativeMethods.UnregisterHotKey(_thread.Handle, id);
                    else if (!NativeMethods.RegisterHotKey(_thread.Handle, id, binding.Chord.Modifiers | NativeMethods.ModNoRepeat, (uint)binding.Chord.Key))
                    {
                        _registered.Remove(id);
                        if (!binding.Id.StartsWith('$')) _keyboard.Remove(binding.Chord);
                        _registrationErrors.Add($"{binding.Name}：恢复快捷键失败，请重新连接监听");
                    }
                }
                if (!value) _configurationKey = null;
            });
        }
    }
    public bool DiagnosticMode { get => _diagnostic; set => _diagnostic = value; }
    public bool Paused { get => _paused; set => _paused = value; }
    public int RegisteredCount => _registeredCount;
    public string BackendStatus { get; private set; } = "未启用";
    public event Action<HotkeySignal>? Triggered;
    public event Action? EmergencyStop;
    public event Action? PauseRequested;
    public HotkeyService()
    {
        _thread.MessageReceived = OnMessage;
        _thread.Invoke(() =>
        {
            _stateRecovery.Tick += (_, _) => RecoverKeyState();
            _stateRecovery.Start();
        });
    }

    public List<string> RegisterAll(IEnumerable<MacroProfile> profiles, bool rawInput = false, bool force = false)
    {
        var snapshots = profiles.Select(p => new { p.Id, p.Name, p.Enabled, p.TriggerType,
            p.TriggerKey, p.TriggerModifiers, p.Hotkey, p.TriggerMouseButton }).ToArray();
        var signature = System.Text.Json.JsonSerializer.Serialize(snapshots.Select(p => new { p.Id, p.Enabled,
            p.TriggerType, p.TriggerKey, p.TriggerModifiers, p.Hotkey, p.TriggerMouseButton })) + rawInput;
        if (!force && signature == _configurationKey) return _registrationErrors.ToList();
        _configurationKey = signature;
        var errors = new List<string>();
        _thread.Invoke(() =>
        {
            ClearCore();
            var nextId = 100;
            Register(new("$stop", "停止", new((int)Keys.F10, 3)), true);
            Register(new("$pause", "暂停", new((int)Keys.P, 3)), true);
            foreach (var p in snapshots)
            {
                if (!p.Enabled) continue;
                if (p.TriggerType == MacroTriggerType.MouseButton)
                {
                    if (p.TriggerMouseButton is not (3 or 4 or 5)) continue;
                    if (!_mouse.TryAdd(p.TriggerMouseButton, new(p.Id, p.Name, default))) errors.Add($"{p.Name}：鼠标按键重复");
                    continue;
                }
                uint modifiers, key;
                if (p.TriggerType == MacroTriggerType.KeyboardHook)
                { modifiers = p.TriggerModifiers & 15; key = (uint)p.TriggerKey; if (key == 0) continue; }
                else
                {
                    if (string.IsNullOrWhiteSpace(p.Hotkey)) continue;
                    if (!HotkeyParser.TryParse(p.Hotkey, out modifiers, out key, out var error))
                    { errors.Add($"{p.Name}：{error}"); continue; }
                }
                if (key > 254 || HotkeyParser.IsModifierKey((Keys)key))
                { errors.Add($"{p.Name}：需要一个非修饰键"); continue; }
                Register(new(p.Id, p.Name, new((int)key, modifiers)), p.TriggerType == MacroTriggerType.KeyboardHotkey);
            }
            _keyboardProc = KeyboardCallback;
            _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandleW(null), 0);
            if (_keyboardHook == 0) errors.Add("键盘兼容监听启动失败：" + LastError());
            if (_mouse.Count > 0)
            {
                _mouseProc = MouseCallback;
                _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseProc, NativeMethods.GetModuleHandleW(null), 0);
                if (_mouseHook == 0) { errors.Add("鼠标监听启动失败：" + LastError()); _mouse.Clear(); }
            }
            if (rawInput)
            { _rawEnabled = SetRawInput(true); if (!_rawEnabled) errors.Add("后台输入补充启动失败：" + LastError()); }
            _hookState.SynchronizeModifiers();
            _rawState.SynchronizeModifiers();
            _registeredCount = _keyboard.Values.Count(b => !b.Id.StartsWith('$') &&
                (_keyboardHook != 0 || _registered.Values.Contains(b) || _rawEnabled)) + _mouse.Count;
            StopAvailable = _keyboard.Values.Any(b => b.Id == "$stop" &&
                (_keyboardHook != 0 || _registered.Values.Contains(b) || _rawEnabled));
            BackendStatus = $"系统热键 {_registered.Count} · 键盘监听 {(_keyboardHook != 0 ? "就绪" : "失败")} · 后台输入 {(_rawEnabled ? "已开启" : "关闭")}";

            void Register(Binding b, bool system)
            {
                if (_keyboard.ContainsKey(b.Chord))
                { errors.Add($"{b.Name}：{b.Chord} 已被其他方案或停止／暂停功能使用"); return; }
                if (system)
                {
                    var id = nextId++;
                    if (!NativeMethods.RegisterHotKey(_thread.Handle, id, b.Chord.Modifiers | NativeMethods.ModNoRepeat, (uint)b.Chord.Key))
                    {
                        // A conflict is not permission to steal another application's shortcut via a hook.
                        var reason = LastError();
                        if (b.Id.StartsWith('$'))
                            errors.Add($"{b.Name}：系统热键注册失败（{reason}），将尝试键盘兼容监听；请在诊断页验证");
                        else
                        { errors.Add($"{b.Name}：{b.Chord} 注册失败（{reason}），请换一个按键"); return; }
                    }
                    else _registered[id] = b;
                }
                _keyboard[b.Chord] = b;
            }
        });
        _registrationErrors = errors;
        return errors;
    }

    private nint KeyboardCallback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
            {
                var kb = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(data);
                if ((nuint)kb.DwExtraInfo == NativeMethods.MacroInputTag) return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
                var key = (int)kb.VkCode;
                _lastKeyboardActivity = Environment.TickCount64;
                var down = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
                var fresh = _hookState.Update(key, down);
                if (!down && _swallowed.Remove(key)) return 1;
                if (down && _swallowed.Contains(key)) return 1;
                if (down && fresh && _keyboard.TryGetValue(new(key, _hookState.Modifiers), out var b) && CanDispatch(b))
                { Dispatch(b, "键盘监听", kb.Time); _swallowed.Add(key); return 1; }
            }
        }
        catch { /* Do not propagate managed exceptions through a native hook. */ }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint MouseCallback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && message is NativeMethods.WmMButtonDown or NativeMethods.WmXButtonDown)
            {
                var ms = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(data);
                if ((nuint)ms.DwExtraInfo != NativeMethods.MacroInputTag)
                {
                    var button = message == NativeMethods.WmMButtonDown ? 3 : (int)(ms.MouseData >> 16) + 3;
                    if (_mouse.TryGetValue(button, out var b) && CanDispatch(b)) Dispatch(b, "鼠标监听", ms.Time);
                }
            }
        }
        catch { }
        return NativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }

    private bool CanDispatch(Binding b) => b.Id == "$stop" || (!_suspended && (b.Id == "$pause" || !_paused));
    private void Dispatch(Binding b, string source, uint time)
    {
        if (!_dedup.Accept(b.Id, source, time)) return;
        if (b.Id == "$stop") { EmergencyStop?.Invoke(); return; }
        if (b.Id == "$pause") { _thread.Post(() => PauseRequested?.Invoke()); return; }
        var signal = new HotkeySignal(b.Id, b.Chord.Key == 0 ? "鼠标键" : b.Chord.ToString(), source, _diagnostic);
        _thread.Post(() => Triggered?.Invoke(signal));
    }

    private void OnMessage(int message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmHotkey && _registered.TryGetValue((int)wParam, out var b) && CanDispatch(b))
            Dispatch(b, "系统热键", unchecked((uint)NativeMethods.GetMessageTime()));
        else if (message == NativeMethods.WmInput && _rawEnabled) ReadRaw(lParam);
        else if (message == NativeMethods.WmInputDeviceChange)
        { _rawState.Clear(); _hookState.Clear(); _swallowed.Clear(); }
    }

    private void ReadRaw(nint input)
    {
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        uint size = 0;
        if (NativeMethods.GetRawInputData(input, 0x10000003, 0, ref size, headerSize) != 0 || size < headerSize || size > 4096) return;
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(input, 0x10000003, memory, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<NativeMethods.RawInputHeader>(memory);
            if (header.Type != 1 || size < headerSize + Marshal.SizeOf<NativeMethods.RawKeyboard>()) return;
            var kb = Marshal.PtrToStructure<NativeMethods.RawKeyboard>(memory + (int)headerSize);
            if (kb.ExtraInformation == (uint)NativeMethods.MacroInputTag || kb.VKey == 255) return;
            var key = (int)kb.VKey;
            _lastKeyboardActivity = Environment.TickCount64;
            if (key == (int)Keys.ControlKey) key = (kb.Flags & 2) != 0 ? (int)Keys.RControlKey : (int)Keys.LControlKey;
            if (key == (int)Keys.Menu) key = (kb.Flags & 2) != 0 ? (int)Keys.RMenu : (int)Keys.LMenu;
            if (key == (int)Keys.ShiftKey) key = kb.MakeCode == 0x36 ? (int)Keys.RShiftKey : (int)Keys.LShiftKey;
            var down = (kb.Flags & 1) == 0;
            if (_rawState.Update(key, down) && down && _keyboard.TryGetValue(new(key, _rawState.Modifiers), out var b) && CanDispatch(b))
                Dispatch(b, "后台输入（不拦截）", unchecked((uint)NativeMethods.GetMessageTime()));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private bool SetRawInput(bool enabled) => NativeMethods.RegisterRawInputDevices(
        [new() { UsagePage = 1, Usage = 6, Flags = enabled ? 0x2100u : 1u, Target = enabled ? _thread.Handle : 0 }],
        1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());
    private void RecoverKeyState()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            _lastForeground = foreground;
            _hookState.SynchronizeModifiers(); _rawState.SynchronizeModifiers();
        }
        // Do not poll at keyboard rate or fight other applications' hook order.
        // A quiet period clears stale main-key latches after missed key-up / desktop changes.
        if (Environment.TickCount64 - _lastKeyboardActivity < 1500) return;
        _hookState.RemoveReleasedKeys(); _rawState.RemoveReleasedKeys();
        _swallowed.RemoveWhere(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) == 0);
    }
    private void ClearCore()
    {
        foreach (var id in _registered.Keys) NativeMethods.UnregisterHotKey(_thread.Handle, id);
        _registered.Clear(); _keyboard.Clear(); _mouse.Clear();
        if (_keyboardHook != 0) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = 0;
        if (_rawEnabled) SetRawInput(false);
        _rawEnabled = false;
        _hookState.Clear(); _rawState.Clear(); _swallowed.Clear(); _dedup.Clear();
    }
    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread.Invoke(() => { _stateRecovery.Dispose(); ClearCore(); });
        _thread.Dispose();
    }
    private sealed record Binding(string Id, string Name, KeyChord Chord);
}

internal readonly record struct KeyChord(int Key, uint Modifiers)
{
    public override string ToString() => HotkeyParser.Normalize(Modifiers, (uint)Key);
}

internal sealed class KeyEventState
{
    private readonly HashSet<int> _down = [];
    public bool Update(int key, bool down) => down ? _down.Add(key) : _down.Remove(key);
    public uint Modifiers => (Has(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey) ? 2u : 0) |
        (Has(Keys.Menu, Keys.LMenu, Keys.RMenu) ? 1u : 0) | (Has(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey) ? 4u : 0) |
        (_down.Contains((int)Keys.LWin) || _down.Contains((int)Keys.RWin) ? 8u : 0);
    private bool Has(Keys a, Keys b, Keys c) => _down.Contains((int)a) || _down.Contains((int)b) || _down.Contains((int)c);
    public void SynchronizeModifiers()
    {
        foreach (var key in new[] { Keys.LControlKey, Keys.RControlKey, Keys.LMenu, Keys.RMenu, Keys.LShiftKey, Keys.RShiftKey, Keys.LWin, Keys.RWin })
            Update((int)key, (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0);
    }
    public void RemoveReleasedKeys() => _down.RemoveWhere(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) == 0);
    public void Clear() => _down.Clear();
}

internal sealed class TriggerDeduplicator
{
    private readonly Dictionary<string, (string Source, uint Time, long Received)> _last = [];
    public bool Accept(string id, string source, uint time)
    {
        var received = Environment.TickCount64;
        // Compare native event timestamps, not delivery times: queued messages may arrive late.
        // Some injected input uses time=0, for which only a bounded receive-time fallback exists.
        if (_last.TryGetValue(id, out var last) && last.Source != source &&
            ((time != 0 && last.Time != 0 && Math.Abs((long)unchecked((int)(time - last.Time))) <= 40) ||
             ((time == 0 || last.Time == 0) && received - last.Received <= 180))) return false;
        _last[id] = (source, time, received); return true;
    }
    public void Clear() => _last.Clear();
}
