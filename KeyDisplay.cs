namespace TinyMouseMacro;

internal static class KeyDisplay
{
    internal static string Name(Keys key) => key switch {
        Keys.Menu => "Alt", Keys.LMenu => "左 Alt", Keys.RMenu => "右 Alt",
        Keys.ControlKey => "Ctrl", Keys.LControlKey => "左 Ctrl", Keys.RControlKey => "右 Ctrl",
        Keys.ShiftKey => "Shift", Keys.LShiftKey => "左 Shift", Keys.RShiftKey => "右 Shift",
        Keys.LWin => "左 Win", Keys.RWin => "右 Win", Keys.Back => "Backspace · 退格", Keys.Return => "Enter · 回车",
        Keys.Space => "Space · 空格", Keys.Escape => "Esc", Keys.CapsLock => "Caps Lock · 大小写",
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "小键盘 " + ((int)key - (int)Keys.NumPad0),
        Keys.Oemplus => "+ / =", Keys.OemMinus => "- / _", Keys.Oemcomma => ", / <", Keys.OemPeriod => ". / >",
        Keys.OemQuestion => "/ / ?", Keys.OemSemicolon => "; / :", Keys.OemQuotes => "' / \"",
        Keys.OemOpenBrackets => "[ / {", Keys.OemCloseBrackets => "] / }", Keys.OemPipe => "\\ / |", Keys.Oemtilde => "` / ~",
        _ => key.ToString()
    };
}
