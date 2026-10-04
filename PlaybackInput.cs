using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

internal interface IPlaybackInput
{
    uint Modifiers { get; }
    void Key(int key, bool down);
    void Character(char character);
    void Mouse(uint flags, uint data = 0);
}

internal static class PhysicalModifiers
{
    internal static uint Read() => Read(key => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0);
    internal static uint Read(Func<Keys, bool> down) =>
        (down(Keys.LControlKey) || down(Keys.RControlKey) ? 2u : 0) |
        (down(Keys.LMenu) || down(Keys.RMenu) ? 1u : 0) |
        (down(Keys.LShiftKey) || down(Keys.RShiftKey) ? 4u : 0) |
        (down(Keys.LWin) || down(Keys.RWin) ? 8u : 0);
}

internal sealed class WindowsPlaybackInput : IPlaybackInput
{
    private readonly Dictionary<int, NativeMethods.Input> _held = [];
    public uint Modifiers => PhysicalModifiers.Read();
    public void Key(int key, bool down)
    {
        if (!down && _held.Remove(key, out var held))
        {
            // A focus/layout switch between down and up must release the original scan code.
            held.U.Ki.DwFlags |= NativeMethods.KeyeventfKeyup;
            Send([held]);
            return;
        }
        var thread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var scan = NativeMethods.MapVirtualKeyExW((uint)key, 4, NativeMethods.GetKeyboardLayout(thread));
        var packet = KeyPacket(key, scan, down);
        Send([packet]);
        if (down) _held[key] = packet;
    }
    internal static NativeMethods.Input KeyPacket(int key, uint scan, bool down)
    {
        // Pause's E1 sequence and unmapped F13-F24/media keys need the virtual-key fallback.
        var useScan = key != (int)Keys.Pause && (scan & 0xFF) != 0 && (scan & 0xFF00) != 0xE100;
        return new() { Type = NativeMethods.InputKeyboard, U = new() { Ki = new()
        {
            WVk = useScan ? (ushort)0 : (ushort)key, WScan = useScan ? (ushort)(scan & 0xFF) : (ushort)0,
            DwFlags = (down ? 0u : NativeMethods.KeyeventfKeyup) | (useScan ? 8u : 0) |
                (useScan && (scan & 0xFF00) == 0xE000 ? 1u : 0),
            DwExtraInfo = (nint)NativeMethods.MacroInputTag
        } } };
    }
    public void Character(char character)
    {
        NativeMethods.Input Packet(bool down) => new() { Type = NativeMethods.InputKeyboard, U = new() { Ki = new()
        { WScan = character, DwFlags = NativeMethods.KeyeventfUnicode | (down ? 0u : NativeMethods.KeyeventfKeyup), DwExtraInfo = (nint)NativeMethods.MacroInputTag } } };
        Send([Packet(true), Packet(false)]);
    }
    public void Mouse(uint flags, uint data = 0) => Send([new() { Type = 0, U = new() { Mi = new()
        { Flags = flags, MouseData = data, ExtraInfo = (nuint)NativeMethods.MacroInputTag } } }]);
    private static void Send(NativeMethods.Input[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent == inputs.Length) return;
        // Partial Unicode down/up insertion must not leave a synthetic key down.
        if (sent == 1 && inputs.Length == 2) NativeMethods.SendInput(1, [inputs[1]], Marshal.SizeOf<NativeMethods.Input>());
        throw new InvalidOperationException("系统未完整接收模拟输入，已停止回放：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);
    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint thread);
}
