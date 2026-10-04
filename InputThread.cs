using System.Runtime.InteropServices;

namespace TinyMouseMacro;

// A dedicated message pump keeps low-level hooks independent of UI rendering and dialogs.
internal sealed class InputThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Control _dispatcher = null!;
    private MessageWindow _window = null!;
    private Exception? _startError;
    private volatile bool _disposed;
    public nint Handle => _window.Handle;
    public Action<int, nint, nint>? MessageReceived { get; set; }

    public InputThread(string name)
    {
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = new Control();
                _ = _dispatcher.Handle;
                _window = new MessageWindow(this);
            }
            catch (Exception ex) { _startError = ex; }
            finally { _ready.Set(); }
            if (_startError is not null) return;
            Application.Run();
            _window.DestroyHandle();
            _dispatcher.Dispose();
        }) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("无法启动输入监听线程", _startError);
    }

    public void Invoke(Action action) => _dispatcher.Invoke(action);
    public void Post(Action action)
    {
        if (_disposed) return;
        try { _dispatcher.BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Invoke(Application.ExitThread);
        _thread.Join(2000);
        _ready.Dispose();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly InputThread _owner;
        public MessageWindow(InputThread owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "TinyMouseMacro.Input", Parent = new nint(-3) });
        }
        protected override void WndProc(ref Message m)
        {
            try { _owner.MessageReceived?.Invoke(m.Msg, m.WParam, m.LParam); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            // DefWindowProc is required to clean up foreground WM_INPUT packets.
            base.WndProc(ref m);
        }
    }
}

internal static partial class NativeMethods
{
    public static readonly nuint MacroInputTag = 0x544D4D31;
    public const int WmInput = 0x00FF;
    public const int WmInputDeviceChange = 0x00FE;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    public static extern int GetMessageTime();

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
}
