namespace TinyMouseMacro;

public sealed partial class MainForm : Form
{
    private const int MaxChainDepth = 32;
    private const string ThemeRoleAccentButton = "AccentButton";
    private const string ThemeRoleDangerButton = "DangerButton";
    private const string ThemeRoleSectionHeader = "SectionHeader";
    private const string ThemeRoleStatus = "Status";
    private const string ThemeRoleMonoLabel = "MonoLabel";
    private const string ThemeRoleMonoTextBox = "MonoTextBox";

    private readonly MacroStore _store;
    private readonly MacroExecutor _executor = new();
    private readonly BindingSource _profilesSource = new();
    private readonly BindingSource _stepsSource = new();
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _scheduleTimer = new() { Interval = 10000 };
    private CancellationTokenSource? _executionCts;

    private HotkeyService? _hotkeys;
    private RecorderService? _recorder;
    private List<MacroProfile> _profiles = [];
    private bool _isBinding;
    private bool _dirty;
    private bool _globalPaused;
    private int _dragStepIndex = -1;
    private MacroProfile? _recordingProfile;

    private readonly ListBox _profileList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly ComboBox _triggerTypeBox = new();
    private readonly TextBox _hookKeyBox = new();
    private readonly ComboBox _mouseButtonBox = new();
    private readonly NumericUpDown _repeatCountBox = new() { Minimum = 0, Maximum = 9999, Increment = 1 };
    private readonly NumericUpDown _repeatIntervalBox = new() { Minimum = 0, Maximum = 600000, Increment = 100 };
    private readonly CheckBox _enabledCheckBox = new();
    private readonly TextBox _targetWindowBox = new();
    private readonly NumericUpDown _speedBox = new() { Minimum = 0.1m, Maximum = 10.0m, Increment = 0.1m, DecimalPlaces = 1 };
    private readonly NumericUpDown _scheduleIntervalBox = new() { Minimum = 0, Maximum = 1440, Increment = 1 };
    private readonly ListBox _stepList = new();
    private readonly Label _livePositionLabel = new();
    private readonly Label _statusLabel = new();
    private readonly ListBox _logList = new();
    private readonly TextBox _searchBox = new();
    private readonly Button _cancelButton;
    private readonly NotifyIcon _trayIcon;
    private bool _reallyClosing;

    public MainForm(bool preview = false)
    {
        _preview = preview;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = UiText.AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1200, 860);
        Font = Theme.BodyFont;
        KeyPreview = true;
        Theme.ApplyToForm(this);

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyMouseMacro");
        Directory.CreateDirectory(appDataDir);
        _store = new MacroStore(Path.Combine(appDataDir, "macros.json"));
        _positionTimer.Tick += (_, _) => UpdateLivePosition();
        _scheduleTimer.Tick += ScheduleTimer_Tick;

        var showItem = new ToolStripMenuItem(UiText.TrayShow);
        showItem.Click += (_, _) => ShowFromTray();

        var autoStartItem = new ToolStripMenuItem(UiText.TrayAutoStart);
        autoStartItem.Checked = AutoStart.IsEnabled();
        autoStartItem.Click += (_, _) =>
        {
            var enable = !autoStartItem.Checked;
            if (AutoStart.SetEnabled(enable))
            {
                autoStartItem.Checked = enable;
            }
        };

        var exitItem = new ToolStripMenuItem(UiText.TrayExit);
        exitItem.Click += (_, _) => { _reallyClosing = true; Close(); };

        _trayIcon = new NotifyIcon
        {
            Icon = TryLoadIcon(),
            Text = UiText.AppTitle,
            Visible = !preview,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add(showItem);
        _trayIcon.ContextMenuStrip.Items.Add(autoStartItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);
        _trayIcon.MouseDoubleClick += (_, _) => ShowFromTray();

        _cancelButton = new Button
        {
            Text = UiText.CancelExecution,
            Width = 110,
            Height = 32,
            Visible = false,
            Tag = ThemeRoleDangerButton,
            Margin = new Padding(4)
        };
        Theme.StyleDangerButton(_cancelButton);

        BuildUi();
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;

        if (!preview)
        {
            _externalTrigger = new ExternalTrigger(Handle);
            _hotkeys = new HotkeyService();
            _hotkeys.Triggered += signal => PostToUi(() =>
            {
                _lastSignal.Text = $"{DateTime.Now:HH:mm:ss}  {signal.Shortcut} · {signal.Source}";
                Log(_lastSignal.Text + (signal.Diagnostic ? " · 仅检测" : ""));
                if (signal.Diagnostic || _diagnosticCheck.Checked || _globalPaused || IsCapturing) return;
                var profile = _profiles.FirstOrDefault(p => p.Id == signal.ProfileId && p.Enabled);
                if (profile is not null) RunMacro(profile);
            });
            _hotkeys.EmergencyStop += () => { _executor.Cancel(); PostToUi(StopAll); };
            _hotkeys.PauseRequested += () => PostToUi(ToggleGlobalPause);
            _hotkeys.DiagnosticMessage += message => PostToUi(() => { _lastSignal.Text = message; Log(message); });
        }

        _executor.ChainMacroRequested += chainId =>
        {
            PostToUi(() =>
            {
                var target = _profiles.FirstOrDefault(p => p.Id == chainId);
                if (target is not null && target.Enabled)
                {
                    SetStatus(UiText.Chaining(target.Name));
                }
            });
        };

        if (!preview)
        {
            _recorder = new RecorderService();
            _recorder.StepRecorded += step => _recordQueue.Enqueue(step);
        }
        _cancelButton.Click += (_, _) => StopAll();
        KeyDown += MainForm_KeyDown;
        if (!preview) LoadPreferences();
        // All layout dimensions above are 96-DPI units. Scale after controls exist,
        // rather than scaling an empty form before building the control tree.
        AutoScaleDimensions = new SizeF(96, 96);
        PerformAutoScale();
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_hotkeyBox.Focused || _hookKeyBox.Focused) return;
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; StopAll(); return; }
        if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = e.SuppressKeyPress = true;
            SaveAndReload();
        }
        else if (e.KeyCode == Keys.Delete && _stepList.Focused)
        {
            e.Handled = true;
            DeleteStep();
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int wmShowWindow = NativeMethods.WmUser + 1;

        if (m.Msg == wmShowWindow)
        {
            ShowFromTray();
            return;
        }

        if (_externalTrigger?.TryGetProfile(unchecked((uint)m.Msg), out var profileId) == true)
        {
            if (!_externalTriggerCheck.Checked || !_profiles.Any(p => p.Id == profileId && p.Enabled && ExternalTrigger.IsEligible(p)))
            { m.Result = 0; return; }
            PostToUi(() =>
            {
                var profile = _profiles.FirstOrDefault(p => p.Id == profileId && p.Enabled);
                if (!_externalTriggerCheck.Checked || profile is null || !ExternalTrigger.IsEligible(profile)) return;
                _lastSignal.Text = $"{DateTime.Now:HH:mm:ss}  {profile.Name} · iCUE / 本地启动器";
                Log(_lastSignal.Text);
                if (!_diagnosticCheck.Checked) RunMacro(profile);
            });
            m.Result = 1; // Acknowledge receipt, not completion of the screen switch.
            return;
        }

        base.WndProc(ref m);
    }

    private void MainForm_Load(object? sender, EventArgs e)
    {
        _profiles = _store.Load();
        _profilesSource.DataSource = _profiles;
        _profileList.DataSource = _profilesSource;
        _profileList.DisplayMember = nameof(MacroProfile.DisplayText);
        _profileList.SelectedIndex = _profiles.Count > 0 ? 0 : -1;

        BindSelectedProfile();
        RebuildHotkeys();
        if (!_preview) { _positionTimer.Start(); _scheduleTimer.Start(); }
        _saveState.Text = "更改自动保存";
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing && !_preview)
        {
            _diagnosticCheck.Checked = false;
            e.Cancel = true;
            StopAll();
            if (!SaveAll(force: true)) return;
            _positionTimer.Stop();
            Hide();
            return;
        }

        StopAll();
        if (!SaveAll(force: true)) { e.Cancel = true; _reallyClosing = false; return; }
        _saveTimer.Dispose();
        _recordUiTimer.Dispose();
        _scheduleTimer.Dispose();
        _positionTimer.Dispose();

        _hotkeys?.Dispose();
        _recorder?.Dispose();
        _trayIcon.Dispose();
    }

    private void ToggleGlobalPause()
    {
        _globalPaused = !_globalPaused;
        if (_hotkeys is not null) _hotkeys.Paused = _globalPaused;
        _pauseButton.Text = _globalPaused ? "恢复全部快捷键" : "暂停全部快捷键";
        if (_globalPaused)
        {
            StopAll();
            SetStatus(UiText.GlobalPaused);
            _trayIcon.Text = UiText.AppTitle + UiText.PausedSuffix;
        }
        else
        {
            SetStatus(UiText.GlobalResumed);
            _trayIcon.Text = UiText.AppTitle;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (!_preview) _positionTimer.Start();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private MacroProfile? CurrentProfile => _profileList.SelectedItem as MacroProfile;
    private MacroStep? CurrentStep => _stepList.SelectedItem as MacroStep;

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e) => CaptureShortcut(e, false);

    private void CaptureShortcut(KeyEventArgs e, bool direct)
    {
        e.Handled = e.SuppressKeyPress = true;
        if (CurrentProfile is not { } profile || HotkeyParser.IsModifierKey(e.KeyCode)) return;
        if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None) { _profileList.Focus(); return; }
        if (e.KeyCode == Keys.Back && e.Modifiers == Keys.None)
        {
            if (direct) { profile.TriggerKey = 0; profile.TriggerModifiers = 0; _hookKeyBox.Clear(); }
            else { profile.Hotkey = ""; _hotkeyBox.Clear(); }
            MarkDirty(); return;
        }
        var shortcut = HotkeyParser.FromKeyEvent(e);
        if (!HotkeyParser.TryParse(shortcut, out var modifiers, out var key, out _) || IsHotkeyUsedByAnotherProfile(profile, shortcut))
        { SetStatus("快捷键与其他方案或停止／暂停功能重复，请重新设置"); return; }
        if (direct) { profile.TriggerKey = (int)key; profile.TriggerModifiers = modifiers; _hookKeyBox.Text = shortcut; }
        else { profile.Hotkey = shortcut; _hotkeyBox.Text = shortcut; }
        MarkDirty();
    }

    private void TriggerTypeBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_isBinding || CurrentProfile is null) return;

        CurrentProfile.TriggerType = _triggerTypeBox.SelectedIndex switch
        {
            0 => MacroTriggerType.KeyboardHotkey,
            1 => MacroTriggerType.KeyboardHook,
            2 => MacroTriggerType.MouseButton,
            _ => MacroTriggerType.KeyboardHotkey
        };

        _hotkeyBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.KeyboardHotkey;
        _hookKeyBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.KeyboardHook;
        _mouseButtonBox.Visible = CurrentProfile.TriggerType == MacroTriggerType.MouseButton;
        MarkDirty();
    }

    private void HookKeyBox_KeyDown(object? sender, KeyEventArgs e) => CaptureShortcut(e, true);

    private void BindSelectedProfile()
    {
        _isBinding = true;
        try
        {
            var profile = CurrentProfile;
            _nameBox.Text = profile?.Name ?? string.Empty;
            _hotkeyBox.Text = profile?.Hotkey ?? string.Empty;
            _triggerTypeBox.SelectedIndex = profile?.TriggerType switch
            {
                MacroTriggerType.KeyboardHotkey => 0,
                MacroTriggerType.KeyboardHook => 1,
                MacroTriggerType.MouseButton => 2,
                _ => 0
            };
            _hookKeyBox.Text = profile?.TriggerKey > 0 ? HotkeyParser.Normalize(profile.TriggerModifiers, (uint)profile.TriggerKey) : string.Empty;
            _mouseButtonBox.SelectedIndex = profile?.TriggerMouseButton switch
            {
                4 => 0,
                5 => 1,
                3 => 2,
                _ => 0
            };
            _repeatCountBox.Value = Math.Min(_repeatCountBox.Maximum, Math.Max(_repeatCountBox.Minimum, profile?.RepeatCount ?? 1));
            _repeatIntervalBox.Value = Math.Min(_repeatIntervalBox.Maximum, Math.Max(_repeatIntervalBox.Minimum, profile?.RepeatIntervalMs ?? 0));
            _enabledCheckBox.Checked = profile?.Enabled ?? true;
            _scheduleIntervalBox.Value = Math.Min(_scheduleIntervalBox.Maximum, Math.Max(_scheduleIntervalBox.Minimum, profile?.ScheduleIntervalMinutes ?? 0));
            _targetWindowBox.Text = profile?.TargetWindowTitle ?? string.Empty;
            _speedBox.Value = Math.Min(_speedBox.Maximum, Math.Max(_speedBox.Minimum, (decimal)(profile?.SpeedMultiplier ?? 1.0)));

            _hotkeyBox.Visible = profile?.TriggerType == MacroTriggerType.KeyboardHotkey;
            _hookKeyBox.Visible = profile?.TriggerType == MacroTriggerType.KeyboardHook;
            _mouseButtonBox.Visible = profile?.TriggerType == MacroTriggerType.MouseButton;

            _stepsSource.DataSource = profile?.Steps ?? [];
            _stepsSource.ResetBindings(false);
            UpdateSummary();
        }
        finally
        {
            _isBinding = false;
        }
    }

    private void StepList_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var step = CurrentStep;
        if (step is null || IsCapturing) return;

        using var dialog = new StepEditDialog(step);
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.Font = Theme.BodyFont;
        Theme.ApplyToForm(dialog);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ApplyStepEdit(step, dialog);
            RefreshSteps();
            _stepList.SelectedItem = step;
            MarkDirty();
            SetStatus(UiText.EditedStep(step.Display));
        }
    }

    private static void ApplyStepEdit(MacroStep step, StepEditDialog dialog)
    {
            step.RecordedDelayMs = dialog.RecordedDelayMs;
            step.Type = dialog.StepType;
            step.X = dialog.X;
            step.Y = dialog.Y;
            step.DelayMs = dialog.DelayMs;
            step.DragEndX = dialog.DragEndX;
            step.DragEndY = dialog.DragEndY;
            step.WheelDelta = dialog.WheelDelta;
            step.KeyCode = step.Type == MacroStepType.KeyCombo ? dialog.KeyComboCode : dialog.KeyCode;
            step.KeyModifiers = dialog.KeyModifiers;
            step.TextToType = dialog.TextToType;
            step.PixelColor = dialog.PixelColor;
            step.PixelTolerance = dialog.PixelTolerance;
            step.PixelTimeoutMs = dialog.PixelTimeoutMs;
            step.SearchWidth = dialog.SearchWidth;
            step.SearchHeight = dialog.SearchHeight;
            step.DelayMsMax = dialog.DelayMsMax;
            step.JumpToStepIndex = dialog.JumpToStepIndex;
            step.RunProgramPath = dialog.RunProgramPath;
            step.RunProgramArgs = dialog.RunProgramArgs;
            step.SoundFilePath = dialog.SoundFilePath;
            step.ChainMacroId = dialog.ChainMacroId;
    }

    private void StepList_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || IsCapturing) return;
        _dragOrigin = e.Location;
        _dragStepIndex = _stepList.IndexFromPoint(e.Location);
        if (_dragStepIndex < 0) return;

    }

    private void StepList_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragStepIndex < 0 || IsCapturing) return;
        var size = SystemInformation.DragSize;
        if (new Rectangle(_dragOrigin.X - size.Width / 2, _dragOrigin.Y - size.Height / 2, size.Width, size.Height).Contains(e.Location)) return;
        _stepList.DoDragDrop(_stepList.Items[_dragStepIndex], DragDropEffects.Move);
        _dragStepIndex = -1;
    }

    private void StepList_DragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.Move;
    }

    private void StepList_DragDrop(object? sender, DragEventArgs e)
    {
        var profile = CurrentProfile;
        if (profile is null || _dragStepIndex < 0) return;

        var point = _stepList.PointToClient(new Point(e.X, e.Y));
        var targetIndex = _stepList.IndexFromPoint(point);
        if (targetIndex < 0 || targetIndex >= profile.Steps.Count || targetIndex == _dragStepIndex) return;

        var step = profile.Steps[_dragStepIndex];
        profile.Steps.RemoveAt(_dragStepIndex);
        profile.Steps.Insert(targetIndex, step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
        _dragStepIndex = -1;
    }

    private void AddProfile()
    {
        if (IsCapturing) return;
        _searchBox.Clear();
        var profile = new MacroProfile { Name = $"{UiText.Macro} {_profiles.Count + 1}", Hotkey = GetNextDefaultHotkey(), ReleaseRemoteInput = true, Steps = [] };
        _profiles.Add(profile);
        _profilesSource.ResetBindings(false);
        _profileList.SelectedItem = profile;
        MarkDirty();
        SetStatus(UiText.AddedProfile);
    }

    private void DuplicateProfile()
    {
        if (IsCapturing) return;
        var current = CurrentProfile;
        if (current is null) return;

        var copy = new MacroProfile
        {
            Name = current.Name + UiText.CopySuffix,
            Hotkey = GetNextDefaultHotkey(),
            TriggerType = current.TriggerType,
            TriggerKey = current.TriggerKey,
            TriggerModifiers = current.TriggerModifiers,
            TriggerMouseButton = current.TriggerMouseButton,
            RepeatCount = current.RepeatCount,
            RepeatIntervalMs = current.RepeatIntervalMs,
            ReleaseRemoteInput = current.ReleaseRemoteInput,
            Enabled = false,
            TargetWindowTitle = current.TargetWindowTitle,
            SpeedMultiplier = current.SpeedMultiplier,
            ScheduleIntervalMinutes = current.ScheduleIntervalMinutes,
            Steps = current.Steps.Select(CloneStep).ToList()
        };

        _searchBox.Clear();
        _profiles.Add(copy);
        _profilesSource.ResetBindings(false);
        _profileList.SelectedItem = copy;
        MarkDirty();
        SetStatus("已复制方案，副本默认停用；设置快捷键后在运行选项启用");
    }

    private void DeleteProfile()
    {
        var current = CurrentProfile;
        if (current is null) return;

        if (IsCapturing) return;
        if (MessageBox.Show(this, $"删除方案“{current.Name}”？", "删除方案", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _searchBox.Clear();
        _profiles.Remove(current);
        if (_profiles.Count == 0)
        {
            _profiles.Add(new MacroProfile { Name = $"{UiText.Macro} 1", Hotkey = GetNextDefaultHotkey(), ReleaseRemoteInput = true });
        }

        _profilesSource.ResetBindings(false);
        _profileList.SelectedIndex = 0;
        MarkDirty();
        SetStatus(UiText.DeletedProfile);
    }

    private void ImportProfile()
    {
        if (IsCapturing) return;
        using var dialog = new OpenFileDialog
        {
            Title = UiText.ImportTitle,
            Filter = "JSON (*.json)|*.json|All (*.*)|*.*",
            DefaultExt = "json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<List<MacroProfile>>(json);
            if (imported is not { Count: > 0 })
            {
                SetStatus(UiText.ImportEmpty);
                return;
            }

            var normalized = MacroStore.NormalizeProfiles(imported);
            if (normalized.GroupBy(p => p.Id).Any(g => g.Count() > 1)) throw new InvalidDataException("导入文件有重复方案 ID，请先修正后再导入。");
            var idMap = normalized.ToDictionary(p => p.Id, _ => Guid.NewGuid().ToString("N"));
            foreach (var profile in normalized)
            {
                profile.Id = idMap[profile.Id];
                profile.Enabled = false;
                foreach (var step in profile.Steps)
                    if (idMap.TryGetValue(step.ChainMacroId, out var replacement)) step.ChainMacroId = replacement;
                if (string.IsNullOrWhiteSpace(profile.Hotkey))
                    profile.Hotkey = GetNextDefaultHotkey();
            }
            _profiles.AddRange(normalized);
            _searchBox.Clear();
            _profilesSource.ResetBindings(false);
            _profileList.SelectedIndex = _profiles.Count - 1;
            MarkDirty();
            SetStatus(UiText.Imported(normalized.Count));
        }
        catch (Exception ex)
        {
            SetStatus(UiText.ImportFailed(ex.Message));
        }
    }

    private void ExportProfile()
    {
        if (IsCapturing) return;
        var current = CurrentProfile;
        if (current is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = UiText.ExportTitle,
            Filter = "JSON (*.json)|*.json",
            DefaultExt = "json",
            FileName = $"{current.Name}.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { current }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            SetStatus(UiText.Exported(dialog.FileName));
        }
        catch (Exception ex)
        {
            SetStatus(UiText.ExportFailed(ex.Message));
        }
    }

    private void ToggleTheme()
    {
        Theme.Toggle();
        Theme.ApplyToForm(this);
        foreach (Control control in Controls)
        {
            ReapplyTheme(control);
        }
        _profileList.ItemHeight = (int)(64 * DeviceDpi / 96f); _stepList.ItemHeight = (int)(48 * DeviceDpi / 96f); _screenMap.Invalidate();
        _profileList.Invalidate();
        SavePreferences();
        SetStatus(Theme.IsDark ? UiText.DarkTheme : UiText.LightTheme);
    }

    private void ScheduleTimer_Tick(object? sender, EventArgs e)
    {
        if (_preview || _globalPaused || _executor.IsRunning || IsCapturing || _diagnosticCheck.Checked) return;
        var now = DateTime.Now;
        foreach (var profile in _profiles)
        {
            if (!profile.Enabled || profile.ScheduleIntervalMinutes <= 0) continue;
            var next = profile.ScheduleNextRun;
            if (next is null) { profile.ScheduleNextRun = now.AddMinutes(profile.ScheduleIntervalMinutes); continue; }
            if (now >= next.Value)
            {
                profile.ScheduleNextRun = now.AddMinutes(profile.ScheduleIntervalMinutes);
                RunMacro(profile);
                break;
            }
        }
    }

    private static void ReapplyTheme(Control control)
    {
        var tag = control.Tag as string;
        if (control is Button btn)
        {
            if (tag == ThemeRoleDangerButton) Theme.StyleDangerButton(btn);
            else if (tag == ThemeRoleAccentButton) Theme.StyleAccentButton(btn);
            else Theme.StyleButton(btn);
        }
        else if (control is TextBoxBase tb)
        {
            Theme.StyleTextBox(tb);
            if (tag == ThemeRoleMonoTextBox) tb.Font = Theme.MonoBodyFont;
        }
        else if (control is ListBox lb)
        {
            Theme.StyleListBox(lb);
        }
        else if (control is ComboBox cb)
        {
            Theme.StyleComboBox(cb);
        }
        else if (control is NumericUpDown nud)
        {
            Theme.StyleNumericUpDown(nud);
        }
        else if (control is Label lbl)
        {
            if (tag == "Title") { lbl.Font = Theme.TitleFont; lbl.ForeColor = Theme.CurrentTextPrimary; }
            else if (tag == "Body") { lbl.Font = Theme.BodyFont; lbl.ForeColor = Theme.CurrentTextSecondary; }
            else if (tag == ThemeRoleSectionHeader) Theme.StyleSectionHeader(lbl);
            else if (tag == ThemeRoleStatus) Theme.StyleStatusBar(lbl);
            else if (tag == ThemeRoleMonoLabel) Theme.StyleMonoLabel(lbl);
            else Theme.StyleLabel(lbl);
        }
        else if (control is TableLayoutPanel or FlowLayoutPanel or Panel or TabPage or TabControl)
        {
            Theme.StylePanel(control);
        }

        foreach (Control child in control.Controls)
        {
            ReapplyTheme(child);
        }
    }

    private async Task CapturePointAfterDelayAsync(bool addClickStep, MacroStepType stepType = MacroStepType.LeftClick)
    {
        if (_preview || IsCapturing || _executor.IsRunning || CurrentProfile is not { } profile) return;
        using var cts = new CancellationTokenSource(); _captureCts = cts;
        SetCaptureEditing(false);
        if (_hotkeys is not null) _hotkeys.Suspended = true;
        try
        {
            for (var i = 3; i >= 1; i--) { SetStatus($"{i} 秒后读取鼠标位置；不会实际点击。停止按钮可取消。"); await Task.Delay(1000, cts.Token); }
            var point = Cursor.Position;
            if (_profiles.Contains(profile))
            {
                profile.Steps.Add(new() { Type = addClickStep ? stepType : MacroStepType.Move, X = point.X, Y = point.Y });
                RefreshSteps(); MarkDirty(); SetStatus($"已添加目标 ({point.X}, {point.Y})");
            }
        }
        catch (OperationCanceledException) { SetStatus("已取消取点"); }
        finally { _captureCts = null; SetCaptureEditing(true); if (_hotkeys is not null) _hotkeys.Suspended = false; SaveAll(); }
    }

    private void AddDelayStep()
    {
        AddStep(new MacroStep { Type = MacroStepType.Delay, DelayMs = 500 });
    }

    private async void ToggleRecording()
    {
        if (_preview || _recorder is null) return;
        if (_recorder.IsRecording || _startingRecording) { StopAll(); return; }
        if (_executor.IsRunning || _captureCts is not null || CurrentProfile is not { } profile) return;
        _startingRecording = true; _recordingProfile = profile;
        SetCaptureEditing(false);
        if (_hotkeys is not null) _hotkeys.Suspended = true;
        using var cts = new CancellationTokenSource(); _captureCts = cts;
        try
        {
            for (var i = 3; i >= 1; i--) { _recordButton.Text = $"取消倒计时 {i}"; SetStatus($"{i} 秒后录制；Ctrl+Alt+F10 停止。将追加到当前方案。"); await Task.Delay(1000, cts.Token); }
            _recorder.Start(_recordKeyboardCheck.Checked, Handle);
            _recordClock.Restart(); _recordUiTimer.Start();
            _profileList.Enabled = false; _workspaceTabs.Enabled = true;
            _recordButton.Text = "■  停止录制"; SetStatus("正在录制操作与真实间隔 · Ctrl+Alt+F10 停止");
        }
        catch (OperationCanceledException) { _recordingProfile = null; }
        catch (Exception ex) { _recordingProfile = null; SetStatus(ex.Message); Log(ex.Message); }
        finally
        {
            _captureCts = null; _startingRecording = false;
            if (!_recorder.IsRecording) { SetCaptureEditing(true); _recordButton.Text = "●  录制操作"; if (_hotkeys is not null) _hotkeys.Suspended = false; SaveAll(); }
        }
    }

    private void AddStep(MacroStep step)
    {
        var profile = CurrentProfile;
        if (profile is null || IsCapturing) return;

        profile.Steps.Add(step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
        SetStatus(UiText.AddedStep(step.Display));
    }

    private void MoveStepUp() => MoveStep(-1);
    private void MoveStepDown() => MoveStep(1);

    private void MoveStep(int direction)
    {
        if (IsCapturing) return;
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        var oldIndex = profile.Steps.IndexOf(step);
        var newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= profile.Steps.Count) return;

        profile.Steps.RemoveAt(oldIndex);
        profile.Steps.Insert(newIndex, step);
        RefreshSteps();
        _stepList.SelectedItem = step;
        MarkDirty();
    }

    private void DeleteStep()
    {
        if (IsCapturing) return;
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        profile.Steps.Remove(step);
        RefreshSteps();
        MarkDirty();
        SetStatus(UiText.DeletedStep);
    }

    private void DuplicateStep()
    {
        if (IsCapturing) return;
        var profile = CurrentProfile;
        var step = CurrentStep;
        if (profile is null || step is null) return;

        var index = profile.Steps.IndexOf(step);
        if (index < 0) return;
        var copy = CloneStep(step);
        profile.Steps.Insert(index + 1, copy);
        RefreshSteps();
        _stepList.SelectedItem = copy;
        MarkDirty();
        SetStatus(UiText.DuplicatedStep);
    }

    private void FilterProfiles()
    {
        if (IsCapturing) return;
        var filter = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            _profilesSource.DataSource = _profiles;
        }
        else
        {
            _profilesSource.DataSource = _profiles
                .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.ShortcutText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        _profilesSource.ResetBindings(false);
        if (_profileList.Items.Count > 0)
            _profileList.SelectedIndex = 0;
    }

    private void TestRun()
    {
        if (_preview || IsCapturing || _diagnosticCheck.Checked) return;
        var profile = CurrentProfile;
        if (profile is null) return;
        if (profile.RepeatCount == 0 && _hotkeys?.StopAvailable != true)
        { SetStatus("停止快捷键不可用，暂不执行持续循环。请先处理快捷键冲突。"); return; }

        if (_executor.IsRunning || _executionCts is not null)
        {
            SetStatus(UiText.AlreadyRunning);
            return;
        }

        SetStatus(UiText.Testing(profile.Name));
        _ = RunWithCancellationAsync(profile);
    }

    private void SaveAndReload()
    {
        SaveAll(); RebuildHotkeys(true);
    }
    private bool SaveAll(bool force = false)
    {
        _saveTimer.Stop();
        if (!_dirty || _preview) return true;
        if (IsCapturing && !force) return false;
        try { _store.Save(_profiles); _dirty = false; _saveState.Text = "已自动保存"; RebuildHotkeys(); return true; }
        catch (Exception ex)
        {
            _saveState.Text = "保存失败 · Ctrl+S 重试"; SetStatus(ex.Message); Log(ex.Message);
            _saveTimer.Interval = 5000; _saveTimer.Start(); return false;
        }
    }
    private void MarkDirty()
    {
        if (_preview) return;
        _dirty = true; _saveState.Text = "正在编辑…"; _profileList.Invalidate(); UpdateSummary();
        _saveTimer.Stop(); _saveTimer.Interval = 700; if (!IsCapturing) _saveTimer.Start();
    }
    private void RebuildHotkeys() => RebuildHotkeys(false);
    private void RebuildHotkeys(bool force)
    {
        ConfigureExternalTrigger();
        if (_hotkeys is null || IsCapturing || _hotkeyBox.Focused || _hookKeyBox.Focused) return;
        var errors = _hotkeys.RegisterAll(_profiles, force: force);
        _backendLabel.Text = _hotkeys.BackendStatus;
        foreach (var error in errors) Log(error);
        SetStatus(errors.Count == 0 ? $"就绪 · {_hotkeys.RegisteredCount} 个方案可用 · Ctrl+Alt+F10 停止" : string.Join(" | ", errors));
    }

    private void RunMacro(MacroProfile profile)
    {
        if (_preview || IsCapturing || _globalPaused || _diagnosticCheck.Checked) return;
        if (profile.RepeatCount == 0 && _hotkeys?.StopAvailable != true)
        { SetStatus("停止快捷键不可用，暂不执行持续循环。请先在诊断页处理快捷键冲突。"); return; }
        if (_executor.IsRunning || _executionCts is not null)
        {
            SetStatus(UiText.AlreadyRunning);
            return;
        }

        SetStatus(UiText.Running(profile.Name));
        Log(UiText.Running(profile.Name));
        _ = RunWithCancellationAsync(profile);
    }

    private async Task RunWithCancellationAsync(MacroProfile profile)
    {
        var current = CloneProfile(profile);
        for (var chainDepth = 0; chainDepth < MaxChainDepth; chainDepth++)
        {
            using var cts = new CancellationTokenSource();
            _executionCts = cts;
            _cancelButton.Enabled = true;
            var completed = false;
            var canceled = false;

            try
            {
                // Do not turn the triggering Shift/Ctrl/Alt chord into a modified click.
                var releaseDeadline = Environment.TickCount64 + 1500;
                while (AnyModifierHeld())
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= releaseDeadline) throw new InvalidOperationException("请先松开 Ctrl / Alt / Shift / Win，再运行方案");
                    await Task.Delay(10, cts.Token);
                }
                await _executor.RunAsync(current, cts.Token, message => PostToUi(() => Log(message)));
                completed = true;
                PostToUi(() =>
                {
                    SetStatus(UiText.Finished(current.Name));
                    Log(UiText.Finished(current.Name));
                });
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                PostToUi(() =>
                {
                    SetStatus(UiText.Cancelled(current.Name));
                    Log(UiText.Cancelled(current.Name));
                });
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    SetStatus(ex.GetBaseException().Message);
                    Log($"{UiText.Error}: {ex.GetBaseException().Message}");
                });
            }
            finally
            {
                if (ReferenceEquals(_executionCts, cts))
                    _executionCts = null;
            }

            if (!completed || canceled)
            {
                PostToUi(() => _cancelButton.Enabled = true);
                return;
            }

            var chainId = _executor.ConsumeChainMacroId();
            if (string.IsNullOrWhiteSpace(chainId))
            {
                PostToUi(() => _cancelButton.Enabled = true);
                return;
            }

            var target = _profiles.FirstOrDefault(p => p.Id == chainId);
            if (target is null || !target.Enabled || target.Id == current.Id)
            {
                PostToUi(() =>
                {
                    SetStatus(UiText.ChainSkipped(current.Name));
                    Log(UiText.ChainSkipped(current.Name));
                    _cancelButton.Enabled = true;
                });
                return;
            }

            PostToUi(() =>
            {
                SetStatus(UiText.Chaining(target.Name));
                Log(UiText.Chaining(target.Name));
            });
            current = CloneProfile(target);
        }

        PostToUi(() =>
        {
            SetStatus(UiText.ChainSkipped(current.Name));
            Log(UiText.ChainSkipped(current.Name));
            _cancelButton.Enabled = true;
        });
    }

    private void CancelExecution()
    {
        _executionCts?.Cancel();
        _executor.Cancel();
    }

    private void RefreshSteps()
    {
        var selected = CurrentStep;
        UpdateSummary();
        _stepsSource.DataSource = null;
        _stepsSource.DataSource = CurrentProfile?.Steps ?? [];
        _stepsSource.ResetBindings(false);
        if (selected is not null)
        {
            _stepList.SelectedItem = selected;
        }
    }

    private void UpdateLivePosition()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        var point = Cursor.Position;
        _livePositionLabel.Text = $"鼠标  {point.X}, {point.Y}";
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logList.Items.Insert(0, $"[{timestamp}] {message}");
        while (_logList.Items.Count > 200)
            _logList.Items.RemoveAt(_logList.Items.Count - 1);
        if (!_preview) ActivityLog.Append(Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "activity.log"), message);
    }

    private static MacroStep CloneStep(MacroStep step)
    {
        return new MacroStep
        {
            RecordedDelayMs = step.RecordedDelayMs,
            Type = step.Type,
            X = step.X,
            Y = step.Y,
            DelayMs = step.DelayMs,
            DragEndX = step.DragEndX,
            DragEndY = step.DragEndY,
            WheelDelta = step.WheelDelta,
            KeyCode = step.KeyCode,
            KeyModifiers = step.KeyModifiers,
            TextToType = step.TextToType,
            PixelColor = step.PixelColor,
            PixelTolerance = step.PixelTolerance,
            PixelTimeoutMs = step.PixelTimeoutMs,
            SearchWidth = step.SearchWidth,
            SearchHeight = step.SearchHeight,
            DelayMsMax = step.DelayMsMax,
            JumpToStepIndex = step.JumpToStepIndex,
            RunProgramPath = step.RunProgramPath,
            RunProgramArgs = step.RunProgramArgs,
            SoundFilePath = step.SoundFilePath,
            ChainMacroId = step.ChainMacroId
        };
    }

    private static Icon TryLoadIcon()
    {
        try
        {
            var exePath = Application.ExecutablePath;
            if (File.Exists(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Icon.ExtractAssociatedIcon(exePath)!;
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private string GetNextDefaultHotkey()
    {
        var used = _profiles.Select(static p => p.ShortcutText.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        used.Add(HotkeyService.StopShortcut); used.Add(HotkeyService.PauseShortcut);
        var candidates = new[] { "Alt+Z", "Alt+X", "Alt+C", "Alt+V", "Ctrl+Alt+Z", "Ctrl+Alt+X", "Ctrl+Alt+C", "Ctrl+Alt+V" };
        foreach (var candidate in candidates)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        for (var i = 1; i <= 12; i++)
        {
            var candidate = $"Ctrl+Alt+F{i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private bool IsHotkeyUsedByAnotherProfile(MacroProfile currentProfile, string hotkey)
    {
        if (!HotkeyParser.TryParse(hotkey, out var modifiers, out var key, out _))
        {
            return false;
        }

        var normalized = HotkeyParser.Normalize(modifiers, key);
        if (normalized == HotkeyParser.Normalize(3, (uint)Keys.F10) || normalized == HotkeyParser.Normalize(3, (uint)Keys.P)) return true;
        foreach (var profile in _profiles)
        {
            if (profile.Id == currentProfile.Id || profile.TriggerType == MacroTriggerType.MouseButton || !HotkeyParser.TryParse(profile.ShortcutText, out var otherModifiers, out var otherKey, out _))
            {
                continue;
            }

            if (HotkeyParser.Normalize(otherModifiers, otherKey).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class StepEditDialog : Form
{
    private readonly ComboBox _typeBox = new();
    private readonly TableLayoutPanel _fields;
    private readonly NumericUpDown _recordedDelayBox = new() { Minimum = 0, Maximum = 86400000 };
    private readonly bool _hasRecordedTiming;
    private bool _dialogScaled;
    public int? RecordedDelayMs => _hasRecordedTiming ? (int)_recordedDelayBox.Value : null;
    private readonly NumericUpDown _xBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _yBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _delayBox = new() { Minimum = 0, Maximum = 600000, Increment = 50 };
    private readonly NumericUpDown _dragEndXBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _dragEndYBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _wheelDeltaBox = new() { Minimum = -12000, Maximum = 12000, Increment = 120 };
    private readonly ComboBox _keyPressBox = new();
    private readonly ComboBox _keyComboBox = new();
    private readonly CheckedListBox _modifierCheckList = new();
    private readonly TextBox _typeTextBox = new();
    private readonly NumericUpDown _pixelColorBox = new() { Minimum = 0, Maximum = 0xFFFFFF, Hexadecimal = true };
    private readonly NumericUpDown _pixelToleranceBox = new() { Minimum = 0, Maximum = 255, Increment = 5 };
    private readonly NumericUpDown _pixelTimeoutBox = new() { Minimum = 0, Maximum = 60000, Increment = 500 };
    private readonly NumericUpDown _searchWBox = new() { Minimum = 1, Maximum = 3840 };
    private readonly NumericUpDown _searchHBox = new() { Minimum = 1, Maximum = 2160 };
    private readonly NumericUpDown _delayMaxBox = new() { Minimum = 0, Maximum = 600000, Increment = 50 };
    private readonly NumericUpDown _jumpToStepBox = new() { Minimum = 0, Maximum = 9999 };
    private readonly TextBox _runProgramPathBox = new();
    private readonly TextBox _runProgramArgsBox = new();
    private readonly TextBox _soundFilePathBox = new();
    private readonly TextBox _chainMacroIdBox = new();
    private readonly Label _xLabel;
    private readonly Label _yLabel;
    private readonly Label _delayLabel;
    private readonly Label _dragEndXLabel;
    private readonly Label _dragEndYLabel;
    private readonly Label _wheelLabel;
    private readonly Label _keyLabel;
    private readonly Label _modifierLabel;
    private readonly Label _textLabel;
    private readonly Label _pixelColorLabel;
    private readonly Label _pixelToleranceLabel;
    private readonly Label _pixelTimeoutLabel;
    private readonly Label _searchWLabel;
    private readonly Label _searchHLabel;
    private readonly Label _delayMaxLabel;
    private readonly Label _jumpToStepLabel;
    private readonly Label _runProgramPathLabel;
    private readonly Label _runProgramArgsLabel;
    private readonly Label _soundFileLabel;
    private readonly Label _chainMacroIdLabel;

    public MacroStepType StepType => _typeBox.SelectedIndex switch
    {
        0 => MacroStepType.Move,
        1 => MacroStepType.LeftClick,
        2 => MacroStepType.RightClick,
        3 => MacroStepType.DoubleClick,
        4 => MacroStepType.MiddleClick,
        5 => MacroStepType.Delay,
        6 => MacroStepType.KeyPress,
        7 => MacroStepType.KeyCombo,
        8 => MacroStepType.TypeText,
        9 => MacroStepType.MouseWheel,
        10 => MacroStepType.Drag,
        11 => MacroStepType.MoveRelative,
        12 => MacroStepType.WaitPixel,
        13 => MacroStepType.FindPixel,
        14 => MacroStepType.Screenshot,
        15 => MacroStepType.RandomDelay,
        16 => MacroStepType.JumpIfPixel,
        17 => MacroStepType.RunProgram,
        18 => MacroStepType.PlaySound,
        19 => MacroStepType.ChainMacro,
        _ => MacroStepType.Move
    };

    public int X => (int)_xBox.Value;
    public int Y => (int)_yBox.Value;
    public int DelayMs => (int)_delayBox.Value;
    public int DragEndX => (int)_dragEndXBox.Value;
    public int DragEndY => (int)_dragEndYBox.Value;
    public int WheelDelta => (int)_wheelDeltaBox.Value;
    public int KeyCode => _keyPressBox.SelectedIndex >= 0 ? (int)(Keys)_keyPressBox.SelectedItem! : 0;
    public int KeyComboCode => _keyComboBox.SelectedIndex >= 0 ? (int)(Keys)_keyComboBox.SelectedItem! : 0;
    public uint KeyModifiers
    {
        get
        {
            uint mods = 0;
            for (var i = 0; i < _modifierCheckList.Items.Count; i++)
            {
                if (_modifierCheckList.GetItemChecked(i))
                {
                    mods |= i switch
                    {
                        0 => NativeMethods.ModControl,
                        1 => NativeMethods.ModAlt,
                        2 => NativeMethods.ModShift,
                        3 => NativeMethods.ModWin,
                        _ => 0
                    };
                }
            }
            return mods;
        }
    }
    public string TextToType => _typeTextBox.Text;
    public int PixelColor => (int)_pixelColorBox.Value;
    public int PixelTolerance => (int)_pixelToleranceBox.Value;
    public int PixelTimeoutMs => (int)_pixelTimeoutBox.Value;
    public int SearchWidth => (int)_searchWBox.Value;
    public int SearchHeight => (int)_searchHBox.Value;
    public int DelayMsMax => (int)_delayMaxBox.Value;
    public int JumpToStepIndex => (int)_jumpToStepBox.Value - 1;
    public string RunProgramPath => _runProgramPathBox.Text;
    public string RunProgramArgs => _runProgramArgsBox.Text;
    public string SoundFilePath => _soundFilePathBox.Text;
    public string ChainMacroId => _chainMacroIdBox.Text;

    public StepEditDialog(MacroStep step)
    {
        Text = UiText.EditStepTitle;
        _hasRecordedTiming = step.RecordedDelayMs.HasValue;
        Size = new Size(540, 620);
        Font = Theme.BodyFont;
        Theme.ApplyToForm(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        AutoScroll = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        Controls.Add(root);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        root.Controls.Add(scroll, 0, 0);
        var panel = _fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 22 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 22; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scroll.Controls.Add(panel);

        _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _typeBox.Items.AddRange([
            UiText.TypeMove, UiText.TypeLeftClick, UiText.TypeRightClick,
            UiText.TypeDoubleClick, UiText.TypeMiddleClick, UiText.TypeDelay,
            UiText.TypeKeyPress, UiText.TypeKeyCombo, UiText.TypeTypeText,
            UiText.TypeMouseWheel, UiText.TypeDrag, UiText.TypeMoveRelative,
            UiText.TypeWaitPixel, UiText.TypeFindPixel, UiText.TypeScreenshot,
            UiText.TypeRandomDelay, UiText.TypeJumpIfPixel, UiText.TypeRunProgram,
            UiText.TypePlaySound, UiText.TypeChainMacro
        ]);
        _typeBox.SelectedIndex = step.Type switch
        {
            MacroStepType.Move => 0,
            MacroStepType.LeftClick => 1,
            MacroStepType.RightClick => 2,
            MacroStepType.DoubleClick => 3,
            MacroStepType.MiddleClick => 4,
            MacroStepType.Delay => 5,
            MacroStepType.KeyPress => 6,
            MacroStepType.KeyCombo => 7,
            MacroStepType.TypeText => 8,
            MacroStepType.MouseWheel => 9,
            MacroStepType.Drag => 10,
            MacroStepType.MoveRelative => 11,
            MacroStepType.WaitPixel => 12,
            MacroStepType.FindPixel => 13,
            MacroStepType.Screenshot => 14,
            MacroStepType.RandomDelay => 15,
            MacroStepType.JumpIfPixel => 16,
            MacroStepType.RunProgram => 17,
            MacroStepType.PlaySound => 18,
            MacroStepType.ChainMacro => 19,
            _ => 0
        };
        _typeBox.Dock = DockStyle.Fill;

        Theme.StyleComboBox(_typeBox);

        _xBox.Value = Math.Min(_xBox.Maximum, Math.Max(_xBox.Minimum, step.X));
        _xBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_xBox);

        _yBox.Value = Math.Min(_yBox.Maximum, Math.Max(_yBox.Minimum, step.Y));
        _yBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_yBox);

        _delayBox.Value = Math.Min(_delayBox.Maximum, Math.Max(_delayBox.Minimum, step.DelayMs));
        _delayBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_delayBox);

        _dragEndXBox.Value = Math.Min(_dragEndXBox.Maximum, Math.Max(_dragEndXBox.Minimum, step.DragEndX));
        _dragEndXBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_dragEndXBox);

        _dragEndYBox.Value = Math.Min(_dragEndYBox.Maximum, Math.Max(_dragEndYBox.Minimum, step.DragEndY));
        _dragEndYBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_dragEndYBox);

        _wheelDeltaBox.Value = Math.Min(_wheelDeltaBox.Maximum, Math.Max(_wheelDeltaBox.Minimum, step.WheelDelta));
        _wheelDeltaBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_wheelDeltaBox);

        _keyPressBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyPressBox.Dock = DockStyle.Fill;
        var keyNames = new List<Keys>();
        for (var k = (int)Keys.A; k <= (int)Keys.Z; k++) keyNames.Add((Keys)k);
        for (var k = (int)Keys.D0; k <= (int)Keys.D9; k++) keyNames.Add((Keys)k);
        keyNames.AddRange([Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12,
            Keys.Escape, Keys.Tab, Keys.Space, Keys.Enter, Keys.Back, Keys.Delete, Keys.Insert,
            Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown, Keys.Up, Keys.Down, Keys.Left, Keys.Right]);
        for (var k = (int)Keys.F13; k <= (int)Keys.F24; k++) keyNames.Add((Keys)k);
        if (step.KeyCode > 0 && !keyNames.Contains((Keys)step.KeyCode)) keyNames.Add((Keys)step.KeyCode);
        _keyPressBox.Items.AddRange(keyNames.Cast<object>().ToArray());
        var keyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyPressBox.SelectedIndex = keyIdx >= 0 ? keyIdx : 0;
        Theme.StyleComboBox(_keyPressBox);

        _keyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyComboBox.Dock = DockStyle.Fill;
        _keyComboBox.Items.AddRange(keyNames.Cast<object>().ToArray());
        var comboKeyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyComboBox.SelectedIndex = comboKeyIdx >= 0 ? comboKeyIdx : 0;
        Theme.StyleComboBox(_keyComboBox);

        _modifierCheckList.Dock = DockStyle.Fill;
        _modifierCheckList.Items.AddRange(["Ctrl", "Alt", "Shift", "Win"]);
        _modifierCheckList.CheckOnClick = true;
        _modifierCheckList.Height = 94;
        if ((step.KeyModifiers & NativeMethods.ModControl) != 0) _modifierCheckList.SetItemChecked(0, true);
        if ((step.KeyModifiers & NativeMethods.ModAlt) != 0) _modifierCheckList.SetItemChecked(1, true);
        if ((step.KeyModifiers & NativeMethods.ModShift) != 0) _modifierCheckList.SetItemChecked(2, true);
        if ((step.KeyModifiers & NativeMethods.ModWin) != 0) _modifierCheckList.SetItemChecked(3, true);
        _modifierCheckList.BackColor = Theme.CurrentInputBg;
        _modifierCheckList.ForeColor = Theme.CurrentTextPrimary;
        _modifierCheckList.BorderStyle = BorderStyle.FixedSingle;
        _modifierCheckList.Font = Theme.BodyFont;

        _typeTextBox.Dock = DockStyle.Fill;
        _typeTextBox.Text = step.TextToType;
        Theme.StyleTextBox(_typeTextBox);

        _pixelColorBox.Value = Math.Min(_pixelColorBox.Maximum, Math.Max(_pixelColorBox.Minimum, step.PixelColor));
        _pixelColorBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelColorBox);

        _pixelToleranceBox.Value = Math.Min(_pixelToleranceBox.Maximum, Math.Max(_pixelToleranceBox.Minimum, step.PixelTolerance));
        _pixelToleranceBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelToleranceBox);

        _pixelTimeoutBox.Value = Math.Min(_pixelTimeoutBox.Maximum, Math.Max(_pixelTimeoutBox.Minimum, step.PixelTimeoutMs));
        _pixelTimeoutBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_pixelTimeoutBox);

        _searchWBox.Value = Math.Min(_searchWBox.Maximum, Math.Max(_searchWBox.Minimum, step.SearchWidth));
        _searchWBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_searchWBox);

        _searchHBox.Value = Math.Min(_searchHBox.Maximum, Math.Max(_searchHBox.Minimum, step.SearchHeight));
        _searchHBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_searchHBox);

        _delayMaxBox.Value = Math.Min(_delayMaxBox.Maximum, Math.Max(_delayMaxBox.Minimum, step.DelayMsMax));
        _delayMaxBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_delayMaxBox);

        _jumpToStepBox.Value = Math.Min(_jumpToStepBox.Maximum, Math.Max(_jumpToStepBox.Minimum, Math.Max(0, step.JumpToStepIndex + 1)));
        _jumpToStepBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_jumpToStepBox);

        _runProgramPathBox.Dock = DockStyle.Fill;
        _runProgramPathBox.Text = step.RunProgramPath;
        Theme.StyleTextBox(_runProgramPathBox);

        _runProgramArgsBox.Dock = DockStyle.Fill;
        _runProgramArgsBox.Text = step.RunProgramArgs;
        Theme.StyleTextBox(_runProgramArgsBox);

        _soundFilePathBox.Dock = DockStyle.Fill;
        _soundFilePathBox.Text = step.SoundFilePath;
        Theme.StyleTextBox(_soundFilePathBox);

        _chainMacroIdBox.Dock = DockStyle.Fill;
        _chainMacroIdBox.Text = step.ChainMacroId;
        Theme.StyleTextBox(_chainMacroIdBox);

        var typeLabel = CreateDialogLabel(UiText.StepType);
        _xLabel = CreateDialogLabel(UiText.X);
        _yLabel = CreateDialogLabel(UiText.Y);
        _delayLabel = CreateDialogLabel(UiText.DelayMs);
        _dragEndXLabel = CreateDialogLabel(UiText.DragEndX);
        _dragEndYLabel = CreateDialogLabel(UiText.DragEndY);
        _wheelLabel = CreateDialogLabel(UiText.WheelDelta);
        _keyLabel = CreateDialogLabel(UiText.KeyToPress);
        _modifierLabel = CreateDialogLabel(UiText.Modifiers);
        _textLabel = CreateDialogLabel(UiText.TextToType);
        _pixelColorLabel = CreateDialogLabel(UiText.PixelColor);
        _pixelToleranceLabel = CreateDialogLabel(UiText.PixelTolerance);
        _pixelTimeoutLabel = CreateDialogLabel(UiText.PixelTimeout);
        _searchWLabel = CreateDialogLabel(UiText.SearchWidth);
        _searchHLabel = CreateDialogLabel(UiText.SearchHeight);
        _delayMaxLabel = CreateDialogLabel(UiText.DelayMax);
        _jumpToStepLabel = CreateDialogLabel(UiText.JumpToStep);
        _runProgramPathLabel = CreateDialogLabel(UiText.ProgramPath);
        _runProgramArgsLabel = CreateDialogLabel(UiText.ProgramArgs);
        _soundFileLabel = CreateDialogLabel(UiText.SoundFile);
        _chainMacroIdLabel = CreateDialogLabel(UiText.ChainMacroId);

        panel.Controls.Add(typeLabel, 0, 0);
        panel.Controls.Add(_typeBox, 1, 0);
        panel.Controls.Add(_xLabel, 0, 1);
        panel.Controls.Add(_xBox, 1, 1);
        panel.Controls.Add(_yLabel, 0, 2);
        panel.Controls.Add(_yBox, 1, 2);
        panel.Controls.Add(_delayLabel, 0, 3);
        panel.Controls.Add(_delayBox, 1, 3);
        panel.Controls.Add(_dragEndXLabel, 0, 4);
        panel.Controls.Add(_dragEndXBox, 1, 4);
        panel.Controls.Add(_dragEndYLabel, 0, 5);
        panel.Controls.Add(_dragEndYBox, 1, 5);
        panel.Controls.Add(_wheelLabel, 0, 6);
        panel.Controls.Add(_wheelDeltaBox, 1, 6);
        panel.Controls.Add(_keyLabel, 0, 7);
        var keyPanel = new Panel { Dock = DockStyle.Fill, Height = 34 };
        keyPanel.Controls.Add(_keyPressBox); keyPanel.Controls.Add(_keyComboBox);
        panel.Controls.Add(keyPanel, 1, 7);
        panel.Controls.Add(_modifierLabel, 0, 8);
        panel.Controls.Add(_modifierCheckList, 1, 8);
        panel.Controls.Add(_textLabel, 0, 9);
        panel.Controls.Add(_typeTextBox, 1, 9);
        panel.Controls.Add(_pixelColorLabel, 0, 10);
        panel.Controls.Add(_pixelColorBox, 1, 10);
        panel.Controls.Add(_pixelToleranceLabel, 0, 11);
        panel.Controls.Add(_pixelToleranceBox, 1, 11);
        panel.Controls.Add(_pixelTimeoutLabel, 0, 12);
        panel.Controls.Add(_pixelTimeoutBox, 1, 12);
        panel.Controls.Add(_searchWLabel, 0, 13);
        panel.Controls.Add(_searchWBox, 1, 13);
        panel.Controls.Add(_searchHLabel, 0, 14);
        panel.Controls.Add(_searchHBox, 1, 14);
        panel.Controls.Add(_delayMaxLabel, 0, 15);
        panel.Controls.Add(_delayMaxBox, 1, 15);
        panel.Controls.Add(_jumpToStepLabel, 0, 16);
        panel.Controls.Add(_jumpToStepBox, 1, 16);
        panel.Controls.Add(_runProgramPathLabel, 0, 17);
        panel.Controls.Add(_runProgramPathBox, 1, 17);
        panel.Controls.Add(_runProgramArgsLabel, 0, 18);
        panel.Controls.Add(_runProgramArgsBox, 1, 18);
        panel.Controls.Add(_soundFileLabel, 0, 19);
        panel.Controls.Add(_soundFilePathBox, 1, 19);
        panel.Controls.Add(_chainMacroIdLabel, 0, 20);
        panel.Controls.Add(_chainMacroIdBox, 1, 20);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        Theme.StylePanel(buttonPanel);

        var cancelBtn = new Button { Text = UiText.Cancel, Width = 80, Height = 32 };
        Theme.StyleButton(cancelBtn);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var okBtn = new Button { Text = UiText.Confirm, Width = 80, Height = 32 };
        Theme.StyleAccentButton(okBtn);
        okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        buttonPanel.Controls.Add(cancelBtn);
        buttonPanel.Controls.Add(okBtn);
        var timingLabel = CreateDialogLabel("录制间隔（毫秒）");
        _recordedDelayBox.Value = step.RecordedDelayMs ?? 0;
        _recordedDelayBox.Dock = DockStyle.Fill; Theme.StyleNumericUpDown(_recordedDelayBox);
        timingLabel.Visible = _recordedDelayBox.Visible = _hasRecordedTiming;
        panel.Controls.Add(timingLabel, 0, 21); panel.Controls.Add(_recordedDelayBox, 1, 21);
        root.Controls.Add(buttonPanel, 0, 1);
        foreach (Control field in panel.Controls) field.Margin = new Padding(4, 7, 4, 7);
        _typeBox.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        UpdateFieldVisibility();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); PerformAutoScale();
        _dialogScaled = true;
    }

    private void UpdateFieldVisibility()
    {
        var type = StepType;
        var isClick = type is MacroStepType.LeftClick or MacroStepType.RightClick or MacroStepType.DoubleClick or MacroStepType.MiddleClick;
        var isMove = type is MacroStepType.Move or MacroStepType.MoveRelative;
        var isDrag = type == MacroStepType.Drag;
        var isDelay = type == MacroStepType.Delay;
        var isWheel = type == MacroStepType.MouseWheel;
        var isKey = type is MacroStepType.KeyPress or MacroStepType.KeyCombo;
        var isCombo = type == MacroStepType.KeyCombo;
        var isTypeText = type == MacroStepType.TypeText;
        var isPixel = type is MacroStepType.WaitPixel or MacroStepType.FindPixel or MacroStepType.JumpIfPixel;
        var isFindPixel = type == MacroStepType.FindPixel;
        var isScreenshot = type == MacroStepType.Screenshot;
        var isRandomDelay = type == MacroStepType.RandomDelay;
        var isJumpIfPixel = type == MacroStepType.JumpIfPixel;
        var isRunProgram = type == MacroStepType.RunProgram;
        var isPlaySound = type == MacroStepType.PlaySound;
        var isChainMacro = type == MacroStepType.ChainMacro;

        _xLabel.Visible = _xBox.Visible = isClick || isMove || isDrag || isPixel || isScreenshot;
        _yLabel.Visible = _yBox.Visible = isClick || isMove || isDrag || isPixel || isScreenshot;
        _delayLabel.Visible = _delayBox.Visible = isDelay || isRandomDelay;
        _dragEndXLabel.Visible = _dragEndXBox.Visible = isDrag;
        _dragEndYLabel.Visible = _dragEndYBox.Visible = isDrag;
        _wheelLabel.Visible = _wheelDeltaBox.Visible = isWheel;
        _keyLabel.Visible = isKey || isCombo;
        _keyPressBox.Visible = isKey && !isCombo;
        _keyComboBox.Visible = isCombo;
        _modifierLabel.Visible = _modifierCheckList.Visible = isCombo;
        _textLabel.Visible = _typeTextBox.Visible = isTypeText;
        _pixelColorLabel.Visible = _pixelColorBox.Visible = isPixel;
        _pixelToleranceLabel.Visible = _pixelToleranceBox.Visible = isPixel;
        _pixelTimeoutLabel.Visible = _pixelTimeoutBox.Visible = isPixel;
        _searchWLabel.Visible = _searchWBox.Visible = isFindPixel || isScreenshot;
        _searchHLabel.Visible = _searchHBox.Visible = isFindPixel || isScreenshot;
        _delayMaxLabel.Visible = _delayMaxBox.Visible = isRandomDelay;
        _jumpToStepLabel.Visible = _jumpToStepBox.Visible = isJumpIfPixel;
        _runProgramPathLabel.Visible = _runProgramPathBox.Visible = isRunProgram;
        _runProgramArgsLabel.Visible = _runProgramArgsBox.Visible = isRunProgram;
        _soundFileLabel.Visible = _soundFilePathBox.Visible = isPlaySound;
        _chainMacroIdLabel.Visible = _chainMacroIdBox.Visible = isChainMacro;
        if (_fields is not null)
        {
            var visibleRows = new[] { true, isClick || isMove || isDrag || isPixel || isScreenshot,
                isClick || isMove || isDrag || isPixel || isScreenshot, isDelay || isRandomDelay,
                isDrag, isDrag, isWheel, isKey, isCombo, isTypeText, isPixel, isPixel,
                isPixel, isFindPixel || isScreenshot, isFindPixel || isScreenshot, isRandomDelay,
                isJumpIfPixel, isRunProgram, isRunProgram, isPlaySound, isChainMacro, _hasRecordedTiming };
            for (var i = 0; i < visibleRows.Length; i++)
            {
                _fields.RowStyles[i].SizeType = SizeType.Absolute;
                _fields.RowStyles[i].Height = visibleRows[i] ? (i == 8 ? 108 : 48) * (_dialogScaled ? DeviceDpi / 96f : 1) : 0;
            }
        }
    }

    private static Label CreateDialogLabel(string text)
    {
        var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Theme.StyleLabel(label);
        return label;
    }
}
