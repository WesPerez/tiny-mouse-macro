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
    private readonly SwitchRequestQueue _switchRequests = new();
    private string? _runningProfileId;
    private volatile bool _stopRequested;

    private HotkeyService? _hotkeys;
    private RecorderService? _recorder;
    private List<MacroProfile> _profiles = [];
    private bool _isBinding;
    private bool _dirty;
    private bool _globalPaused;
    private int _dragStepIndex = -1;
    private MacroProfile? _recordingProfile;

    private readonly ProfileListBox _profileList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly SoftComboBox _triggerTypeBox = new();
    private readonly TextBox _hookKeyBox = new();
    private readonly SoftComboBox _mouseButtonBox = new();
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
            ContextMenuStrip = new AppMenu()
        };
        _trayIcon.ContextMenuStrip.Items.Add(showItem);
        _trayIcon.ContextMenuStrip.Items.Add(autoStartItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);
        _trayIcon.MouseDoubleClick += (_, _) => ShowFromTray();

        _cancelButton = new SoftButton
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
                _lastSignal.Text = $"已收到 {signal.Shortcut} · {DateTime.Now:HH:mm:ss}";
                Log($"{signal.Shortcut} · {signal.Source}" + (signal.Diagnostic ? " · 仅检测" : ""), technical: true);
                if (signal.Diagnostic || _diagnosticCheck.Checked || _globalPaused || IsCapturing) return;
                var profile = _profiles.FirstOrDefault(p => p.Id == signal.ProfileId && p.Enabled);
                if (profile is not null) RunMacro(profile, signal.ReleaseGate);
            });
            _hotkeys.EmergencyStop += () => { _stopRequested = true; _executor.Cancel(); PostToUi(StopAll); };
            _hotkeys.PauseRequested += () => PostToUi(ToggleGlobalPause);
            _hotkeys.DiagnosticMessage += message => PostToUi(() => Log(message, technical: true));
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
        RefreshActivityDisplay();
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
        _pauseButton.Text = _globalPaused ? "恢复自动运行" : "暂停自动运行";
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
        RefreshActivityDisplay();
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

        // The UI has one keyboard choice; retain the actual chord when a legacy
        // hook profile is changed to mouse and back, instead of reviving a stale Hotkey.
        if (CurrentProfile.TriggerType == MacroTriggerType.KeyboardHook)
            CurrentProfile.Hotkey = CurrentProfile.TriggerKey > 0
                ? HotkeyParser.Normalize(CurrentProfile.TriggerModifiers, (uint)CurrentProfile.TriggerKey) : "";

        CurrentProfile.TriggerType = _triggerTypeBox.SelectedIndex switch
        {
            0 when CurrentProfile.TriggerType == MacroTriggerType.KeyboardHook => MacroTriggerType.KeyboardHook,
            0 => MacroTriggerType.KeyboardHotkey,
            1 => MacroTriggerType.MouseButton,
            _ => MacroTriggerType.KeyboardHotkey
        };
        if (CurrentProfile.TriggerType == MacroTriggerType.MouseButton)
            CurrentProfile.TriggerMouseButton = _mouseButtonBox.SelectedIndex switch { 0 => 4, 1 => 5, _ => 3 };

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
                MacroTriggerType.KeyboardHook => 0,
                MacroTriggerType.MouseButton => 1,
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
        RefreshActivityDisplay();
    }

    private void StepList_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var step = CurrentStep;
        if (step is null || IsCapturing) return;

        using var dialog = new StepEditDialog(step);
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.Font = Theme.BodyFont;
        Theme.ApplyToForm(dialog);

        if (ShowStepEditor(dialog) == DialogResult.OK)
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
        SetStatus("已复制方案，副本默认停用；设置快捷键后在上方勾选“启用”");
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
        _themeButton.Invalidate(); RefreshActivityDisplay();
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
            if (tag == "Surface") control.BackColor = Theme.CurrentSurface;
            else Theme.StylePanel(control);
        }

        foreach (Control child in control.Controls)
        {
            ReapplyTheme(child);
        }
    }

    private async Task CapturePointAfterDelayAsync(bool addClickStep, MacroStepType stepType = MacroStepType.LeftClick)
    {
        if (_preview || IsCapturing || _executor.IsRunning || _executionCts is not null || CurrentProfile is not { } profile) return;
        using var cts = new CancellationTokenSource(); _captureCts = cts;
        BeginActivity(ActivityMode.Picking, profile);
        SetCaptureEditing(false);
        if (_hotkeys is not null) _hotkeys.Suspended = true;
        try
        {
            for (var i = 3; i >= 1; i--) { _activityNote = $"{i} 秒后记住点击位置，请把鼠标移到目标上。"; RefreshActivityDisplay(); await Task.Delay(1000, cts.Token); }
            var point = Cursor.Position;
            if (_profiles.Contains(profile))
            {
                profile.Steps.Add(new() { Type = addClickStep ? stepType : MacroStepType.Move, X = point.X, Y = point.Y });
                RefreshSteps(); MarkDirty(); SetStatus($"已添加目标 ({point.X}, {point.Y})");
                FinishActivity($"已添加点击位置 · {profile.Name}", $"位置 ({point.X}, {point.Y})，运行方案时执行点击。");
            }
        }
        catch (OperationCanceledException) { FinishActivity("已取消取点", "没有添加新的点击位置。"); }
        finally { _captureCts = null; SetCaptureEditing(true); if (_hotkeys is not null) _hotkeys.Suspended = false; SaveAll(); RefreshActivityDisplay(); }
    }

    private void AddDelayStep()
    {
        if (IsCapturing || CurrentProfile is null) return;
        var step = new MacroStep { Type = MacroStepType.Delay, DelayMs = 500 };
        using var dialog = new StepEditDialog(step) { Text = "加延迟", StartPosition = FormStartPosition.CenterParent };
        if (ShowStepEditor(dialog) == DialogResult.OK) { ApplyStepEdit(step, dialog); AddStep(step); }
    }

    private async void ToggleRecording()
    {
        if (_preview || _recorder is null) return;
        if (_recorder.IsRecording || _startingRecording) { StopAll(); return; }
        if (_executor.IsRunning || _executionCts is not null || _captureCts is not null || CurrentProfile is not { } profile) return;
        _startingRecording = true; _recordingProfile = profile;
        BeginActivity(ActivityMode.Countdown, profile);
        SetCaptureEditing(false);
        if (_hotkeys is not null) _hotkeys.Suspended = true;
        using var cts = new CancellationTokenSource(); _captureCts = cts;
        try
        {
            for (var i = 3; i >= 1; i--) { _activityNote = $"{i} 秒后开始，录制鼠标和键盘；Ctrl+Alt+F10 停止。"; RefreshActivityDisplay(); await Task.Delay(1000, cts.Token); }
            _recorder.Start(excludedWindow: Handle);
            _recordClock.Restart(); _recordUiTimer.Start();
            _profileList.Enabled = false; _workspace.Enabled = true;
            BeginActivity(ActivityMode.Recording, profile); SetStatus("正在录制鼠标、键盘与操作间隔 · Ctrl+Alt+F10 停止");
        }
        catch (OperationCanceledException) { _recordingProfile = null; FinishActivity("已取消录制", "没有开始录制。"); }
        catch (Exception ex) { _recordingProfile = null; FinishActivity("录制未开始", ex.Message, true); SetStatus(ex.Message); Log(ex.Message); }
        finally
        {
            _captureCts = null; _startingRecording = false;
            if (!_recorder.IsRecording) { SetCaptureEditing(true); if (_hotkeys is not null) _hotkeys.Suspended = false; SaveAll(); }
            RefreshActivityDisplay();
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
        if (_preview || IsCapturing || _globalPaused || _diagnosticCheck.Checked) return;
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
        _ = RunAndDrainAsync(profile);
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
        _backendLabel.Text = $"{_hotkeys.RegisteredCount} 个方案可用";
        foreach (var error in errors) Log(error);
        _tips.SetToolTip(_backendLabel, errors.Count == 0 ? "快捷键可以使用" : string.Join("\n", errors));
        SetStatus(errors.Count == 0 ? "就绪" : "部分快捷键不可用，请在运行记录中查看详情");
        RefreshActivityDisplay();
    }

    private TriggerReleaseGate? _pendingReleaseGate;
    private void RunMacro(MacroProfile profile, TriggerReleaseGate? releaseGate = null)
    {
        if (_preview || IsCapturing || _globalPaused || _diagnosticCheck.Checked) return;
        if (profile.RepeatCount == 0 && _hotkeys?.StopAvailable != true)
        { SetStatus("停止快捷键被占用，请先换一个快捷键再使用持续循环。"); return; }
        if (_executor.IsRunning || _executionCts is not null)
        {
            var active = _profiles.FirstOrDefault(p => p.Id == _runningProfileId);
            if (!_stopRequested && active is not null && _switchRequests.Offer(active, profile))
            {
                _pendingReleaseGate = active.Id == profile.Id ? null : releaseGate;
                var message = active.Id == profile.Id ? $"正在切换到：{profile.Name}" : $"接下来切换到：{profile.Name}";
                SetStatus(message); Log(message);
            }
            else { SetStatus(UiText.AlreadyRunning); Log($"正在运行，未执行：{profile.Name}"); }
            return;
        }

        SetStatus(UiText.Running(profile.Name));
        Log(UiText.Running(profile.Name));
        _ = RunAndDrainAsync(profile, releaseGate);
    }

    private async Task RunAndDrainAsync(MacroProfile profile, TriggerReleaseGate? releaseGate = null)
    {
        _stopRequested = false;
        _runningProfileId = profile.Id;
        try { await RunWithCancellationAsync(profile, releaseGate); }
        finally
        {
            _runningProfileId = null;
            var nextId = _switchRequests.Take();
            var nextGate = _pendingReleaseGate; _pendingReleaseGate = null;
            var next = _profiles.FirstOrDefault(p => p.Id == nextId && p.Enabled);
            if (next is not null && !_stopRequested && !_globalPaused && !IsCapturing && !_diagnosticCheck.Checked && !IsDisposed)
                RunMacro(next, nextGate);
        }
    }

    private async Task RunWithCancellationAsync(MacroProfile profile, TriggerReleaseGate? releaseGate = null)
    {
        var current = CloneProfile(profile);
        for (var chainDepth = 0; chainDepth < MaxChainDepth; chainDepth++)
        {
            using var cts = new CancellationTokenSource();
            _executionCts = cts;
            _runningProfileId = current.Id;
            BeginActivity(ActivityMode.Running, current);
            var completed = false;
            var outcome = $"已完成 · {current.Name}";
            var detail = "本次运行已结束，等待下一次触发。";
            var failed = false;
            try
            {
                var releaseDeadline = Environment.TickCount64 + 1500;
                if (!_preview && releaseGate is { IsReleased: false })
                    Log($"等待触发修饰键松开 · 系统修饰键 {PhysicalModifiers.Read()} · 监听仍有按键未松开", technical: true);
                while ((!_preview && AnyModifierHeld()) || releaseGate is { IsReleased: false })
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= releaseDeadline) throw new InvalidOperationException("未确认快捷键已松开，本次未执行；请松开按键后重试");
                    await Task.Delay(10, cts.Token);
                }
                if (releaseGate is not null) Log("触发修饰键已松开，开始按顺序回放", technical: true);
                releaseGate = null;
                await _executor.RunAsync(current, cts.Token, message => PostToUi(() => Log(message, technical: true)));
                completed = true;
                SetStatus(UiText.Finished(current.Name)); Log(UiText.Finished(current.Name));
            }
            catch (OperationCanceledException)
            {
                outcome = $"已停止 · {current.Name}"; detail = "已停止本次运行。";
                SetStatus(UiText.Cancelled(current.Name)); Log(UiText.Cancelled(current.Name));
            }
            catch (Exception ex)
            {
                failed = true; outcome = $"未完成 · {current.Name}"; detail = ex.GetBaseException().Message;
                SetStatus(detail); Log($"{UiText.Error}: {detail}");
            }
            finally
            {
                if (ReferenceEquals(_executionCts, cts)) _executionCts = null;
            }
            FinishActivity(outcome, detail, failed, completed ? "已完成" : "已停止");
            if (!completed || _stopRequested) return;
            var chainId = _executor.ConsumeChainMacroId();
            if (string.IsNullOrWhiteSpace(chainId)) return;
            var target = _profiles.FirstOrDefault(p => p.Id == chainId);
            if (target is null || !target.Enabled || target.Id == current.Id)
            {
                FinishActivity($"已停止 · {current.Name}", "后续方案不存在、未启用或引用了自身。", true);
                Log(UiText.ChainSkipped(current.Name)); return;
            }
            Log(UiText.Chaining(target.Name));
            current = CloneProfile(target);
        }
        FinishActivity($"已停止 · {current.Name}", "连续调用的方案过多，请检查是否相互循环调用。", true);
        Log(UiText.ChainSkipped(current.Name));
    }
    private void CancelExecution()
    {
        _stopRequested = true;
        if (_activityMode == ActivityMode.Running)
        { _activityMode = ActivityMode.Stopping; _activityNote = "正在结束本次运行…"; RefreshActivityDisplay(); }
        _switchRequests.Clear();
        _pendingReleaseGate = null;
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
        if (_uiDisposed || IsDisposed || Disposing) return;
        _statusLabel.Text = message;
    }

    private void Log(string message, bool technical = false)
    {
        if (_uiDisposed || IsDisposed || Disposing) return;
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        if (!technical) _logList.Items.Insert(0, $"[{timestamp}] {message}");
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
