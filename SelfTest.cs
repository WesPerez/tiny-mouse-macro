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
            Check("negative coordinates and old enum values", (int)MacroStepType.LeftClick == 1 && (int)MacroStepType.ChainMacro == 19);
            var old = JsonSerializer.Deserialize<MacroProfile>("{\"Name\":\"上\",\"Hotkey\":\"Shift+Z\",\"Steps\":[{\"Type\":1,\"X\":2272,\"Y\":-457}]}")!;
            MacroStore.NormalizeProfile(old);
            Check("legacy defaults and timing preserved", old.Enabled && old.RepeatCount == 1 && old.Steps[0].RecordedDelayMs is null && old.Steps[0].Y == -457);
            Check("legacy remote release remains opt-in", !old.ReleaseRemoteInput && !DesktopSwitcher.IsSwitchProfile(old));
            old.ReleaseRemoteInput = true;
            Check("one-point switch policy", DesktopSwitcher.IsSwitchProfile(old) && ExternalTrigger.IsEligible(old));
            Check("external launch cannot run arbitrary programs", !ExternalTrigger.IsEligible(new() { Steps = [new() { Type = MacroStepType.RunProgram }] }));
            Check("external launch cannot start loops", !ExternalTrigger.IsEligible(new() { RepeatCount = 0, Steps = [new() { Type = MacroStepType.Move }] }));
            var state = new KeyEventState();
            state.Update((int)Keys.LControlKey, true); state.Update((int)Keys.RControlKey, true); state.Update((int)Keys.LControlKey, false);
            Check("left/right modifiers independent", state.Modifiers == 2);
            state.Update((int)Keys.LShiftKey, true); Check("exact modifier chord", state.Modifiers == 6);
            Check("autorepeat suppressed", state.Update((int)Keys.F13, true) && !state.Update((int)Keys.F13, true));
            state.Update((int)Keys.F13, false); Check("next physical press allowed", state.Update((int)Keys.F13, true));
            var dedup = new TriggerDeduplicator();
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
                    Check($"layout {size.Width} tab {tab}", form.ValidatePreviewLayout(out var error));
                }
                form.VerifyPreviewInteractions(Check);
                form.TogglePreviewTheme();
                form.RenderPreview(Path.Combine(output, $"ui-{size.Width}-dark.png"), 0);
                form.TogglePreviewTheme();
            }
            for (var type = 0; type <= 19; type++)
            {
                using var dialog = new StepEditDialog(new() { Type = (MacroStepType)type, KeyCode = (int)Keys.F13, RecordedDelayMs = 640 });
                CreateHandles(dialog); dialog.PerformLayout();
                Check($"step editor preserves type {type}", (int)dialog.StepType == type);
                if (type == (int)MacroStepType.KeyCombo) Check("combo selector retains F13 and is parented", dialog.KeyComboCode == (int)Keys.F13 && FindField<ComboBox>(dialog, "_keyComboBox").Parent is not null);
                if (type is 5 or 7 or 13)
                { using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, $"step-{type}.png")); }
            }
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
        _profileList.SelectedIndex = 1;
        check("selecting scheme binds identity and steps", _nameBox.Text == "右 · UU 远程" && CurrentStep?.X == 4778);
        var first = _profiles[0];
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
    }
    internal void InitializePreview(List<MacroProfile> profiles, Size size)
    {
        if (!_preview) throw new InvalidOperationException();
        Theme.SetDark(false); Size = new Size((int)(size.Width * DeviceDpi / 96f), (int)(size.Height * DeviceDpi / 96f));
        _profiles = profiles; _profilesSource.DataSource = profiles; _profileList.DataSource = _profilesSource;
        _profileList.DisplayMember = nameof(MacroProfile.DisplayText); _profileList.SelectedIndex = 0;
        BindSelectedProfile(); UpdateSummary();
        _backendLabel.Text = "系统热键 5 · 键盘监听 就绪 · 后台输入 关闭";
        _statusLabel.Text = "就绪 · 3 个方案可用 · Ctrl+Alt+F10 停止"; _saveState.Text = "更改自动保存";
        SelfTest.CreateHandles(this); PerformAutoScale(); PerformLayout();
    }
    internal void RenderPreview(string path, int tab)
    {
        _workspaceTabs.SelectedIndex = tab; PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(path);
        File.WriteAllLines(path + ".layout.txt", Enumerate(this).Select(c => $"{c.GetType().Name} {c.Text} bounds={c.Bounds} font={c.Font.Size} dpi={c.DeviceDpi} preferred={c.PreferredSize} visible={c.Visible}"));
        static IEnumerable<Control> Enumerate(Control root) { yield return root; foreach(Control c in root.Controls) foreach(var item in Enumerate(c)) yield return item; }
    }
    internal bool ValidatePreviewLayout(out string error)
    {
        foreach (var c in new Control[] { _cancelButton, _profileList, _stepList, _recordButton, _screenMap, _hotkeyBox, _logList })
        {
            if (c.Width < 20 || c.Height < 20) { error = $"{c.Name}/{c.AccessibleName} too small {c.Bounds}"; return false; }
            if (c.Parent is FlowLayoutPanel flow && c.Right > flow.ClientSize.Width) { error = "Clipped action " + c.Text; return false; }
        }
        error = ""; return true;
    }
}
