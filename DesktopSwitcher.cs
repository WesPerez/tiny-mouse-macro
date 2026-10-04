using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

internal static class DesktopSwitcher
{
    internal static bool IsRemoteProcess(string name) => IsVmwareProcess(name) ||
        name.Equals("GameViewer", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVmwareProcess(string name) => name.Equals("vmware", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vmware-vmx", StringComparison.OrdinalIgnoreCase) || name.Equals("mksSandbox", StringComparison.OrdinalIgnoreCase);
}

internal static class WindowActivation
{
    // Focus requests and pointer movement may be retried; user actions are never replayed.
    internal static async Task EnsureForegroundAsync(nint source, nint target, Func<nint> foreground,
        Func<bool> activate, Func<bool> targetUnchanged, Func<int, CancellationToken, Task> delay,
        CancellationToken ct, Action<string>? log = null, bool allowClickActivation = false)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var current = foreground();
            if (!targetUnchanged() || (current != 0 && current != source && current != target))
                throw new InvalidOperationException("交接期间前台或目标窗口改变，已停止后续操作。");
            var accepted = current == target || activate();
            await delay(60, ct);
            ct.ThrowIfCancellationRequested();
            current = foreground();
            if (!targetUnchanged() || (current != 0 && current != source && current != target))
                throw new InvalidOperationException("请求焦点后出现其他窗口，已停止后续操作。");
            if (current == target)
            {
                await delay(60, ct);
                ct.ThrowIfCancellationRequested();
                if (targetUnchanged() && foreground() == target)
                {
                    log?.Invoke($"焦点交接已验证 · 目标 0x{target:X} · 第 {attempt} 次请求");
                    return;
                }
            }
            log?.Invoke($"焦点交接 {attempt}/6 · 请求返回 {accepted} · 当前 0x{foreground():X} · 目标 0x{target:X}");
            if (allowClickActivation && current == source)
            {
                log?.Invoke("置前请求未获准，改由方案本身的一次点击激活目标；先验证位置。");
                return;
            }
        }
        throw new InvalidOperationException("快捷键已收到，但 Windows 未将焦点交给目标窗口；已停止后续操作。");
    }
}

internal static class PointerPlacement
{
    internal static bool IsAtTarget(Point actual, Point target) =>
        Math.Abs((long)actual.X - target.X) <= 2 && Math.Abs((long)actual.Y - target.Y) <= 2;

    internal static async Task MoveUntilStableAsync(Point target, Func<bool> move, Func<Point> read,
        Func<bool> contextUnchanged, Func<int, CancellationToken, Task> delay, CancellationToken ct, Action<string>? log = null)
    {
        Point actual = default;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!contextUnchanged()) throw new InvalidOperationException("定位期间目标窗口已改变，已停止后续操作。");
            if (!move()) throw new InvalidOperationException("系统拒绝移动鼠标，已停止后续操作。");
            await delay(50, ct);
            ct.ThrowIfCancellationRequested();
            actual = read();
            if (IsAtTarget(actual, target))
            {
                await delay(50, ct);
                ct.ThrowIfCancellationRequested();
                actual = read();
                if (contextUnchanged() && IsAtTarget(actual, target)) return;
            }
            log?.Invoke($"定位重试 {attempt}/6 · 当前 ({actual.X}, {actual.Y})，目标 ({target.X}, {target.Y})");
        }
        throw new InvalidOperationException($"鼠标仍被限制在 ({actual.X}, {actual.Y})，已停止后续操作。");
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
