using System.Text.Json;

namespace TinyMouseMacro;

public sealed partial class MainForm
{
    private void FlushRecording()
    {
        var count = 0;
        while (_recordingProfile is not null && _recordQueue.TryDequeue(out var step))
        { _recordingProfile.Steps.Add(step); count++; }
        if (count > 0) { _stepsSource.ResetBindings(false); _stepList.SelectedIndex = _stepList.Items.Count - 1; MarkDirty(); }
        if (_recorder?.IsRecording == true) RefreshActivityDisplay();
        if (_recorder?.LimitReached == true && _recorder.IsRecording)
        { StopRecording(); SetStatus("录制已达到 100,000 步，已自动停止并保存。"); }
    }
    private void StopRecording()
    {
        if (_recorder?.IsRecording != true) return;
        _recorder.Stop(); _recordUiTimer.Stop(); FlushRecording(); _recordClock.Stop();
        var profileName = _recordingProfile?.Name ?? "当前方案";
        _recordingProfile = null; SetCaptureEditing(true);
        if (_hotkeys is not null) _hotkeys.Suspended = false;
        if (SaveAll()) { SetStatus("录制已停止，操作与时间间隔已保存"); FinishActivity($"录制已保存 · {profileName}", "鼠标、键盘和操作间隔已追加到方案。", badge: "已保存"); }
        else { SetStatus("录制已停止，但保存失败；内容仍在内存中，请按 Ctrl+S 重试。"); FinishActivity($"录制已停止 · {profileName}", "保存失败，内容仍在内存中；请按 Ctrl+S 重试。", true); }
    }
    private void StopAll()
    { _captureCts?.Cancel(); CancelExecution(); StopRecording(); if (!IsCapturing) SetCaptureEditing(true); SaveAll(); }
    private static MacroProfile CloneProfile(MacroProfile profile)
    {
        return new() { Id = profile.Id, Name = profile.Name, Hotkey = profile.Hotkey,
            RepeatCount = profile.RepeatCount, RepeatIntervalMs = profile.RepeatIntervalMs,
            SpeedMultiplier = profile.SpeedMultiplier, TargetWindowTitle = profile.TargetWindowTitle,
            ReleaseRemoteInput = profile.ReleaseRemoteInput,
            TriggerType = profile.TriggerType, TriggerKey = profile.TriggerKey, TriggerModifiers = profile.TriggerModifiers,
            TriggerMouseButton = profile.TriggerMouseButton,
            Enabled = profile.Enabled, Steps = profile.Steps.Select(CloneStep).ToList() };
    }
    private static bool AnyModifierHeld() => PhysicalModifiers.Read() != 0;
    private void SetCaptureEditing(bool enabled)
    {
        foreach (var c in new Control[] { _nameBox, _searchBox, _profileList, _triggerTypeBox, _hotkeyBox,
            _hookKeyBox, _mouseButtonBox, _repeatCountBox, _repeatIntervalBox, _speedBox, _scheduleIntervalBox,
            _enabledCheckBox, _targetWindowBox }) c.Enabled = enabled;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SaveWindowPlacement();
            _windowPlacementTimer.Dispose();
            _uiDisposed = true;
            _saveTimer.Dispose(); _recordUiTimer.Dispose(); _scheduleTimer.Dispose(); _positionTimer.Dispose(); _activityTimer.Dispose();
            _tips.Dispose(); _trayIcon.ContextMenuStrip?.Dispose(); _trayIcon.Dispose();
            _optionsPanel.Dispose(); _diagnosticsPanel.Dispose();
            _hotkeys?.Dispose(); _recorder?.Dispose();
            _externalTrigger?.Dispose();
            _profilesSource.Dispose(); _stepsSource.Dispose();
        }
        base.Dispose(disposing);
    }
    private string PreferencesPath => Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "preferences.json");
    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
            if (prefs is null) return;
            _isBinding = true; _externalTriggerCheck.Checked = prefs.ExternalLaunchers; _isBinding = false;
            if (Theme.IsDark != prefs.Dark) ToggleTheme();
        }
        catch { _isBinding = false; }
    }
    private void SavePreferences()
    {
        if (_preview) return;
        try { File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new Preferences(Theme.IsDark, true, _externalTriggerCheck.Checked))); }
        catch (Exception ex) { SetStatus("偏好保存失败：" + ex.Message); }
    }
    private sealed record Preferences(bool Dark, bool RawInput, bool ExternalLaunchers = false);
    private void ConfigureExternalTrigger()
    {
        if (_preview || _externalTrigger is null) return;
        try { _externalTrigger.Configure(_profiles, _externalTriggerCheck.Checked); }
        catch (Exception ex) { SetStatus(ex.Message); Log(ex.Message); }
    }
    private void ExportLauncher()
    {
        if (_preview || IsCapturing || CurrentProfile is not { } p) return;
        if (!ExternalTrigger.IsEligible(p)) { SetStatus("iCUE 启动器仅支持重复 1 次、无目标窗口的一步移动／左键点击方案。"); return; }
        var source = Path.Combine(AppContext.BaseDirectory, "TinyMouseMacro.Switch.exe");
        if (!File.Exists(source)) { SetStatus("请使用完整发布目录，其中应包含 TinyMouseMacro.Switch.exe。"); return; }
        using var dialog = new SaveFileDialog { Filter = "切屏启动器 (*.exe)|*.exe", FileName = $"TinyMouseMacro.{p.Id}.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Path.GetFileNameWithoutExtension(dialog.FileName).EndsWith("." + p.Id, StringComparison.OrdinalIgnoreCase))
        { SetStatus("请保留启动器文件名末尾的方案 ID，以便识别目标方案。"); return; }
        try { File.Copy(source, dialog.FileName, true); SetStatus("已生成。可将此 exe 分配给 iCUE 的“启动应用”；使用时开启高级工具中的“允许外部启动器触发切屏”。"); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
}
