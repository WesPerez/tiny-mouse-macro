using System.Runtime.InteropServices;
using TinyMouseMacro;

// Assign this executable in iCUE's Launch App action. Renamed copies end in .<profile-id>.exe.
// A separate non-elevated executable avoids asking for UAC on every key press.
var id = args.Length == 2 && args[0] == "--profile" ? args[1] :
    Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.Split('.').LastOrDefault();
if (id is null || !Guid.TryParseExact(id, "N", out _)) return Fail(2, "请使用诊断页生成的方案启动器，保留文件名末尾的方案 ID。");
var window = Native.FindWindow(null, UiText.AppTitle);
if (window == 0) return Fail(3, "未找到主程序，请先运行新版 TinyMouseMacro。");
var message = Native.RegisterWindowMessage("TinyMouseMacro.Run." + id);
if (message == 0) return Fail(4, "无法创建本地触发消息。");
if (Native.SendMessageTimeout(window, message, 0, 0, 2, 1500, out var accepted) == 0)
    return Fail(4, "主程序未响应或消息被拒绝。请在新版中启用 iCUE 入口。");
return accepted == 1 ? 0 : Fail(5, "主程序未接受方案。请确认已退出旧版、已允许 iCUE 入口且该单步方案已启用。");

static int Fail(int code, string reason)
{
    // No dialogs or foreground activation; iCUE does not display executable exit codes.
    try
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyMouseMacro");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "launcher-last-error.txt"), $"{DateTime.Now:O} [{code}] {reason}");
    }
    catch { }
    return code;
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
