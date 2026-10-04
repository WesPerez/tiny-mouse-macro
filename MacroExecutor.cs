using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TinyMouseMacro;

public sealed class MacroExecutor
{
    private const int MaxExecutedStepsPerRun = 100_000;
    private const uint ClrInvalid = 0xFFFFFFFF;

    private readonly object _lock = new();
    private bool _isRunning;
    private CancellationTokenSource? _loopCts;
    private string? _pendingChainMacroId;
    private readonly IPlaybackDesktop _desktop;
    private readonly IPlaybackInput _input;
    public MacroExecutor() : this(new WindowsPlaybackDesktop(), new WindowsPlaybackInput()) { }
    internal MacroExecutor(IPlaybackDesktop desktop, IPlaybackInput input) { _desktop = desktop; _input = input; }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    public void Cancel()
    {
        lock (_lock) _loopCts?.Cancel();
    }

    public event Action<string>? ChainMacroRequested;

    public string? ConsumeChainMacroId()
    {
        lock (_lock)
        {
            var id = _pendingChainMacroId;
            _pendingChainMacroId = null;
            return id;
        }
    }

    public Task RunAsync(MacroProfile profile, CancellationToken cancellationToken = default, Action<string>? log = null)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return Task.CompletedTask;
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isRunning = true;
            _pendingChainMacroId = null;
        }

        var runCts = _loopCts!;
        var linkedToken = runCts.Token;

        return Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(profile, linkedToken, log);
            }
            finally
            {
                lock (_lock)
                {
                    runCts.Dispose();
                    _loopCts = null;
                    _isRunning = false;
                }
            }
        });
    }

    private async Task RunCoreAsync(MacroProfile profile, CancellationToken ct, Action<string>? log)
    {
        var repeatCount = profile.RepeatCount;
        var repeatIntervalMs = Math.Max(0, profile.RepeatIntervalMs);
        var isInfinite = repeatCount <= 0;
        var iteration = 0;
        var executedSteps = 0;
        var speedMultiplier = Math.Max(0.1, Math.Min(10.0, profile.SpeedMultiplier));
        var targetWindowTitle = profile.TargetWindowTitle?.Trim();
        var session = new PlaybackSession(_desktop, log);

        var steps = profile.Steps.ToArray();
        while (isInfinite || iteration < repeatCount)
        {
            ct.ThrowIfCancellationRequested();
            await session.FocusNamedWindowAsync(targetWindowTitle, ct);

            var timing = Stopwatch.StartNew();
            double targetTimeMs = 0;
            var previousRecorded = false;
            for (var index = 0; index < steps.Length; index++)
            {
                ct.ThrowIfCancellationRequested();

                if (++executedSteps > MaxExecutedStepsPerRun)
                {
                    throw new InvalidOperationException($"Macro stopped after {MaxExecutedStepsPerRun:N0} executed steps. Check conditional jumps for an accidental loop.");
                }

                var step = steps[index];
                if (step.RecordedDelayMs is int recordedDelay)
                {
                    if (!previousRecorded) targetTimeMs = timing.Elapsed.TotalMilliseconds;
                    targetTimeMs += recordedDelay / speedMultiplier;
                    var remaining = targetTimeMs - timing.Elapsed.TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct);
                }
                previousRecorded = step.RecordedDelayMs.HasValue;
                var result = await ExecuteStepAsync(step, speedMultiplier, session, ct);
                if (result.JumpToIndex.HasValue)
                {
                    var jumpIndex = result.JumpToIndex.Value;
                    if (jumpIndex >= 0 && jumpIndex < steps.Length)
                    {
                        index = jumpIndex - 1;
                    }
                }

                if (!step.RecordedDelayMs.HasValue) await DelayMs(20, speedMultiplier, ct);
            }

            iteration++;
            if ((isInfinite || iteration < repeatCount) && repeatIntervalMs > 0)
            {
                await DelayMs(repeatIntervalMs, speedMultiplier, ct);
            }
        }
    }

    private async Task<StepResult> ExecuteStepAsync(MacroStep step, double speedMultiplier, PlaybackSession session, CancellationToken ct)
    {
        if (step.Type is MacroStepType.Move or MacroStepType.MoveRelative or MacroStepType.LeftClick or
            MacroStepType.RightClick or MacroStepType.MiddleClick or MacroStepType.DoubleClick or MacroStepType.Drag)
            EnsureModifiersReleased();
        switch (step.Type)
        {
            case MacroStepType.Move:
                await session.PlaceAsync(new(step.X, step.Y), false, ct);
                break;
            case MacroStepType.MoveRelative:
                var currentPos = _desktop.Cursor;
                await session.PlaceAsync(new(checked(currentPos.X + step.X), checked(currentPos.Y + step.Y)), false, ct);
                break;
            case MacroStepType.WaitPixel:
                await WaitForPixelAsync(step.X, step.Y, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, ct);
                break;
            case MacroStepType.FindPixel:
                var found = await FindPixelInRegionAsync(step.X, step.Y, step.SearchWidth, step.SearchHeight, step.PixelColor, step.PixelTolerance, step.PixelTimeoutMs, ct);
                if (found.HasValue)
                    await session.PlaceAsync(new(found.Value.x, found.Value.y), false, ct);
                break;
            case MacroStepType.Screenshot:
                CaptureScreenshot(step.X, step.Y, step.SearchWidth, step.SearchHeight);
                break;
            case MacroStepType.RandomDelay:
                await DelayMs(GetRandomDelay(step.DelayMs, step.DelayMsMax), speedMultiplier, ct);
                break;
            case MacroStepType.JumpIfPixel:
                if (PixelMatches(step.X, step.Y, step.PixelColor, step.PixelTolerance) && step.JumpToStepIndex >= 0)
                {
                    return StepResult.Jump(step.JumpToStepIndex);
                }
                break;
            case MacroStepType.RunProgram:
                RunProgram(step.RunProgramPath, step.RunProgramArgs);
                session.AllowNextForeground();
                break;
            case MacroStepType.PlaySound:
                PlaySound(step.SoundFilePath);
                break;
            case MacroStepType.ChainMacro:
                RequestChainMacro(step.ChainMacroId);
                break;
            case MacroStepType.LeftClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, session, ct);
                break;
            case MacroStepType.RightClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFRightDown, NativeMethods.MouseEventFRightUp, speedMultiplier, session, ct);
                break;
            case MacroStepType.DoubleClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, session, ct, settleAfter: false);
                await DelayMs(50, speedMultiplier, ct);
                // No second placement/focus activation between the two intentional clicks.
                session.VerifyPoint(new(step.X, step.Y));
                EnsureModifiersReleased();
                await ClickAtCursorAsync(new(step.X, step.Y), NativeMethods.MouseEventFLeftDown, NativeMethods.MouseEventFLeftUp, speedMultiplier, ct);
                await session.CompleteClickAsync(new(step.X, step.Y), ct);
                break;
            case MacroStepType.MiddleClick:
                await ClickAsync(step.X, step.Y, NativeMethods.MouseEventFMiddleDown, NativeMethods.MouseEventFMiddleUp, speedMultiplier, session, ct);
                break;
            case MacroStepType.Delay:
                await DelayMs(Math.Max(0, step.DelayMs), speedMultiplier, ct);
                break;
            case MacroStepType.KeyPress:
                session.VerifyFocus(); EnsureModifiersReleased();
                await SendKeyPress(step.KeyCode, speedMultiplier, ct);
                session.ObserveActionResult();
                break;
            case MacroStepType.KeyCombo:
                session.VerifyFocus(); EnsureModifiersReleased();
                await SendKeyCombo(step.KeyModifiers, step.KeyCode, speedMultiplier, ct);
                if (IsWindowSwitch(step)) session.AllowNextForeground();
                else session.ObserveActionResult();
                break;
            case MacroStepType.TypeText:
                foreach (var c in step.TextToType ?? string.Empty)
                {
                    ct.ThrowIfCancellationRequested();
                    session.VerifyFocus(); EnsureModifiersReleased();
                    _input.Character(c);
                    await DelayMs(15, speedMultiplier, ct);
                }
                break;
            case MacroStepType.MouseWheel:
                session.VerifyFocus(); EnsureModifiersReleased();
                _input.Mouse(NativeMethods.MouseEventFWheel, unchecked((uint)step.WheelDelta));
                break;
            case MacroStepType.Drag:
                await session.PlaceAsync(new(step.X, step.Y), true, ct);
                EnsureModifiersReleased();
                session.VerifyPoint(new(step.X, step.Y));
                _input.MouseAt(new(step.X, step.Y), NativeMethods.MouseEventFLeftDown);
                try
                {
                    await DelayMs(35, speedMultiplier, ct);
                    // Dragging across windows must keep the original drag owner focused.
                    await PointerPlacement.MoveUntilStableAsync(new(step.DragEndX, step.DragEndY),
                        () => _desktop.Move(new(step.DragEndX, step.DragEndY)), () => _desktop.Cursor,
                        () => { session.VerifyFocus(); return true; }, _desktop.Delay, ct);
                    await DelayMs(35, speedMultiplier, ct);
                }
                finally { _input.Mouse(NativeMethods.MouseEventFLeftUp); }
                session.ObserveActionResult();
                break;
        }

        return StepResult.Continue;
    }

    private void RequestChainMacro(string? chainMacroId)
    {
        if (string.IsNullOrWhiteSpace(chainMacroId)) return;

        lock (_lock)
        {
            _pendingChainMacroId = chainMacroId;
        }

        ChainMacroRequested?.Invoke(chainMacroId);
    }

    private async Task ClickAsync(int x, int y, uint downFlag, uint upFlag, double speedMultiplier, PlaybackSession session, CancellationToken ct, bool settleAfter = true)
    {
        await session.PlaceAsync(new(x, y), true, ct, allowClickActivation: true);
        EnsureModifiersReleased();
        session.VerifyPoint(new(x, y));
        await ClickAtCursorAsync(new(x, y), downFlag, upFlag, speedMultiplier, ct);
        await session.CompleteClickAsync(new(x, y), ct, settleAfter);
    }

    private async Task ClickAtCursorAsync(Point point, uint downFlag, uint upFlag, double speedMultiplier, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _input.MouseAt(point, downFlag);
        try { await DelayMs(35, speedMultiplier, ct); }
        finally { _input.Mouse(upFlag); }
    }

    private static async Task DelayMs(int ms, double speedMultiplier, CancellationToken ct)
    {
        var adjusted = (int)(Math.Max(0, ms) / speedMultiplier);
        if (adjusted > 0)
            await Task.Delay(adjusted, ct);
    }

    private static int GetRandomDelay(int first, int second)
    {
        var min = Math.Max(0, Math.Min(first, second));
        var max = Math.Max(0, Math.Max(first, second));
        if (max <= min) return min;
        if (max == int.MaxValue) return Random.Shared.Next(min, max);
        return Random.Shared.Next(min, max + 1);
    }

    private void EnsureModifiersReleased()
    {
        if (_input.Modifiers != 0)
            throw new InvalidOperationException("物理修饰键仍被按住，已停止操作；请松开 Ctrl / Alt / Shift / Win。");
    }

    internal static bool IsWindowSwitch(MacroStep step) =>
        (step.KeyModifiers & 1) != 0 && step.KeyCode is (int)Keys.Tab or (int)Keys.Escape ||
        (step.KeyModifiers & 8) != 0 && step.KeyCode is (int)Keys.Tab or (int)Keys.D or (int)Keys.M;

    private async Task SendKeyPress(int keyCode, double speedMultiplier, CancellationToken ct)
    {
        if (keyCode is <= 0 or > byte.MaxValue) return;
        ct.ThrowIfCancellationRequested();
        _input.Key(keyCode, true);
        try { await DelayMs(35, speedMultiplier, ct); }
        finally { _input.Key(keyCode, false); }
    }

    private async Task SendKeyCombo(uint modifiers, int keyCode, double speedMultiplier, CancellationToken ct)
    {
        if (keyCode is <= 0 or > byte.MaxValue) return;
        var held = new List<int>();
        try
        {
            foreach (var (mask, modifier) in new[] { (2u, Keys.LControlKey), (1u, Keys.LMenu), (4u, Keys.LShiftKey), (8u, Keys.LWin) })
            {
                ct.ThrowIfCancellationRequested();
                if ((modifiers & mask) == 0) continue;
                _input.Key((int)modifier, true);
                held.Add((int)modifier);
            }
            await SendKeyPress(keyCode, speedMultiplier, ct);
        }
        finally
        {
            // Try releasing every key even if Windows rejects one release.
            Exception? releaseError = null;
            for (var i = held.Count - 1; i >= 0; i--)
                try { _input.Key(held[i], false); } catch (Exception ex) { releaseError ??= ex; }
            if (releaseError is not null) throw releaseError;
        }
    }

    private static async Task WaitForPixelAsync(int x, int y, int targetColor, int tolerance, int timeoutMs, CancellationToken ct)
    {
        if (timeoutMs <= 0) return;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (PixelMatches(x, y, targetColor, tolerance))
                return;

            await Task.Delay(50, ct);
        }
    }

    private static async Task<(int x, int y)?> FindPixelInRegionAsync(int startX, int startY, int width, int height, int targetColor, int tolerance, int timeoutMs, CancellationToken ct)
    {
        if (width <= 0 || height <= 0) return null;

        if (timeoutMs <= 0)
        {
            return FindPixelOnce(startX, startY, width, height, targetColor, tolerance);
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var found = FindPixelOnce(startX, startY, width, height, targetColor, tolerance);
            if (found.HasValue)
                return found;

            await Task.Delay(100, ct);
        }

        return null;
    }

    private static (int x, int y)? FindPixelOnce(int startX, int startY, int width, int height, int targetColor, int tolerance)
    {
        var dc = NativeMethods.GetDC(0);
        if (dc == 0) return null;

        try
        {
            for (var dy = 0; dy < height; dy++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    var pixel = NativeMethods.GetPixel(dc, startX + dx, startY + dy);
                    if (pixel != ClrInvalid && ColorMatch((int)pixel, targetColor, tolerance))
                        return (startX + dx, startY + dy);
                }
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }

        return null;
    }

    private static bool PixelMatches(int x, int y, int targetColor, int tolerance)
    {
        var dc = NativeMethods.GetDC(0);
        if (dc == 0) return false;

        try
        {
            var pixel = NativeMethods.GetPixel(dc, x, y);
            return pixel != ClrInvalid && ColorMatch((int)pixel, targetColor, tolerance);
        }
        finally
        {
            NativeMethods.ReleaseDC(0, dc);
        }
    }

    private static void CaptureScreenshot(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 7680 || height > 4320) return;

        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TinyMouseMacro");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunProgram(string? path, string? args)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var psi = new ProcessStartInfo(path, args ?? string.Empty)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(psi);
        }
        catch
        {
        }
    }

    private static void PlaySound(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            using var player = new System.Media.SoundPlayer(path);
            player.Play();
        }
        catch
        {
        }
    }

    private static bool ColorMatch(int pixel, int target, int tolerance)
    {
        var dr = Math.Abs(((pixel >> 16) & 0xFF) - ((target >> 16) & 0xFF));
        var dg = Math.Abs(((pixel >> 8) & 0xFF) - ((target >> 8) & 0xFF));
        var db = Math.Abs((pixel & 0xFF) - (target & 0xFF));
        return dr <= tolerance && dg <= tolerance && db <= tolerance;
    }

    private readonly record struct StepResult(int? JumpToIndex)
    {
        public static StepResult Continue { get; } = new(null);
        public static StepResult Jump(int index) => new(index);
    }
}
