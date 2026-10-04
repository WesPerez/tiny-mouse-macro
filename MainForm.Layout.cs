namespace TinyMouseMacro;

public sealed partial class MainForm
{
    private readonly Label _saveState = new(), _stepSummary = new(), _backendLabel = new(), _lastSignal = new();
    private readonly SoftButton _recordButton = new(), _pauseButton = new(), _runButton = new();
    private readonly ThemeToggleButton _themeButton = new();
    private readonly CheckBox _diagnosticCheck = new();
    private readonly CheckBox _externalTriggerCheck = new();
    private ExternalTrigger? _externalTrigger;
    private readonly ScreenMap _screenMap = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 400 };
    private readonly TableLayoutPanel _workspace = Grid(1, 1);
    private Control _composePanel = null!, _optionsPanel = null!, _diagnosticsPanel = null!;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _recordUiTimer = new() { Interval = 250 };
    private readonly System.Diagnostics.Stopwatch _recordClock = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<MacroStep> _recordQueue = new();
    private CancellationTokenSource? _captureCts;
    private bool _startingRecording;
    private Point _dragOrigin;
    private readonly bool _preview;

    private void BuildUi()
    {
        SuspendLayout();
        var root = Grid(1, 4, 18);
        root.RowStyles.Add(new(SizeType.Absolute, 60));
        root.RowStyles.Add(new(SizeType.Absolute, 76));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 38));
        Controls.Add(root);

        var heading = Grid(2, 1);
        heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new(SizeType.Absolute, 52));
        var branding = new Panel { Dock = DockStyle.Fill };
        branding.Controls.Add(new Label { Text = "TinyMouseMacro", AutoSize = true, Location = new(0, 0), Font = Theme.TitleFont, Tag = "Title", ForeColor = Theme.CurrentTextPrimary });
        branding.Controls.Add(new Label { Text = "一键切换屏幕，录制与回放常用键鼠操作。", AutoSize = true, Location = new(2, 38), Tag = "Body", Font = Theme.BodyFont, ForeColor = Theme.CurrentTextSecondary });
        heading.Controls.Add(branding, 0, 0);
        _themeButton.Size = new(42, 42); _themeButton.Margin = new(6, 4, 0, 0);
        _themeButton.Click += (_, _) => ToggleTheme();
        heading.Controls.Add(_themeButton, 1, 0); root.Controls.Add(heading, 0, 0);
        root.Controls.Add(BuildActivityBar(), 0, 1);

        var body = Grid(2, 1);
        body.ColumnStyles.Add(new(SizeType.Absolute, 238));
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        body.Padding = new(0, 12, 0, 0); root.Controls.Add(body, 0, 2);
        body.Controls.Add(BuildSidebar(), 0, 0);
        var workspace = Grid(1, 2); workspace.Padding = new(18, 0, 0, 0);
        workspace.RowStyles.Add(new(SizeType.Absolute, 70));
        workspace.RowStyles.Add(new(SizeType.Percent, 100));
        workspace.Controls.Add(BuildIdentity(), 0, 0);
        _composePanel = BuildComposer(); _optionsPanel = BuildOptions(); _diagnosticsPanel = BuildDiagnostics();
        _workspace.Controls.Add(_optionsPanel, 0, 0); _workspace.Controls.Add(_diagnosticsPanel, 0, 0);
        _workspace.Controls.Add(_composePanel, 0, 0);
        _optionsPanel.Visible = false; _diagnosticsPanel.Visible = false;
        _composePanel.BringToFront();
        workspace.Controls.Add(_workspace, 0, 1); body.Controls.Add(workspace, 1, 0);

        var footer = Grid(2, 1);
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 250));
        _statusLabel.Dock = DockStyle.Fill; _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft; _statusLabel.Tag = ThemeRoleStatus;
        Theme.StyleStatusBar(_statusLabel); footer.Controls.Add(_statusLabel, 0, 0);
        _saveState.Dock = DockStyle.Fill; _saveState.TextAlign = ContentAlignment.MiddleRight;
        _saveState.Font = Theme.BodyFont; _saveState.ForeColor = Theme.CurrentTextSecondary;
        footer.Controls.Add(_saveState, 1, 0); root.Controls.Add(footer, 0, 3);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAll(); };
        _recordUiTimer.Tick += (_, _) => FlushRecording();
        _activityTimer.Tick += (_, _) => { if (_activityMode != ActivityMode.Idle) RefreshActivityDisplay(); };
        _activityTimer.Start();
        RefreshActivityDisplay();
        ResumeLayout(true);
    }

    private Control BuildSidebar()
    {
        var panel = Grid(1, 5, 12, rounded: true);
        panel.RowStyles.Add(new(SizeType.Absolute, 36)); panel.RowStyles.Add(new(SizeType.Absolute, 42));
        panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 46));
        panel.RowStyles.Add(new(SizeType.Absolute, 74));
        panel.Controls.Add(Heading("我的方案"), 0, 0);
        _searchBox.Dock = DockStyle.Fill; _searchBox.PlaceholderText = "搜索名称或快捷键";
        _searchBox.AccessibleName = "搜索方案"; Theme.StyleTextBox(_searchBox);
        _searchBox.TextChanged += (_, _) => FilterProfiles(); panel.Controls.Add(_searchBox, 0, 1);
        _profileList.Dock = DockStyle.Fill; _profileList.IntegralHeight = false; _profileList.AccessibleName = "方案列表";
        _profileList.SelectedIndexChanged += (_, _) => BindSelectedProfile();
        Theme.StyleListBox(_profileList); _profileList.Tag = "Profiles"; _profileList.ItemHeight = 64;
        panel.Controls.Add(_profileList, 0, 2);
        var buttons = Flow(); buttons.Controls.Add(CreateAccentButton("＋ 新建方案", AddProfile, 125));
        var more = CreateButton("更多 ⋯", () => { }, 73);
        var menu = new AppMenu(); more.ContextMenuStrip = menu; more.Disposed += (_, _) => menu.Dispose();
        menu.Items.Add("复制当前方案", null, (_, _) => DuplicateProfile());
        menu.Items.Add("导入方案…", null, (_, _) => ImportProfile());
        menu.Items.Add("导出当前方案…", null, (_, _) => ExportProfile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除当前方案…", null, (_, _) => DeleteProfile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("运行记录…", null, (_, _) => OpenToolPanel(_diagnosticsPanel, "运行记录"));
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height)); buttons.Controls.Add(more);
        panel.Controls.Add(buttons, 0, 3);
        var bottom = Grid(1, 2); bottom.RowStyles.Add(new(SizeType.Absolute, 40)); bottom.RowStyles.Add(new(SizeType.Percent, 100));
        _pauseButton.Text = "暂停自动运行"; _pauseButton.Dock = DockStyle.Fill; Theme.StyleButton(_pauseButton);
        _pauseButton.Click += (_, _) => ToggleGlobalPause(); bottom.Controls.Add(_pauseButton, 0, 0);
        bottom.Controls.Add(Hint("Ctrl+Alt+P 暂停 / 恢复"), 0, 1);
        panel.Controls.Add(bottom, 0, 4);
        Theme.SurfaceTree(panel); return panel;
    }

    private Control BuildIdentity()
    {
        var panel = Grid(4, 2);
        panel.ColumnStyles.Add(new(SizeType.Percent, 40)); panel.ColumnStyles.Add(new(SizeType.Absolute, 170));
        panel.ColumnStyles.Add(new(SizeType.Percent, 60)); panel.ColumnStyles.Add(new(SizeType.Absolute, 88));
        panel.RowStyles.Add(new(SizeType.Absolute, 28)); panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(Heading("方案名称"), 0, 0); panel.Controls.Add(Heading("触发方式"), 1, 0); panel.Controls.Add(Heading("快捷键"), 2, 0);
        Theme.StyleTextBox(_nameBox); _nameBox.Dock = DockStyle.Fill; _nameBox.AccessibleName = "方案名称";
        _nameBox.TextChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? UiText.Untitled : _nameBox.Text.Trim();
            _profileList.Invalidate(); MarkDirty();
        };
        panel.Controls.Add(_nameBox, 0, 1);
        _triggerTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _triggerTypeBox.Items.AddRange(["键盘快捷键", "鼠标按键"]);
        _triggerTypeBox.Dock = DockStyle.Fill; Theme.StyleComboBox(_triggerTypeBox);
        _triggerTypeBox.SelectedIndexChanged += TriggerTypeBox_SelectedIndexChanged;
        panel.Controls.Add(_triggerTypeBox, 1, 1);
        var inputs = new Panel { Dock = DockStyle.Fill };
        foreach (var box in new[] { _hotkeyBox, _hookKeyBox })
        {
            box.Dock = DockStyle.Top; box.ReadOnly = true; box.PlaceholderText = "点击后按键；Backspace 清除";
            box.AccessibleName = "方案快捷键"; Theme.StyleTextBox(box); box.PreviewKeyDown += (_, e) => e.IsInputKey = true;
            box.Enter += (_, _) => { if (_hotkeys is not null) _hotkeys.Suspended = true; };
            box.Leave += (_, _) => { if (_hotkeys is not null) { _hotkeys.Suspended = IsCapturing; RebuildHotkeys(); } };
            inputs.Controls.Add(box);
        }
        _hotkeyBox.KeyDown += HotkeyBox_KeyDown; _hookKeyBox.KeyDown += HookKeyBox_KeyDown;
        _mouseButtonBox.Dock = DockStyle.Top; _mouseButtonBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mouseButtonBox.Items.AddRange(["侧键 X1（后退）", "侧键 X2（前进）", "鼠标中键"]); Theme.StyleComboBox(_mouseButtonBox);
        _mouseButtonBox.SelectedIndexChanged += (_, _) =>
        { if (_isBinding || CurrentProfile is null) return; CurrentProfile.TriggerMouseButton = _mouseButtonBox.SelectedIndex switch { 0 => 4, 1 => 5, _ => 3 }; MarkDirty(); };
        inputs.Controls.Add(_mouseButtonBox); panel.Controls.Add(inputs, 2, 1);
        _enabledCheckBox.Text = "启用"; _enabledCheckBox.AutoSize = true; _enabledCheckBox.Margin = new(12, 7, 0, 0);
        _enabledCheckBox.CheckedChanged += (_, _) => { if (_isBinding || CurrentProfile is null) return; CurrentProfile.Enabled = _enabledCheckBox.Checked; MarkDirty(); };
        panel.Controls.Add(_enabledCheckBox, 3, 1);
        _tips.SetToolTip(inputs, "推荐使用不与其他应用冲突的组合键。单独的字母按键会影响正常输入。F13–F24 适合映射到专用键。" );
        return panel;
    }

    private Control BuildComposer()
    {
        var panel = Grid(1, 5, 12, rounded: true);
        panel.RowStyles.Add(new(SizeType.Absolute, 28)); panel.RowStyles.Add(new(SizeType.Absolute, 96));
        panel.RowStyles.Add(new(SizeType.Absolute, 50)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 46));
        var title = Grid(2, 1); title.ColumnStyles.Add(new(SizeType.Percent, 100)); title.ColumnStyles.Add(new(SizeType.Absolute, 300));
        title.Controls.Add(Heading("选择鼠标位置"), 0, 0);
        _livePositionLabel.Dock = DockStyle.Fill; _livePositionLabel.TextAlign = ContentAlignment.MiddleRight;
        _livePositionLabel.Font = Theme.MonoBodyFont; _livePositionLabel.ForeColor = Theme.CurrentTextSecondary;
        title.Controls.Add(_livePositionLabel, 1, 0); panel.Controls.Add(title, 0, 0);
        _screenMap.Dock = DockStyle.Fill; _screenMap.AccessibleName = "显示器布局：点击设置目标位置，不会移动真实鼠标";
        _screenMap.PointChosen += point => SetTargetPoint(point); panel.Controls.Add(_screenMap, 0, 1);
        var actions = Grid(4, 1); actions.Padding = new(0, 3, 0, 3);
        foreach (var width in new[] { 29f, 20f, 25f, 26f }) actions.ColumnStyles.Add(new(SizeType.Percent, width));
        var pick = CreateButton("定位点击（3 秒后）", async () => await CapturePointAfterDelayAsync(true), 160);
        _tips.SetToolTip(pick, "点击后把鼠标移到目标位置，3 秒后添加左键点击步骤。此时只记住位置。");
        var delay = CreateButton("加延迟…", AddDelayStep, 100);
        _recordButton.Text = "●  开始录制"; _recordButton.Margin = new(3);
        Theme.StyleButton(_recordButton); _recordButton.Click += (_, _) => ToggleRecording();
        _tips.SetToolTip(_recordButton, "3 秒后开始录制鼠标、键盘和操作间隔，追加到当前方案。");
        var add = CreateButton("添加其他动作…", AddCustomStep, 150);
        var actionButtons = new Button[] { pick, delay, _recordButton, add };
        for (var i = 0; i < actionButtons.Length; i++) { actionButtons[i].Dock = DockStyle.Fill; actions.Controls.Add(actionButtons[i], i, 0); }
        panel.Controls.Add(actions, 0, 2);
        var steps = Grid(1, 2); steps.RowStyles.Add(new(SizeType.Absolute, 28)); steps.RowStyles.Add(new(SizeType.Percent, 100));
        _stepSummary.Dock = DockStyle.Fill; _stepSummary.Tag = ThemeRoleSectionHeader; _stepSummary.Font = Theme.SectionFont; _stepSummary.ForeColor = Theme.CurrentTextPrimary;
        _stepSummary.TextAlign = ContentAlignment.MiddleLeft; steps.Controls.Add(_stepSummary, 0, 0);
        _stepList.Dock = DockStyle.Fill; _stepList.IntegralHeight = false; _stepList.DataSource = _stepsSource;
        _stepList.Tag = "Steps"; Theme.StyleListBox(_stepList); _stepList.ItemHeight = 48;
        _stepList.AccessibleName = "动作时间线"; _stepList.AllowDrop = true;
        _stepList.MouseDoubleClick += StepList_MouseDoubleClick; _stepList.MouseDown += StepList_MouseDown;
        _stepList.MouseMove += StepList_MouseMove; _stepList.DragOver += StepList_DragOver; _stepList.DragDrop += StepList_DragDrop;
        _stepList.SelectedIndexChanged += (_, _) => { _screenMap.SelectedPoint = CurrentStep is { } step && IsPositionStep(step.Type) ? new(step.X, step.Y) : null; };
        steps.Controls.Add(_stepList, 0, 1); panel.Controls.Add(steps, 0, 3);
        var edit = Flow();
        edit.Controls.Add(CreateButton("编辑", () => StepList_MouseDoubleClick(null, new(MouseButtons.Left, 2, 0, 0, 0)), 76));
        edit.Controls.Add(CreateButton("复制", DuplicateStep, 76)); edit.Controls.Add(CreateButton("↑", MoveStepUp, 46));
        edit.Controls.Add(CreateButton("↓", MoveStepDown, 46)); edit.Controls.Add(CreateButton("删除", DeleteStep, 76));
        edit.Controls.Add(CreateButton("重复与定时…", () => OpenToolPanel(_optionsPanel, "运行设置"), 142));
        _tips.SetToolTip(_stepList, "双击编辑，拖动调整顺序");
        panel.Controls.Add(edit, 0, 4); Theme.SurfaceTree(panel); return panel;
    }

    private Control BuildOptions()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panel = Grid(2, 6, 20); panel.Dock = DockStyle.Top; panel.AutoSize = true; scroll.Controls.Add(panel);
        panel.ColumnStyles.Add(new(SizeType.Absolute, 210)); panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) panel.RowStyles.Add(new(SizeType.Absolute, 52));
        SetupNumber(_repeatCountBox, () => { if (CurrentProfile is { } p) p.RepeatCount = (int)_repeatCountBox.Value; });
        SetupNumber(_repeatIntervalBox, () => { if (CurrentProfile is { } p) p.RepeatIntervalMs = (int)_repeatIntervalBox.Value; });
        SetupNumber(_speedBox, () => { if (CurrentProfile is { } p) p.SpeedMultiplier = (double)_speedBox.Value; });
        SetupNumber(_scheduleIntervalBox, () => { if (CurrentProfile is { } p) { p.ScheduleIntervalMinutes = (int)_scheduleIntervalBox.Value; p.ScheduleNextRun = null; } });
        Option(0, "重复次数（0 = 不停）", _repeatCountBox); Option(1, "每轮间隔（毫秒）", _repeatIntervalBox);
        Option(2, "播放速度（倍）", _speedBox); Option(3, "每隔几分钟运行（0 = 关闭）", _scheduleIntervalBox);
        _targetWindowBox.PlaceholderText = "留空即可；指定时填写完整窗口名称"; Theme.StyleTextBox(_targetWindowBox);
        _targetWindowBox.TextChanged += (_, _) => { if (_isBinding || CurrentProfile is null) return; CurrentProfile.TargetWindowTitle = _targetWindowBox.Text; MarkDirty(); };
        Option(4, "运行前切换到窗口", _targetWindowBox);
        return scroll;
        void Option(int row, string name, Control input)
        { panel.Controls.Add(Heading(name), 0, row); input.Dock = DockStyle.Top; input.Margin = new(4, 9, 4, 4); panel.Controls.Add(input, 1, row); }
    }

    private Control BuildDiagnostics()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panel = Grid(1, 7, 18); panel.Dock = DockStyle.Top; panel.AutoSize = true;
        scroll.Controls.Add(panel);
        panel.RowStyles.Add(new(SizeType.Absolute, 40)); panel.RowStyles.Add(new(SizeType.Absolute, 46));
        panel.RowStyles.Add(new(SizeType.Absolute, 48)); panel.RowStyles.Add(new(SizeType.Absolute, 52));
        panel.RowStyles.Add(new(SizeType.Absolute, 32)); panel.RowStyles.Add(new(SizeType.Absolute, 200)); panel.RowStyles.Add(new(SizeType.AutoSize));
        _backendLabel.Dock = DockStyle.Fill; _backendLabel.Tag = ThemeRoleSectionHeader; _backendLabel.Font = Theme.SectionFont; _backendLabel.ForeColor = Theme.CurrentTextPrimary;
        panel.Controls.Add(_backendLabel, 0, 0);
        var tools = Flow();
        _diagnosticCheck.Text = "测试快捷键（不运行操作）"; _diagnosticCheck.AutoSize = true; _diagnosticCheck.Margin = new(4, 10, 16, 0);
        _diagnosticCheck.CheckedChanged += (_, _) =>
        {
            if (_hotkeys is not null) _hotkeys.DiagnosticMode = _diagnosticCheck.Checked;
            if (_diagnosticCheck.Checked) StopAll();
            if (!_preview) SetStatus(_diagnosticCheck.Checked ? "正在测试快捷键，暂不执行操作" : "已恢复正常执行");
            RefreshActivityDisplay();
        };
        tools.Controls.Add(_diagnosticCheck); tools.Controls.Add(CreateButton("重新连接快捷键", () => RebuildHotkeys(true), 154)); panel.Controls.Add(tools, 0, 1);
        panel.Controls.Add(CreateButton("查看详细日志…", OpenActivityLog, 160), 0, 2);
        _lastSignal.Dock = DockStyle.Fill; _lastSignal.Font = Theme.BodyFont; _lastSignal.ForeColor = Theme.CurrentAccent;
        _lastSignal.Text = "尚未收到快捷键";
        panel.Controls.Add(_lastSignal, 0, 3);
        panel.Controls.Add(Heading("最近活动"), 0, 4);
        _logList.Dock = DockStyle.Fill; _logList.IntegralHeight = false; Theme.StyleListBox(_logList);
        panel.Controls.Add(_logList, 0, 5);
        var advanced = Grid(1, 2); advanced.Dock = DockStyle.Top; advanced.AutoSize = true;
        advanced.RowStyles.Add(new(SizeType.Absolute, 46)); advanced.RowStyles.Add(new(SizeType.Absolute, 0));
        var expand = CreateButton("高级工具 ▸", () => { }, 130);
        advanced.Controls.Add(expand, 0, 0);
        var external = Flow();
        external.Visible = false;
        var expanded = false;
        expand.Click += (_, _) => { expanded = !expanded; external.Visible = expanded; advanced.RowStyles[1].Height = expanded ? 52 * DeviceDpi / 96f : 0; expand.Text = expanded ? "高级工具 ▾" : "高级工具 ▸"; };
        _externalTriggerCheck.Text = "允许外部启动器触发切屏"; _externalTriggerCheck.AutoSize = true; _externalTriggerCheck.Margin = new(4, 10, 12, 0);
        _externalTriggerCheck.CheckedChanged += (_, _) => { if (_isBinding) return; SavePreferences(); ConfigureExternalTrigger(); };
        _tips.SetToolTip(_externalTriggerCheck, "仅在使用 iCUE 等外部程序启动方案时需要。");
        external.Controls.Add(_externalTriggerCheck); external.Controls.Add(CreateButton("生成启动器…", ExportLauncher, 140));
        advanced.Controls.Add(external, 0, 1); panel.Controls.Add(advanced, 0, 6); return scroll;
    }

    private void OpenToolPanel(Control panel, string title)
    {
        if (_preview) return;
        if (IsCapturing) { SetStatus("录制结束后可打开设置和记录"); return; }
        var scale = DeviceDpi / 96f;
        var area = Screen.FromControl(this).WorkingArea;
        var width = Math.Min((int)(760 * scale), area.Width - 40);
        var height = Math.Min((int)(700 * scale), area.Height - 40);
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            Size = new(width, height), MinimumSize = new(Math.Min(width, (int)(650 * scale)), Math.Min(height, (int)(550 * scale))),
            ShowInTaskbar = false, Font = Theme.BodyFont, KeyPreview = true };
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; dialog.Close(); } };
        Theme.ApplyToForm(dialog);
        dialog.Controls.Add(panel); panel.Visible = true;
        try { dialog.ShowDialog(this); }
        finally { _diagnosticCheck.Checked = false; panel.Visible = false; dialog.Controls.Remove(panel); _workspace.Controls.Add(panel, 0, 0); _composePanel.BringToFront(); SaveAll(); }
    }

    private void OpenActivityLog()
    {
        if (_preview) return;
        if (_executor.IsRunning || _executionCts is not null) { SetStatus("操作结束后可打开详细日志"); return; }
        var path = Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "activity.log");
        if (!File.Exists(path)) { SetStatus("还没有运行记录"); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { SetStatus("日志暂时无法打开，请稍后重试"); }
    }

    private void SetupNumber(NumericUpDown input, Action update)
    { Theme.StyleNumericUpDown(input); input.ValueChanged += (_, _) => { if (_isBinding) return; update(); MarkDirty(); }; }
    private static TableLayoutPanel Grid(int columns, int rows, int padding = 0, bool rounded = false)
    {
        TableLayoutPanel grid = rounded ? new SurfaceGrid() : new TableLayoutPanel();
        grid.Dock = DockStyle.Fill; grid.ColumnCount = columns; grid.RowCount = rows;
        grid.Padding = new(padding); grid.Margin = Padding.Empty; grid.BackColor = Theme.CurrentBg;
        if (columns == 1) grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        if (rows == 1) grid.RowStyles.Add(new(SizeType.Percent, 100));
        return grid;
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.CurrentBg };
    private static Label Heading(string text) => new() { Text = text, Tag = ThemeRoleSectionHeader, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.SectionFont, ForeColor = Theme.CurrentTextPrimary, AutoEllipsis = true };
    private static Label Hint(string text, int width = 0) => new() { Text = text, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width, Height = 38, Font = Theme.SmallFont, ForeColor = Theme.CurrentTextSecondary, TextAlign = ContentAlignment.MiddleLeft };
    private Button CreateButton(string text, Action action, int width)
    { var b = new SoftButton { Text = text, AccessibleName = text, Width = width, Height = 38, Margin = new(3) }; Theme.StyleButton(b); b.Click += (_, _) => action(); return b; }
    private Button CreateAccentButton(string text, Action action, int width)
    { var b = CreateButton(text, action, width); b.Tag = ThemeRoleAccentButton; Theme.StyleAccentButton(b); return b; }
    private void AddCustomStep()
    {
        if (IsCapturing || CurrentProfile is null) return;
        var step = new MacroStep { Type = MacroStepType.Move };
        using var dialog = new StepEditDialog(step) { Text = "添加动作", StartPosition = FormStartPosition.CenterParent };
        if (ShowStepEditor(dialog) != DialogResult.OK) return;
        ApplyStepEdit(step, dialog); AddStep(step);
    }
    private DialogResult ShowStepEditor(StepEditDialog dialog)
    {
        var suspended = _hotkeys?.Suspended ?? false;
        if (_hotkeys is not null) _hotkeys.Suspended = true;
        try { return dialog.ShowDialog(this); }
        finally { if (_hotkeys is not null) _hotkeys.Suspended = suspended || IsCapturing; RebuildHotkeys(); }
    }
    private static bool IsPositionStep(MacroStepType t) => t is MacroStepType.Move or MacroStepType.LeftClick or MacroStepType.RightClick or MacroStepType.DoubleClick or MacroStepType.MiddleClick;
    private void SetTargetPoint(Point point)
    {
        if (IsCapturing) return;
        if (CurrentStep is { } step && IsPositionStep(step.Type))
        { step.X = point.X; step.Y = point.Y; RefreshSteps(); MarkDirty(); }
        else AddStep(new() { Type = MacroStepType.LeftClick, X = point.X, Y = point.Y });
        SetStatus($"目标已设为 ({point.X}, {point.Y})；运行方案时才会移动鼠标");
    }
    private bool IsCapturing => _startingRecording || _recorder?.IsRecording == true || _captureCts is not null;
    private void UpdateSummary()
    {
        var steps = CurrentProfile?.Steps ?? [];
        var ms = steps.Sum(s => (long)(s.RecordedDelayMs ?? 0) + (s.Type == MacroStepType.Delay ? s.DelayMs : 0));
        _stepSummary.Text = $"操作步骤 · {steps.Count} 步" + (ms > 0 ? $" · 等待共 {ms / 1000.0:0.##} 秒" : "");
        _screenMap.Targets = steps.Where(s => IsPositionStep(s.Type)).Select(s => new Point(s.X, s.Y)).ToArray();
        _screenMap.Invalidate();
        RefreshActivityDisplay();
    }
}
