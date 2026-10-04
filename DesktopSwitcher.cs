using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

internal static class DesktopSwitcher
{
    // Remote release is enabled explicitly for one-point screen-switch profiles.
    public static bool IsSwitchProfile(MacroProfile profile) => profile.ReleaseRemoteInput && profile.RepeatCount == 1 &&
        string.IsNullOrWhiteSpace(profile.TargetWindowTitle) && profile.Steps.Count == 1 &&
        profile.Steps[0].Type is MacroStepType.Move or MacroStepType.LeftClick;

    public static async Task PrepareAsync(MacroProfile profile, CancellationToken ct, Action<string>? log = null)
    {
        if (!IsSwitchProfile(profile)) return;
        var step = profile.Steps[0];
        var point = new NativeMethods.Point { X = step.X, Y = step.Y };
        if (!Screen.AllScreens.Any(s => s.Bounds.Contains(step.X, step.Y)))
            throw new InvalidOperationException("目标位置不在当前显示器上，请重新取点。");
        var source = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(source, out var processId);
        string processName;
        try { using var process = Process.GetProcessById(processId); processName = process.ProcessName; }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (System.ComponentModel.Win32Exception) { return; }
        log?.Invoke($"切屏触发已收到 · 前台进程 {processName}");
        var vmware = processName.Equals("vmware", StringComparison.OrdinalIgnoreCase) || processName.Equals("vmware-vmx", StringComparison.OrdinalIgnoreCase);
        var remote = processName.Equals("GameViewer", StringComparison.OrdinalIgnoreCase);
        if (!vmware && !remote) return;
        var target = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(point), 2);
        if (target == 0) throw new InvalidOperationException("目标位置没有可激活的宿主窗口，请重新取点。");
        if (target == source)
            throw new InvalidOperationException("目标位置仍属于当前远程窗口，无法据此切回宿主。请把切屏位置设在另一屏的目标窗口上。");
        ct.ThrowIfCancellationRequested();
        if (vmware)
        {
            // VMware's documented default ungrab chord; never guess a UU-specific shortcut.
            NativeMethods.keybd_event((byte)Keys.ControlKey, 0, 0, NativeMethods.MacroInputTag);
            NativeMethods.keybd_event((byte)Keys.Menu, 0, 0, NativeMethods.MacroInputTag);
            try { await Task.Delay(40, ct); }
            finally
            {
                NativeMethods.keybd_event((byte)Keys.Menu, 0, NativeMethods.KeyeventfKeyup, NativeMethods.MacroInputTag);
                NativeMethods.keybd_event((byte)Keys.ControlKey, 0, NativeMethods.KeyeventfKeyup, NativeMethods.MacroInputTag);
            }
            await Task.Delay(60, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (!NativeMethods.ClipCursor(0)) throw new InvalidOperationException("无法释放鼠标范围限制，请先手动释放远程输入。");
        NativeMethods.SetForegroundWindow(target);
        await Task.Delay(35, ct);
        if (NativeMethods.GetForegroundWindow() != target)
            throw new InvalidOperationException("快捷键已收到，但目标窗口未获得焦点，已取消点击。请先释放远程输入。");
        if (!NativeMethods.SetCursorPos(step.X, step.Y)) throw new InvalidOperationException("系统拒绝移动鼠标，请先释放远程输入。");
        await Task.Delay(45, ct);
        var actual = Cursor.Position;
        if (Math.Abs(actual.X - step.X) > 6 || Math.Abs(actual.Y - step.Y) > 6)
            throw new InvalidOperationException("快捷键已收到，但远程软件仍限制鼠标，已停止点击。请先释放输入或检查独占模式。");
        if (NativeMethods.GetForegroundWindow() != target)
            throw new InvalidOperationException("目标窗口在切屏过程中失去焦点，已取消后续点击。请先释放远程输入。");
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClipCursor(nint rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeFilter);
}
