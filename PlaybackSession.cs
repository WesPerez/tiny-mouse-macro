using System.Diagnostics;

namespace TinyMouseMacro;

// One session follows focus through all mouse/keyboard steps, including recordings and loops.
// The interface keeps regression tests completely separate from the user's real desktop.
internal interface IPlaybackDesktop
{
    nint Foreground { get; }
    Point Cursor { get; }
    nint WindowAt(Point point);
    nint FindWindow(string title);
    bool IsWindow(nint window);
    bool SameApplication(nint first, nint second);
    bool IsRemote(nint window);
    bool IsOnScreen(Point point);
    bool OutsideClip(Point point);
    bool ReleaseClip();
    bool Activate(nint window);
    bool Move(Point point);
    Task Delay(int ms, CancellationToken ct);
}

internal sealed class PlaybackSession(IPlaybackDesktop desktop, Action<string>? log)
{
    private nint _expected = desktop.Foreground;
    private nint _remoteWindow;
    private bool _remoteWindowKnown, _remoteValue;
    private bool _acceptNextForeground;
    private nint _clickTarget;
    private bool _leavingRemote;

    // RunProgram and explicit window-switch keys intentionally change the target. Bind at
    // the next action, after any recorded/manual wait for that application to become ready.
    internal void AllowNextForeground() => _acceptNextForeground = true;

    private bool IsRemote(nint window)
    {
        if (!_remoteWindowKnown || window != _remoteWindow)
        {
            _remoteWindow = window; _remoteValue = desktop.IsRemote(window); _remoteWindowKnown = true;
        }
        return _remoteValue;
    }

    internal void VerifyFocus()
    {
        if (_acceptNextForeground)
        {
            _expected = desktop.Foreground;
            _acceptNextForeground = false;
        }
        var current = desktop.Foreground;
        // Dialogs can appear asynchronously after the preceding click/key has returned.
        if (current != 0 && current != _expected && desktop.SameApplication(_expected, current)) _expected = current;
        if (_expected == 0 || current != _expected)
            throw new InvalidOperationException("回放目标失去焦点，已停止，避免把后续按键发到其他窗口。");
    }

    internal async Task FocusNamedWindowAsync(string? title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var target = desktop.FindWindow(title);
        if (target == 0) throw new InvalidOperationException($"找不到目标窗口：{title}");
        await ActivateAsync(target, () => desktop.IsWindow(target), ct);
    }

    private async Task ActivateAsync(nint target, Func<bool> unchanged, CancellationToken ct, bool allowClickActivation = false)
    {
        VerifyFocus();
        if (target == _expected) return;
        var source = _expected;
        log?.Invoke($"自动交接焦点 · 0x{source:X} → 0x{target:X}");
        await WindowActivation.EnsureForegroundAsync(source, target, () => desktop.Foreground,
            () => desktop.Activate(target), unchanged, desktop.Delay, ct, log, allowClickActivation);
        if (desktop.Foreground == target) _expected = target;
        else _clickTarget = target;
    }

    internal async Task PlaceAsync(Point point, bool clicking, CancellationToken ct, bool allowClickActivation = false)
    {
        ct.ThrowIfCancellationRequested();
        VerifyFocus();
        if (!desktop.IsOnScreen(point)) throw new InvalidOperationException("目标位置不在当前显示器上，请重新取点。");
        var source = _expected;
        _clickTarget = 0;
        _leavingRemote = false;
        var target = desktop.WindowAt(point);
        var outsideClip = desktop.OutsideClip(point);
        // Moving over a normal background window is a hover, not an activation request.
        // A captured pointer or remote console must hand off before the requested action.
        var crossing = target != 0 && target != source;
        var handoff = crossing && (clicking || outsideClip || IsRemote(source));
        if (handoff)
        {
            _leavingRemote = IsRemote(source);
            await ActivateAsync(target, () => desktop.WindowAt(point) == target, ct, allowClickActivation);
            // Remove only a clip obstructing the explicitly requested point. When activation
            // is denied, motion and the original click must complete the transition instead.
            if (desktop.OutsideClip(point) && !desktop.ReleaseClip())
                throw new InvalidOperationException("无法解除阻挡目标位置的鼠标限制，已停止操作。");
        }
        bool MoveRequested()
        {
            if (handoff && desktop.OutsideClip(point) && !desktop.ReleaseClip()) return false;
            return desktop.Move(point);
        }
        bool Unchanged() => desktop.Foreground == _expected &&
            (!clicking && !handoff || desktop.WindowAt(point) == target);
        // Keep recorded hover timing when the cursor already reaches its point normally.
        if (!clicking && !handoff && !outsideClip)
        {
            if (!desktop.Move(point)) throw new InvalidOperationException("系统拒绝移动鼠标。");
            if (PointerPlacement.IsAtTarget(desktop.Cursor, point)) return;
        }
        await PointerPlacement.MoveUntilStableAsync(point, MoveRequested, () => desktop.Cursor,
            Unchanged, desktop.Delay, ct, log);
        if (_leavingRemote)
        {
            // The console can asynchronously restore the pre-ungrab cursor. Process real
            // motion and wait before sending the one and only button action.
            await desktop.Delay(160, ct);
            if (!Unchanged()) throw new InvalidOperationException("目标窗口发生变化，已停止操作。");
            if (desktop.OutsideClip(point) && _expected == target && !desktop.ReleaseClip())
                throw new InvalidOperationException("鼠标仍被限制，已停止操作。");
            if (!PointerPlacement.IsAtTarget(desktop.Cursor, point) || desktop.OutsideClip(point))
                await PointerPlacement.MoveUntilStableAsync(point, MoveRequested, () => desktop.Cursor,
                    Unchanged, desktop.Delay, ct, log);
        }
        VerifyFocus();
        if (handoff) log?.Invoke($"跨窗口定位已验证 · ({point.X}, {point.Y})");
    }

    internal void VerifyPoint(Point point)
    {
        VerifyFocus();
        if (!PointerPlacement.IsAtTarget(desktop.Cursor, point))
            throw new InvalidOperationException("执行点击前鼠标位置发生变化，已停止操作。");
        if (_clickTarget != 0 && desktop.WindowAt(point) != _clickTarget)
            throw new InvalidOperationException("目标位置被其他窗口遮挡，已停止操作。");
    }

    internal async Task CompleteClickAsync(Point point, CancellationToken ct, bool settleAfter = true)
    {
        if (_clickTarget != 0)
        {
            var target = _clickTarget;
            // A normal mouse click can activate a window even when SetForegroundWindow
            // was denied. Do not send a second click if WM_MOUSEACTIVATE eats the first.
            for (var i = 0; i < 5 && desktop.Foreground == _expected; i++) await desktop.Delay(30, ct);
            if (desktop.Foreground != target)
                throw new InvalidOperationException("已点击一次，但目标窗口未响应；后续操作已停止。");
            _expected = target; _clickTarget = 0;
        }
        ObserveActionResult();
        if (!_leavingRemote || !settleAfter) return;
        for (var i = 0; i < 3; i++)
        {
            await desktop.Delay(60, ct);
            VerifyFocus();
            if (!PointerPlacement.IsAtTarget(desktop.Cursor, point) || desktop.OutsideClip(point))
            {
                log?.Invoke($"点击后位置回弹 · 实际 ({desktop.Cursor.X}, {desktop.Cursor.Y}) · 目标 ({point.X}, {point.Y}) · 限制 {desktop.OutsideClip(point)}");
                if (desktop.WindowAt(point) != _expected)
                    throw new InvalidOperationException("点击后目标窗口改变，后续操作已停止。");
                if (desktop.OutsideClip(point) && !desktop.ReleaseClip())
                    throw new InvalidOperationException("点击后鼠标再次被限制，后续操作已停止。");
                // Correct position only. Never duplicate a click to escape capture.
                await PointerPlacement.MoveUntilStableAsync(point, () => desktop.Move(point), () => desktop.Cursor,
                    () => desktop.Foreground == _expected && desktop.WindowAt(point) == _expected,
                    desktop.Delay, ct, log);
            }
        }
        log?.Invoke($"点击后落点已核对 · 实际 ({desktop.Cursor.X}, {desktop.Cursor.Y}) · 前台 0x{desktop.Foreground:X}");
        _leavingRemote = false;
    }

    // A click or key can open/close a dialog in the same application. Accept that result,
    // but never silently accept an unrelated application stealing focus while typing.
    internal void ObserveActionResult()
    {
        var current = desktop.Foreground;
        if (current != 0 && (current == _expected || desktop.SameApplication(_expected, current)))
            _expected = current;
        else VerifyFocus();
    }
}

internal sealed class WindowsPlaybackDesktop : IPlaybackDesktop
{
    public nint Foreground => NativeMethods.GetForegroundWindow();
    public Point Cursor => System.Windows.Forms.Cursor.Position;
    public nint WindowAt(Point point) => NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = point.X, Y = point.Y }), 2);
    public nint FindWindow(string title) => NativeMethods.FindWindowW(null, title);
    public bool IsWindow(nint window) => NativeMethods.IsWindow(window);
    public bool SameApplication(nint first, nint second)
    {
        NativeMethods.GetWindowThreadProcessId(first, out var a);
        NativeMethods.GetWindowThreadProcessId(second, out var b);
        return a != 0 && a == b;
    }
    public bool IsRemote(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var id);
        try
        {
            using var process = Process.GetProcessById(id);
            return DesktopSwitcher.IsVmwareProcess(process.ProcessName) || process.ProcessName.Equals("GameViewer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public bool IsOnScreen(Point point) => Screen.AllScreens.Any(screen => screen.Bounds.Contains(point));
    public bool OutsideClip(Point point) => NativeMethods.GetClipCursor(out var r) &&
        !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(point);
    public bool ReleaseClip() => NativeMethods.ClipCursor(0);
    public bool Activate(nint window) => NativeMethods.SetForegroundWindow(window);
    public bool Move(Point point) => WindowsPlaybackInput.MovePointer(point);
    public Task Delay(int ms, CancellationToken ct) => Task.Delay(ms, ct);
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool IsWindow(nint window);
}
