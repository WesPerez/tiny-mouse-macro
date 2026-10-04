using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class RecorderService : IDisposable
{
    private readonly InputThread _thread = new("TinyMouseMacro recorder");
    private readonly List<MacroStep> _steps = [];
    private readonly Stopwatch _clock = new();
    private readonly KeyEventState _keys = new();
    private readonly RecordingTimeline _timeline = new();
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private nint _mouseHook, _keyboardHook, _excludedWindow;
    private volatile bool _recording;
    private bool _disposed;
    private long _lastMove;
    public bool LimitReached { get; private set; }
    public bool IsRecording => _recording;
    public event Action<MacroStep>? StepRecorded;
    public void Start(bool keyboard = true, nint excludedWindow = 0)
    {
        _thread.Invoke(() =>
        {
            if (_recording) return;
            _steps.Clear(); _keys.Clear(); _keys.SynchronizeModifiers(); _timeline.Reset(); _lastMove = -150; LimitReached = false;
            _excludedWindow = excludedWindow;
            _mouseProc = MouseCallback;
            _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseProc, NativeMethods.GetModuleHandleW(null), 0);
            if (_mouseHook == 0) throw new InvalidOperationException("鼠标录制启动失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (keyboard)
            {
                _keyboardProc = KeyboardCallback;
                _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardProc, NativeMethods.GetModuleHandleW(null), 0);
                if (_keyboardHook == 0)
                {
                    var error = Marshal.GetLastWin32Error(); StopCore();
                    throw new InvalidOperationException("键盘录制启动失败：" + new Win32Exception(error).Message);
                }
            }
            _clock.Restart(); _recording = true;
        });
    }
    public List<MacroStep> Stop()
    {
        List<MacroStep> result = [];
        _thread.Invoke(() => { StopCore(); result = _steps.ToList(); });
        return result;
    }
    private void StopCore()
    {
        _recording = false; _clock.Stop();
        if (_mouseHook != 0) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = 0; _mouseProc = null; _keyboardProc = null;
    }
    private bool IsOwnWindow(nint window)
    {
        if (_excludedWindow == 0) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }
    private nint MouseCallback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && _recording)
            {
                var ms = Marshal.PtrToStructure<NativeMethods.Msllhookstruct>(data);
                if ((nuint)ms.DwExtraInfo != NativeMethods.MacroInputTag && !IsOwnWindow(NativeMethods.WindowFromPoint(ms.Pt)))
                {
                    var now = _clock.ElapsedMilliseconds;
                    MacroStep? step = message switch
                    {
                        NativeMethods.WmLbuttonDown => new() { Type = MacroStepType.LeftClick, X = ms.Pt.X, Y = ms.Pt.Y },
                        NativeMethods.WmRButtonDown => new() { Type = MacroStepType.RightClick, X = ms.Pt.X, Y = ms.Pt.Y },
                        NativeMethods.WmMButtonDown => new() { Type = MacroStepType.MiddleClick, X = ms.Pt.X, Y = ms.Pt.Y },
                        NativeMethods.WmMouseWheel => new() { Type = MacroStepType.MouseWheel, WheelDelta = (short)(ms.MouseData >> 16) },
                        NativeMethods.WmMouseMove when now - _lastMove >= 150 => new() { Type = MacroStepType.Move, X = ms.Pt.X, Y = ms.Pt.Y },
                        _ => null
                    };
                    if (step is not null) { if (step.Type == MacroStepType.Move) _lastMove = now; Add(step, now); }
                }
            }
        }
        catch { }
        return NativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }
    private nint KeyboardCallback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && _recording && message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
            {
                var kb = Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(data);
                if ((nuint)kb.DwExtraInfo != NativeMethods.MacroInputTag)
                {
                    var key = (int)kb.VkCode; var down = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
                    _keys.Update(key, down);
                    if (down && !HotkeyParser.IsModifierKey((Keys)key) && !IsOwnWindow(NativeMethods.GetForegroundWindow()) &&
                        !(key is (int)Keys.F10 or (int)Keys.P && (_keys.Modifiers & 3) == 3))
                    {
                        Add(new() { Type = _keys.Modifiers == 0 ? MacroStepType.KeyPress : MacroStepType.KeyCombo,
                            KeyCode = key, KeyModifiers = _keys.Modifiers }, _clock.ElapsedMilliseconds);
                    }
                }
            }
        }
        catch { }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }
    private void Add(MacroStep step, long now)
    {
        // Bounded to keep an accidentally forgotten recording from consuming unbounded memory.
        if (_steps.Count >= 100000) { LimitReached = true; return; }
        _timeline.Stamp(step, now); _steps.Add(step); StepRecorded?.Invoke(step);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _thread.Invoke(StopCore); _thread.Dispose(); }
}

internal sealed class RecordingTimeline
{
    private long _previous;
    public void Reset() => _previous = 0;
    public void Stamp(MacroStep step, long elapsedMs)
    {
        step.RecordedDelayMs = (int)Math.Clamp(elapsedMs - _previous, 0, 86400000);
        _previous = elapsedMs;
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
