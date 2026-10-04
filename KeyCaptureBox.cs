namespace TinyMouseMacro;

internal sealed class KeyCaptureBox : TextBox
{
    private readonly KeyEventState _keys = new();
    public event Action<Keys, uint>? KeyCaptured;
    public KeyCaptureBox()
    {
        ReadOnly = true; ShortcutsEnabled = false; PlaceholderText = "点击这里，直接按键";
        AccessibleName = "直接按键设置动作";
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Focused && msg.Msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        { CaptureKey(keyData & Keys.KeyCode, (long)msg.LParam); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (Focused && m.Msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        { CaptureKey((Keys)(int)m.WParam, (long)m.LParam); return; }
        if (m.Msg is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        { _keys.Update((int)Side((Keys)(int)m.WParam, (long)m.LParam), false); if (Focused) return; }
        if (Focused && m.Msg is 0x102 or 0x106) return;
        base.WndProc(ref m);
    }
    private static Keys Side(Keys key, long flags) => key switch {
        Keys.Menu => (flags & (1L << 24)) != 0 ? Keys.RMenu : Keys.LMenu,
        Keys.ControlKey => (flags & (1L << 24)) != 0 ? Keys.RControlKey : Keys.LControlKey,
        Keys.ShiftKey => ((flags >> 16) & 0xFF) == 0x36 ? Keys.RShiftKey : Keys.LShiftKey, _ => key };
    private void CaptureKey(Keys key, long flags)
    {
        key = Side(key, flags); _keys.Update((int)key, true);
        KeyCaptured?.Invoke(key, HotkeyParser.IsModifierKey(key) ? 0 : _keys.Modifiers);
    }
    protected override void OnLostFocus(EventArgs e) { _keys.Clear(); base.OnLostFocus(e); }
    internal void CaptureForTest(Keys key, long flags = 0) => CaptureKey(key, flags);
}
