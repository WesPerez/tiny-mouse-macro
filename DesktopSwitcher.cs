using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

internal static class DesktopSwitcher
{
    // Remote release is enabled explicitly for one-point screen-switch profiles.
    public static bool IsSwitchProfile(MacroProfile profile) => profile.ReleaseRemoteInput && profile.RepeatCount == 1 &&
        string.IsNullOrWhiteSpace(profile.TargetWindowTitle) && profile.Steps.Count == 1 &&
        profile.Steps[0].Type is MacroStepType.Move or MacroStepType.LeftClick;

    public static bool CanAssistVmware(MacroProfile profile, bool compatibility, Rectangle sourceBounds) =>
        compatibility && profile.RepeatCount == 1 && string.IsNullOrWhiteSpace(profile.TargetWindowTitle) &&
        profile.Steps.Count == 1 && profile.Steps[0].RecordedDelayMs is null &&
        profile.Steps[0].Type is MacroStepType.Move or MacroStepType.LeftClick &&
        !sourceBounds.Contains(profile.Steps[0].X, profile.Steps[0].Y);

    // A compatibility-only move retry, not a change to the saved remote-release preference.
    public static async Task<bool> TryVmwareMoveAsync(MacroProfile profile, bool compatibility, CancellationToken ct, Action<string> log)
    {
        if (!compatibility || profile.ReleaseRemoteInput) return false;
        var source = NativeMethods.GetForegroundWindow();
        if (!NativeMethods.GetWindowRect(source, out var rectangle)) return false;
        var bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        if (!CanAssistVmware(profile, compatibility, bounds)) return false;
        NativeMethods.GetWindowThreadProcessId(source, out var id);
        try
        {
            using var process = Process.GetProcessById(id);
            if (!IsVmwareProcess(process.ProcessName)) return false;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        var step = profile.Steps[0];
        var point = new Point(step.X, step.Y);
        if (!Screen.AllScreens.Any(screen => screen.Bounds.Contains(point)))
            throw new InvalidOperationException("目标位置不在当前显示器上，请重新取点。");
        nint WindowAtTarget() => NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = point.X, Y = point.Y }), 2);
        var target = WindowAtTarget();
        if (target == 0 || target == source) return false;
        bool ContextUnchanged()
        {
            var foreground = NativeMethods.GetForegroundWindow();
            return (foreground == source || foreground == target) && WindowAtTarget() == target;
        }
        log($"VMware 切屏定位开始 · 目标 ({point.X}, {point.Y}) · 稳定后仅点击一次");
        await PointerPlacement.MoveUntilStableAsync(point,
            () => NativeMethods.SetCursorPos(point.X, point.Y), () => Cursor.Position,
            ContextUnchanged, (ms, token) => Task.Delay(ms, token), ct, log);
        ct.ThrowIfCancellationRequested();
        if (!ContextUnchanged() || !PointerPlacement.IsAtTarget(Cursor.Position, point))
            throw new InvalidOperationException("点击前目标位置发生变化，已取消点击。");
        if (step.Type == MacroStepType.LeftClick)
        {
            NativeMethods.mouse_event(NativeMethods.MouseEventFLeftDown, 0, 0, 0, NativeMethods.MacroInputTag);
            try { await Task.Delay(35, ct); }
            finally { NativeMethods.mouse_event(NativeMethods.MouseEventFLeftUp, 0, 0, 0, NativeMethods.MacroInputTag); }
            // Acknowledge activation separately from cursor placement; never retry the click itself.
            for (var attempt = 0; attempt < 5 && NativeMethods.GetForegroundWindow() != target; attempt++)
                await Task.Delay(40, ct);
            if (NativeMethods.GetForegroundWindow() != target)
                throw new InvalidOperationException("已定位并点击一次，但目标窗口未获得焦点；未追加点击。");
        }
        log($"VMware 切屏定位已验证 · ({point.X}, {point.Y})");
        return true;
    }

    internal static bool IsVmwareProcess(string name) => name.Equals("vmware", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vmware-vmx", StringComparison.OrdinalIgnoreCase) || name.Equals("mksSandbox", StringComparison.OrdinalIgnoreCase);

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
        var vmware = IsVmwareProcess(processName);
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

internal static class PointerPlacement
{
    internal static bool IsAtTarget(Point actual, Point target) =>
        Math.Abs((long)actual.X - target.X) <= 2 && Math.Abs((long)actual.Y - target.Y) <= 2;

    // Only moves are retried. The caller performs at most one click after this succeeds.
    internal static async Task MoveUntilStableAsync(Point target, Func<bool> move, Func<Point> read,
        Func<bool> contextUnchanged, Func<int, CancellationToken, Task> delay, CancellationToken ct, Action<string>? log = null)
    {
        Point actual = default;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!contextUnchanged()) throw new InvalidOperationException("切屏期间目标窗口已改变，已取消点击。");
            if (!move()) throw new InvalidOperationException("系统拒绝移动鼠标，已取消点击。");
            await delay(50, ct);
            actual = read();
            if (IsAtTarget(actual, target))
            {
                await delay(50, ct);
                actual = read();
                if (contextUnchanged() && IsAtTarget(actual, target)) return;
            }
            log?.Invoke($"VMware 定位重试 {attempt}/6 · 当前 ({actual.X}, {actual.Y})，目标 ({target.X}, {target.Y})");
        }
        throw new InvalidOperationException($"鼠标仍被 VMware 限制在 ({actual.X}, {actual.Y})，未点击。请开启方案的“切屏前尝试释放”后重试。");
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClipCursor(out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClipCursor(nint rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeFilter);
}
