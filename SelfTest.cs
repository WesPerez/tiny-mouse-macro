using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TinyMouseMacro;

// Offline checks only: no hooks, hotkey registration, visible windows, or input injection.
internal static class SelfTest
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<string>();
        try
        {
            void Check(string name, bool pass)
            { if (!pass) throw new InvalidOperationException(name); results.Add("PASS " + name); }
            Check("hotkey parser", HotkeyParser.TryParse("Ctrl+Shift+F13", out var mods, out var key, out _) && mods == 6 && key == (uint)Keys.F13);
            const string startupPath = @"C:\Program Files\TinyMouseMacro\TinyMouseMacro.exe";
            Check("startup command contains the quoted executable path", AutoStart.BuildCommand(startupPath) == "\"C:\\Program Files\\TinyMouseMacro\\TinyMouseMacro.exe\"");
            Check("negative coordinates and old enum values", (int)MacroStepType.LeftClick == 1 && (int)MacroStepType.ChainMacro == 19);
            var old = JsonSerializer.Deserialize<MacroProfile>("{\"Name\":\"上\",\"Hotkey\":\"Shift+Z\",\"Steps\":[{\"Type\":1,\"X\":2272,\"Y\":-457}]}")!;
            MacroStore.NormalizeProfile(old);
            Check("legacy defaults and timing preserved", old.Enabled && old.RepeatCount == 1 && old.Steps[0].RecordedDelayMs is null && old.Steps[0].Y == -457);
            Check("legacy remote field preserved without controlling playback", !old.ReleaseRemoteInput);
            old.ReleaseRemoteInput = true;
            Check("one-point external switch policy", ExternalTrigger.IsEligible(old));
            Check("external launch cannot run arbitrary programs", !ExternalTrigger.IsEligible(new() { Steps = [new() { Type = MacroStepType.RunProgram }] }));
            Check("external launch cannot start loops", !ExternalTrigger.IsEligible(new() { RepeatCount = 0, Steps = [new() { Type = MacroStepType.Move }] }));
            var state = new KeyEventState();
            state.Update((int)Keys.LControlKey, true); state.Update((int)Keys.RControlKey, true); state.Update((int)Keys.LControlKey, false);
            Check("left/right modifiers independent", state.Modifiers == 2);
            state.Update((int)Keys.LShiftKey, true); Check("exact modifier chord", state.Modifiers == 6);
            Check("autorepeat suppressed", state.Update((int)Keys.F13, true) && !state.Update((int)Keys.F13, true));
            state.Update((int)Keys.F13, false); Check("next physical press allowed", state.Update((int)Keys.F13, true));
            var dedup = new TriggerDeduplicator();
            var releaseState = new KeyEventState(); releaseState.Update((int)Keys.LShiftKey, true);
            var releaseGate = new TriggerReleaseGate(releaseState.PressedModifiers);
            releaseState.Clear();
            Check("captured Shift stays pending when Windows reports no modifiers", PhysicalModifiers.Read(_ => false) == 0 && !releaseGate.IsReleased);
            releaseGate.ObserveKeyUp((int)Keys.X);
            Check("main-key release does not mistake Shift Alt for standalone Alt", !releaseGate.IsReleased);
            releaseGate.ObserveKeyUp((int)Keys.LShiftKey);
            Check("actual Shift key-up permits standalone Alt playback", releaseGate.IsReleased);
            releaseGate = new TriggerReleaseGate([(int)Keys.LShiftKey, (int)Keys.RShiftKey]);
            releaseGate.ObserveKeyUp((int)Keys.LShiftKey);
            Check("trigger release guard preserves both modifier sides", !releaseGate.IsReleased);
            releaseGate.ObserveKeyUp((int)Keys.RShiftKey);
            Check("both modifier releases complete the trigger guard", releaseGate.IsReleased);
            Check("unmodified trigger needs no key release wait", new TriggerReleaseGate([]).IsReleased);
            var forwarded = 0; var dispatched = 0; var swallowFollowing = false;
            nint Forward() { forwarded++; return 0; }
            void Dispatch(bool swallow) { dispatched++; swallowFollowing = swallow; }
            Check("ordinary registered shortcut keeps Windows hotkey route",
                HotkeyHookRoute.Handle(true, false, Forward, Dispatch) == 0 && forwarded == 1 && dispatched == 0);
            foreach (var remoteProcess in new[] { "GameViewer", "GAMEVIEWER", "vmware", "vmware-vmx", "mksSandbox" })
            {
                var before = dispatched;
                Check(remoteProcess + " registered shortcut dispatches before remote interception",
                    HotkeyHookRoute.Handle(true, DesktopSwitcher.IsRemoteProcess(remoteProcess), Forward, Dispatch) == 1 &&
                    forwarded == 1 && dispatched == before + 1 && swallowFollowing);
            }
            Check("unrelated process is not a captured console", !DesktopSwitcher.IsRemoteProcess("notepad") &&
                !DesktopSwitcher.IsRemoteProcess("GameViewerHelper") && !DesktopSwitcher.IsRemoteProcess("vmware-tools"));
            var calls = dispatched;
            Check("hook-only binding still dispatches directly", HotkeyHookRoute.Handle(false, false, Forward, Dispatch) == 1 &&
                forwarded == 1 && dispatched == calls + 1 && swallowFollowing);
            calls = dispatched;
            Check("interception before remote detection dispatches once and preserves hook result",
                HotkeyHookRoute.Handle(true, false, () => { forwarded++; return 7; }, Dispatch) == 7 &&
                forwarded == 2 && dispatched == calls + 1);
            Check("downstream interception must still receive key-up", !swallowFollowing);
            var routed = new TriggerDeduplicator(); var runs = 0;
            void RoutedDispatch(bool swallow) { if (routed.Accept("return", "hook", 500)) runs++; }
            HotkeyHookRoute.Handle(true, true, Forward, RoutedDispatch);
            if (routed.Accept("return", "raw", 500)) runs++;
            if (routed.Accept("return", "system", 501)) runs++;
            Check("remote direct path plus raw and system notifications run once", runs == 1 && forwarded == 2);
            if (routed.Accept("return", "hook", 600)) runs++;
            Check("next remote press can run again", runs == 2);

            var reconnect = new RemoteReconnectBudget();
            Check("no rehook outside remote consoles", !reconnect.Request());
            reconnect.ObserveForeground(123);
            Check("VMware entry schedules one rehook", reconnect.Request() && !reconnect.Request() && reconnect.Consume());
            Check("one later grab click can reconnect", reconnect.Request() && reconnect.Consume());
            Check("VMware clicks cannot cause hook race", !reconnect.Request() && !reconnect.Consume());
            reconnect.ObserveForeground(0); reconnect.ObserveForeground(123);
            Check("new VMware visit resets rehook budget", reconnect.Request());
            reconnect.ObserveForeground(0);
            Check("leaving VMware cancels pending rehook", !reconnect.Consume() && !reconnect.Request());
            reconnect.ObserveForeground(123);
            reconnect.Request(); reconnect.Consume(); reconnect.Request(); reconnect.Consume();
            Check("new capture restores exhausted budget", reconnect.ObserveCapture(true) && reconnect.Request() && reconnect.Consume());
            reconnect.Request(); reconnect.Consume();
            Check("continuous capture cannot reset budget", !reconnect.ObserveCapture(true) && !reconnect.Request());
            reconnect.ObserveCapture(false);
            Check("same window recapture can recover", reconnect.ObserveCapture(true) && reconnect.Request());
            reconnect.ObserveForeground(456);
            Check("VMware to UU transition drops old pending request", !reconnect.Consume());
            Check("UU visit has its own recovery budget", reconnect.Request() && reconnect.Consume() && reconnect.Request() && reconnect.Consume() && !reconnect.Request());
            reconnect.ObserveForeground(0);
            Check("returning from UU stops recovery", !reconnect.Request() && !reconnect.Consume());

            PlaybackSelfTest.Run(Check);
            var targetPoint = new Point(1500, 300); var moves = 0; var reads = 0;
            PointerPlacement.MoveUntilStableAsync(targetPoint, () => { moves++; return true; },
                () => { reads++; return moves < 2 ? new Point(999, 300) : targetPoint; },
                () => true, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Check("first move clipped then second move stable", moves == 2 && reads == 3);
            moves = 0; reads = 0;
            PointerPlacement.MoveUntilStableAsync(targetPoint, () => { moves++; return true; },
                () => { reads++; return reads == 2 ? new Point(999, 300) : targetPoint; },
                () => true, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Check("temporary arrival must remain stable", moves == 2 && reads == 4);
            moves = 0; var blocked = false;
            try { PointerPlacement.MoveUntilStableAsync(targetPoint, () => { moves++; return true; }, () => new Point(999, 300),
                () => true, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("permanent clipping aborts within six moves", blocked && moves == 6);
            moves = 0; blocked = false;
            try { PointerPlacement.MoveUntilStableAsync(targetPoint, () => { moves++; return true; }, () => targetPoint,
                () => false, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("changed foreground cancels before moving", blocked && moves == 0);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); blocked = false;
                try { PointerPlacement.MoveUntilStableAsync(targetPoint, () => { moves++; return true; }, () => targetPoint,
                    () => true, (_, _) => Task.CompletedTask, cancelled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { blocked = true; }
                Check("cancelled switch does not move", blocked && moves == 0);
            }
            var activity = Path.Combine(output, "activity-test.log");
            nint focused = 11; var focusRequests = 0; var focusSamples = 0;
            WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                () => { focusRequests++; return true; }, () => true,
                (_, _) => { focusSamples++; if (focusSamples == 3) focused = 22; return Task.CompletedTask; },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("focus transfer waits for actual foreground not API success", focusRequests == 3 && focusSamples == 4);
            focused = 11; focusRequests = 0; blocked = false;
            try { WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                () => { focusRequests++; return true; }, () => true, (_, _) => Task.CompletedTask,
                CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("unchanged VMware focus aborts before any click", blocked && focusRequests == 6);
            focused = 11; focusRequests = 0; focusSamples = 0;
            WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                () => { focusRequests++; focused = 22; return true; }, () => true,
                (_, _) => { focusSamples++; if (focusSamples == 2) focused = 11; return Task.CompletedTask; },
                CancellationToken.None).GetAwaiter().GetResult();
            Check("transient target focus must stabilize", focusRequests == 2 && focusSamples == 4);
            focused = 11; focusRequests = 0; blocked = false;
            try { WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                () => { focusRequests++; return true; }, () => true,
                (_, _) => { focused = 33; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("other foreground cancels instead of fighting user", blocked && focusRequests == 1);
            focused = 11; focusRequests = 0; blocked = false;
            try { WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                () => { focusRequests++; return true; }, () => false, (_, _) => Task.CompletedTask,
                CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { blocked = true; }
            Check("changed target aborts before requesting focus", blocked && focusRequests == 0);
            using (var cancelledFocus = new CancellationTokenSource())
            {
                cancelledFocus.Cancel(); blocked = false;
                try { WindowActivation.EnsureForegroundAsync(11, 22, () => focused,
                    () => { focusRequests++; return true; }, () => true, (_, _) => Task.CompletedTask,
                    cancelledFocus.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { blocked = true; }
                Check("cancelled focus handoff does not activate", blocked && focusRequests == 0);
            }
            File.WriteAllText(activity, new string('x', ActivityLog.MaximumBytes));
            ActivityLog.Append(activity, "test\nentry");
            Check("activity log rotates with one backup", File.Exists(activity + ".previous") && new FileInfo(activity).Length < 500 && File.ReadAllText(activity).Contains("test entry"));
            Check("multiple input paths deduplicated", dedup.Accept("a", "hook", 1234) && !dedup.Accept("a", "raw", 1234) && !dedup.Accept("a", "system", 1235));
            Check("quick second press allowed", dedup.Accept("a", "hook", 1250));
            dedup.Clear(); Check("native timestamp wrap", dedup.Accept("a", "hook", uint.MaxValue - 5) && !dedup.Accept("a", "raw", 5));
            var timeline = new RecordingTimeline(); var first = new MacroStep(); var second = new MacroStep();
            timeline.Stamp(first, 100); timeline.Stamp(second, 740);
            Check("record real inter-event timing", first.RecordedDelayMs == 100 && second.RecordedDelayMs == 640);
            Check("INPUT native union size", Marshal.SizeOf<NativeMethods.Input>() == (IntPtr.Size == 8 ? 40 : 28));
            Check("recording clock format", $"{TimeSpan.FromSeconds(75):mm\\:ss}" == "01:15");
            for (var i = 0; i < 5; i++)
            {
                using var input = new InputThread("offline lifecycle test");
                var ran = false; input.Invoke(() => ran = true);
                Check($"message thread lifecycle {i + 1}", ran);
            }
            var store = new MacroStore(Path.Combine(output, "test-profiles.json"));
            old.Steps.Add(new() { Type = MacroStepType.KeyCombo, KeyCode = (int)Keys.C, KeyModifiers = 2, RecordedDelayMs = 640 });
            old.ScheduleNextRun = DateTime.Now.AddMinutes(5); var scheduled = old.ScheduleNextRun;
            store.Save([old]); var reloaded = store.Load()[0];
            Check("save roundtrip retains old and recorded steps", reloaded.Steps[0].RecordedDelayMs is null && reloaded.Steps[1].RecordedDelayMs == 640 && reloaded.Steps[1].KeyModifiers == 2);
            Check("saving does not reset active schedule", old.ScheduleNextRun == scheduled);
            var executor = new MacroExecutor();
            for (var i = 0; i < 30; i++)
            {
                var run = executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.Delay, DelayMs = 1000 }] });
                executor.Cancel();
                try { run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                Check($"immediate cancellation {i + 1}", !executor.IsRunning);
            }
            // Delay-only profiles exercise the real scheduler without touching mouse or keyboard.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.Delay, DelayMs = 0, RecordedDelayMs = 100 }, new() { Type = MacroStepType.Delay, DelayMs = 0, RecordedDelayMs = 150 }] }).GetAwaiter().GetResult();
            Check("recorded playback timing", timer.ElapsedMilliseconds >= 230 && timer.ElapsedMilliseconds < 600);
            Check("progress tracks actual executor step and iteration", executor.Progress is { Step: 2, StepCount: 2, Iteration: 1, RepeatCount: 1 });
            var fixture = new List<MacroProfile>
            {
                new() { Name = "上 · 虚拟机", Hotkey = "Shift+Z", Steps = [new() { Type = MacroStepType.LeftClick, X = 2272, Y = -457 }] },
                new() { Name = "右 · UU 远程", Hotkey = "Shift+X", Steps = [new() { Type = MacroStepType.LeftClick, X = 4778, Y = 1335 }] },
                new() { Name = "中 · 本机", Hotkey = "Alt+Z", Steps = [new() { Type = MacroStepType.LeftClick, X = 2333, Y = 288 }] },
                new() { Name = "录制示例", Hotkey = "Ctrl+Shift+F8", Steps = [new() { Type = MacroStepType.Move, X = 2272, Y = -457, RecordedDelayMs = 100 }, new() { Type = MacroStepType.LeftClick, X = 4778, Y = 1335, RecordedDelayMs = 820 }] }
            };
            foreach (var size in new[] { new Size(1200, 860), new Size(1080, 700) })
            {
                using var form = new MainForm(preview: true);
                form.InitializePreview(fixture, size);
                for (var tab = 0; tab < 3; tab++)
                {
                    form.RenderPreview(Path.Combine(output, $"ui-{size.Width}-{tab}.png"), tab);
                    var validLayout = form.ValidatePreviewLayout(out var error);
                    Check($"layout {size.Width} tab {tab}: {error}", validLayout);
                }
                form.VerifyPreviewInteractions(Check);
                form.VerifyExecutionLifecycle(Check);
                form.PreviewRunning();
                form.RenderPreview(Path.Combine(output, $"ui-{size.Width}-running.png"), 0);
                form.PreviewRecording();
                form.RenderPreview(Path.Combine(output, $"ui-{size.Width}-recording.png"), 0);
                form.PreviewFinished();
                form.TogglePreviewTheme();
                form.RenderPreview(Path.Combine(output, $"ui-{size.Width}-dark.png"), 0);
                form.TogglePreviewTheme();
            }
            var dialogTimings = new List<string>();
            for (var type = 0; type <= 19; type++)
            {
                var dialogClock = System.Diagnostics.Stopwatch.StartNew();
                using var dialog = new StepEditDialog(new() { Type = (MacroStepType)type, KeyCode = (int)Keys.F13, KeyModifiers = 3, DelayMs = 1250, DelayMsMax = 2750, RecordedDelayMs = 640 });
                var constructedMs = dialogClock.Elapsed.TotalMilliseconds;
                CreateHandles(dialog); dialog.PerformLayout();
                dialogTimings.Add($"{type}: constructor {constructedMs:0.0} ms; with handles {dialogClock.Elapsed.TotalMilliseconds:0.0} ms");
                Check($"step editor preserves type {type}", (int)dialog.StepType == type);
                if (type == (int)MacroStepType.KeyCombo) Check("combo selector retains F13 and is parented", dialog.KeyComboCode == (int)Keys.F13 && FindField<ComboBox>(dialog, "_keyComboBox").Parent is not null);
                if (type == (int)MacroStepType.KeyCombo) Check("compact modifiers retain Ctrl and Alt", dialog.KeyModifiers == 3);
                if (type == (int)MacroStepType.Delay) Check("seconds editor preserves millisecond precision", dialog.DelayMs == 1250 && dialog.DelayMsMax == 2750 && dialog.RecordedDelayMs == 640);
                if (type == (int)MacroStepType.KeyPress)
                {
                    var capture = FindField<KeyCaptureBox>(dialog, "_keyCapture");
                    capture.CaptureForTest(Keys.Menu);
                    Check("direct key capture selects left Alt", dialog.KeyCode == (int)Keys.LMenu);
                    capture.CaptureForTest(Keys.Menu, 1L << 24);
                    Check("direct key capture distinguishes right Alt", dialog.KeyCode == (int)Keys.RMenu);
                    // A new capture field has no held keys from the previous fixture.
                    using (var comboDialog = new StepEditDialog(new() { Type = MacroStepType.KeyPress }))
                    {
                        var comboCapture = FindField<KeyCaptureBox>(comboDialog, "_keyCapture");
                        comboCapture.CaptureForTest(Keys.ControlKey);
                        comboCapture.CaptureForTest(Keys.ShiftKey);
                        comboCapture.CaptureForTest(Keys.X);
                        Check("direct chord capture changes action type and retains Ctrl Shift X",
                            comboDialog.StepType == MacroStepType.KeyCombo && comboDialog.KeyComboCode == (int)Keys.X && comboDialog.KeyModifiers == 6);
                    }
                    var selector = FindField<ComboBox>(dialog, "_keyPressBox");
                    selector.SelectedItem = Keys.Menu;
                    Check("single Alt is selectable and clearly named", dialog.KeyCode == (int)Keys.Menu && selector.GetItemText(selector.SelectedItem) == "Alt");
                    var category = FindField<ComboBox>(dialog, "_categoryBox");
                    category.SelectedIndex = 2;
                    Check("category picker filters to delays without changing stored enum", dialog.StepType == MacroStepType.Delay && FindField<ComboBox>(dialog, "_typeBox").Items.Count == 2);
                    category.SelectedIndex = 1;
                    Check("returning to keyboard category retains chosen Alt", dialog.StepType == MacroStepType.KeyPress && dialog.KeyCode == (int)Keys.Menu);
                }
                if (type is 5 or 6 or 7 or 13)
                { using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, $"step-{type}.png")); }
            }
            File.WriteAllLines(Path.Combine(output, "dialog-timings.txt"), dialogTimings);
            File.WriteAllLines(Path.Combine(output, "results.txt"), results);
            return 0;
        }
        catch (Exception ex)
        {
            results.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), results); return 1;
        }
    }
    internal static void CreateHandles(Control control)
    { _ = control.Handle; foreach (Control child in control.Controls) CreateHandles(child); }
    private static T FindField<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
}

public sealed partial class MainForm
{
    internal void TogglePreviewTheme() => ToggleTheme();
    internal void VerifyPreviewInteractions(Action<string, bool> check)
    {
        PreviewRunning();
        var runningName = _activityTitle.Text;
        _profileList.SelectedIndex = 2;
        SetStatus("已自动保存"); RefreshActivityDisplay();
        check("running identity survives selection and routine status messages", _activityTitle.Text == runningName && _activityBadge.Text == "运行中" && _cancelButton.Enabled && !_runButton.Enabled);
        CancelExecution();
        check("stop requested is visible until execution actually ends", _activityBadge.Text == "停止中");
        PreviewFinished();
        check("finished task remains visible with disabled stop", _activityTitle.Text.Contains("已完成") && !_cancelButton.Enabled && _runButton.Enabled);
        _globalPaused = true; RefreshActivityDisplay();
        check("paused automation is visibly distinct from idle", _activityBadge.Text == "已暂停" && !_runButton.Enabled);
        _globalPaused = false;
        PreviewRecording();
        check("recording is visible and identifies the destination scheme", _activityBadge.Text == "录制中" && _activityDetail.Text.Contains("鼠标、键盘") && _recordButton.Text.Contains("停止录制"));
        PreviewFinished();
        BeginActivity(ActivityMode.Running, _profiles[1]);
        check("next queued or chained scheme replaces running identity", _activityTitle.Text == _profiles[1].Name && _profileList.ActiveProfileId == _profiles[1].Id);
        FinishActivity("未完成 · " + _profiles[1].Name, "目标窗口未能切换", true);
        SetStatus("就绪");
        check("failure remains visible after routine status updates", _activityBadge.Text == "未完成" && _activityDetail.Text == "目标窗口未能切换");
        BeginActivity(ActivityMode.Countdown, _profiles[0], "3 秒后开始录制");
        check("recording countdown offers stop before recording starts", _cancelButton.Enabled && _recordButton.Enabled && !_runButton.Enabled && _activityBadge.Text == "准备录制");
        PreviewFinished();
        check("recording defaults include keyboard", (bool)typeof(RecorderService).GetMethod("Start")!.GetParameters()[0].DefaultValue!);
        _profileList.SelectedIndex = 1;
        check("selecting scheme binds identity and steps", _nameBox.Text == "右 · UU 远程" && CurrentStep?.X == 4778);
        var first = _profiles[0];
        var legacy = CurrentProfile!;
        var savedType = legacy.TriggerType; var savedShortcut = legacy.Hotkey;
        legacy.TriggerType = MacroTriggerType.KeyboardHook; legacy.TriggerKey = (int)Keys.X; legacy.TriggerModifiers = 4;
        BindSelectedProfile();
        legacy.TriggerMouseButton = 0;
        _triggerTypeBox.SelectedIndex = 1;
        check("switching to mouse trigger stores the displayed default X1", legacy.TriggerMouseButton == 4);
        _triggerTypeBox.SelectedIndex = 0;
        check("legacy keyboard chord survives mouse and keyboard selection", legacy.Hotkey == "SHIFT+X");
        legacy.TriggerType = savedType; legacy.Hotkey = savedShortcut; BindSelectedProfile();
        _startingRecording = true;
        var count = _profiles.Count; var steps = CurrentProfile!.Steps.Count;
        DuplicateProfile(); DeleteStep(); DuplicateStep(); MoveStep(1); AddCustomStep();
        check("recording prevents profile and step mutation", _profiles.Count == count && CurrentProfile.Steps.Count == steps);
        _startingRecording = false;
        _searchBox.Text = "Alt+Z";
        check("search by shortcut", _profileList.Items.Count == 1 && CurrentProfile?.Name == "中 · 本机");
        _searchBox.Clear();
        check("clearing search restores all schemes", _profileList.Items.Count == _profiles.Count);
        _profileList.SelectedIndex = 0;
        var original = CurrentStep!.X; SetTargetPoint(new(original + 1, -457));
        check("map edits selected point without executing input", CurrentStep!.X == original + 1);
        SetTargetPoint(new(original, -457));
        var snapshot = CloneProfile(CurrentProfile!);
        SetTargetPoint(new(original + 2, -457));
        check("execution snapshot keeps verified target", snapshot.Steps[0].X == original && CurrentStep!.X == original + 2);
        SetTargetPoint(new(original, -457));

        // A private, hidden preview window tests receipt without invoking any real macro or launcher.
        _diagnosticCheck.Checked = true;
        _externalTriggerCheck.Checked = true;
        _externalTrigger = new ExternalTrigger(Handle);
        _externalTrigger.Configure(_profiles, true);
        var message = NativeMethods.RegisterWindowMessage("TinyMouseMacro.Run." + CurrentProfile!.Id);
        check("local launcher receives acknowledgement", NativeMethods.SendMessageW(Handle, (int)message, 0, 0) == 1);
        _externalTriggerCheck.Checked = false;
        check("disabled launcher rejects queued message", NativeMethods.SendMessageW(Handle, (int)message, 0, 0) == 0);
        _externalTrigger.Dispose(); _externalTrigger = null;
        _diagnosticCheck.Checked = false;
        _diagnosticCheck.Checked = true;
        SelectPreviewPanel(0);
        check("leaving diagnostics restores execution", !_diagnosticCheck.Checked);
    }
    internal void InitializePreview(List<MacroProfile> profiles, Size size)
    {
        if (!_preview) throw new InvalidOperationException();
        Theme.SetDark(false); Size = new Size((int)(size.Width * DeviceDpi / 96f), (int)(size.Height * DeviceDpi / 96f));
        _profiles = profiles; _profilesSource.DataSource = profiles; _profileList.DataSource = _profilesSource;
        _profileList.DisplayMember = nameof(MacroProfile.DisplayText); _profileList.SelectedIndex = 0;
        BindSelectedProfile(); UpdateSummary();
        _backendLabel.Text = "3 个方案可用";
        _statusLabel.Text = "就绪 · 3 个方案可用 · Ctrl+Alt+F10 停止"; _saveState.Text = "更改自动保存";
        SelfTest.CreateHandles(this); PerformAutoScale(); PerformLayout();
    }
    internal void RenderPreview(string path, int tab)
    {
        SelectPreviewPanel(tab); PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(path);
        File.WriteAllLines(path + ".layout.txt", Enumerate(this).Select(c => $"{c.GetType().Name} {c.Text} bounds={c.Bounds} font={c.Font.Size} dpi={c.DeviceDpi} preferred={c.PreferredSize} visible={c.Visible}"));
        static IEnumerable<Control> Enumerate(Control root) { yield return root; foreach(Control c in root.Controls) foreach(var item in Enumerate(c)) yield return item; }
    }
    internal void PreviewRunning() => BeginActivity(ActivityMode.Running, _profiles[0]);
    internal void PreviewRecording() => BeginActivity(ActivityMode.Recording, _profiles[3]);
    internal void PreviewFinished() => FinishActivity($"已完成 · {_profiles[0].Name}", "本次运行已结束，等待下一次触发。");
    internal void VerifyExecutionLifecycle(Action<string, bool> check)
    {
        // Only delays and a link to another private delay fixture: no desktop input.
        var follow = new MacroProfile { Name = "后续等待", Steps = [new() { Type = MacroStepType.Delay, DelayMs = 80 }] };
        var first = new MacroProfile { Name = "前置等待", Steps = [new() { Type = MacroStepType.Delay, DelayMs = 80 }, new() { Type = MacroStepType.ChainMacro, ChainMacroId = follow.Id }] };
        _profiles.Add(follow);
        var release = new TriggerReleaseGate([(int)Keys.LShiftKey]);
        var gatedRun = RunAndDrainAsync(follow, release);
        check("playback waits for captured Shift before starting even with zero Windows state", !gatedRun.IsCompleted && !_executor.IsRunning);
        release.ObserveKeyUp((int)Keys.LShiftKey);
        PumpUntil(() => gatedRun.IsCompleted); gatedRun.GetAwaiter().GetResult();
        check("captured Shift release starts the ordered playback", _activityTitle.Text.Contains("已完成"));
        gatedRun = RunAndDrainAsync(follow, new TriggerReleaseGate([(int)Keys.LShiftKey]));
        CancelExecution(); PumpUntil(() => gatedRun.IsCompleted); gatedRun.GetAwaiter().GetResult();
        check("stop during trigger-release wait cancels before executor starts", !_executor.IsRunning && _activityTitle.Text == "已停止 · 后续等待");
        var run = RunAndDrainAsync(first);
        check("real execution shows running state before completion", _activityMode == ActivityMode.Running && _activityName == first.Name && _cancelButton.Enabled);
        PumpUntil(() => run.IsCompleted);
        run.GetAwaiter().GetResult();
        check("real chain finishes with final scheme identity", _activityMode == ActivityMode.Idle && _activityTitle.Text.Contains(follow.Name) && !_cancelButton.Enabled && _runningProfileId is null);
        var longDelay = new MacroProfile { Name = "可停止等待", Steps = [new() { Type = MacroStepType.Delay, DelayMs = 10000 }] };
        run = RunAndDrainAsync(longDelay); CancelExecution();
        check("real cancellation first displays stopping", _activityMode == ActivityMode.Stopping);
        PumpUntil(() => run.IsCompleted); run.GetAwaiter().GetResult();
        check("real cancellation finishes without retaining a running badge", _activityMode == ActivityMode.Idle && _activityTitle.Text == "已停止 · 可停止等待" && !_cancelButton.Enabled);
        run = RunAndDrainAsync(follow); PumpUntil(() => run.IsCompleted); run.GetAwaiter().GetResult();
        check("new execution after stop clears cancellation state", !_stopRequested && _activityTitle.Text.Contains("已完成"));
        _profiles.Remove(follow); PreviewFinished();
        static void PumpUntil(Func<bool> condition)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (!condition())
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException("offline execution lifecycle");
                Application.DoEvents(); Thread.Sleep(1);
            }
        }
    }
    private void SelectPreviewPanel(int page)
    {
        if (page != 2) _diagnosticCheck.Checked = false;
        var panel = page switch { 1 => _optionsPanel, 2 => _diagnosticsPanel, _ => _composePanel };
        foreach (var item in new[] { _composePanel, _optionsPanel, _diagnosticsPanel }) item.Visible = item == panel;
        panel.BringToFront();
        SelfTest.CreateHandles(panel);
    }
    internal bool ValidatePreviewLayout(out string error)
    {
        var fields = new Control[] { _nameBox.Parent!, _triggerTypeBox, _hotkeyBox.Parent!, _mouseButtonBox };
        static int TopInForm(Control field)
        { var top = 0; for (Control? current = field; current is not null && current is not Form; current = current.Parent) top += current.Top; return top; }
        var expected = TopInForm(_triggerTypeBox);
        foreach (var field in fields)
            if (field.Height != _triggerTypeBox.Height || TopInForm(field) != expected)
            { error = $"Identity field is not aligned: {field.GetType().Name} {field.Bounds}"; return false; }
        if (_statusLabel.BackColor != Color.Transparent || _saveState.BackColor != Color.Transparent)
        { error = "Footer labels must share their container background"; return false; }
        foreach (var c in new Control[] { _cancelButton, _runButton, _activityTitle, _activityBadge, _themeButton, _profileList, _stepList, _recordButton, _screenMap, _hotkeyBox, _logList })
        {
            if (c.Width < 20 || c.Height < 20) { error = $"{c.Name}/{c.AccessibleName} too small {c.Bounds}"; return false; }
            if (c.Parent is FlowLayoutPanel flow && c.Right > flow.ClientSize.Width) { error = "Clipped action " + c.Text; return false; }
        }
        error = ""; return true;
    }
}
