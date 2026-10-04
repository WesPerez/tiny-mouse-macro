namespace TinyMouseMacro;

public sealed partial class MainForm
{
    private readonly Label _saveState = new(), _stepSummary = new(), _backendLabel = new(), _lastSignal = new();
    private readonly Button _recordButton = new(), _pauseButton = new();
    private readonly CheckBox _rawInputCheck = new(), _diagnosticCheck = new(), _recordKeyboardCheck = new();
    private readonly CheckBox _releaseRemoteCheck = new(), _externalTriggerCheck = new();
    private ExternalTrigger? _externalTrigger;
    private readonly ScreenMap _screenMap = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 400 };
    private readonly TabControl _workspaceTabs = new();
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
        var root = Grid(1, 3, 18);
        root.RowStyles.Add(new(SizeType.Absolute, 76));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 38));
        Controls.Add(root);

        var heading = Grid(2, 1);
        heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new(SizeType.Absolute, 430));
        var branding = new Panel { Dock = DockStyle.Fill };
        branding.Controls.Add(new Label { Text = "TinyMouseMacro", AutoSize = true, Location = new(0, 0), Font = Theme.TitleFont, Tag = "Title", ForeColor = Theme.CurrentTextPrimary });
        branding.Controls.Add(new Label { Text = "多屏切换与操作录制，让常用动作回到一个按键。", AutoSize = true, Location = new(2, 38), Tag = "Body", Font = Theme.BodyFont, ForeColor = Theme.CurrentTextSecondary });
        heading.Controls.Add(branding, 0, 0);
        var primary = Flow();
        primary.FlowDirection = FlowDirection.RightToLeft;
        primary.Padding = new(0, 9, 0, 0);
        _cancelButton.Text = "停止  Ctrl+Alt+F10";
        _cancelButton.Width = 172; _cancelButton.Height = 38; _cancelButton.Visible = true;
        primary.Controls.Add(_cancelButton);
        primary.Controls.Add(CreateAccentButton("▶  运行方案", TestRun, 114));
        primary.Controls.Add(CreateButton("保存  Ctrl+S", SaveAndReload, 116));
        heading.Controls.Add(primary, 1, 0); root.Controls.Add(heading, 0, 0);

        var body = Grid(2, 1);
        body.ColumnStyles.Add(new(SizeType.Absolute, 238));
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 1);
        body.Controls.Add(BuildSidebar(), 0, 0);
        var workspace = Grid(1, 2); workspace.Padding = new(18, 0, 0, 0);
        workspace.RowStyles.Add(new(SizeType.Absolute, 90));
        workspace.RowStyles.Add(new(SizeType.Percent, 100));
        workspace.Controls.Add(BuildIdentity(), 0, 0);
        _workspaceTabs.Dock = DockStyle.Fill; _workspaceTabs.Font = Theme.BodyFont;
        _workspaceTabs.Padding = new Point(18, 9);
        TabPage compose = Page("方案编排"), options = Page("运行选项"), diagnostics = Page("快捷键诊断");
        compose.Controls.Add(BuildComposer()); options.Controls.Add(BuildOptions()); diagnostics.Controls.Add(BuildDiagnostics());
        _workspaceTabs.TabPages.AddRange([compose, options, diagnostics]);
        workspace.Controls.Add(_workspaceTabs, 0, 1); body.Controls.Add(workspace, 1, 0);

        var footer = Grid(2, 1);
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 250));
        _statusLabel.Dock = DockStyle.Fill; _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft; _statusLabel.Tag = ThemeRoleStatus;
        Theme.StyleStatusBar(_statusLabel); footer.Controls.Add(_statusLabel, 0, 0);
        _saveState.Dock = DockStyle.Fill; _saveState.TextAlign = ContentAlignment.MiddleRight;
        _saveState.Font = Theme.BodyFont; _saveState.ForeColor = Theme.CurrentTextSecondary;
        footer.Controls.Add(_saveState, 1, 0); root.Controls.Add(footer, 0, 2);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAll(); };
        _recordUiTimer.Tick += (_, _) => FlushRecording();
        ResumeLayout(true);
    }

    private Control BuildSidebar()
    {
        var panel = Grid(1, 5, 12); panel.BackColor = Theme.CurrentSurface;
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
        var menu = new ContextMenuStrip(); more.ContextMenuStrip = menu; more.Disposed += (_, _) => menu.Dispose();
        menu.Items.Add("复制当前方案", null, (_, _) => DuplicateProfile());
        menu.Items.Add("导入方案…", null, (_, _) => ImportProfile());
        menu.Items.Add("导出当前方案…", null, (_, _) => ExportProfile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除当前方案…", null, (_, _) => DeleteProfile());
        menu.Items.Add("切换明暗主题", null, (_, _) => ToggleTheme());
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height)); buttons.Controls.Add(more);
        panel.Controls.Add(buttons, 0, 3);
        var bottom = Grid(1, 2); bottom.RowStyles.Add(new(SizeType.Absolute, 40)); bottom.RowStyles.Add(new(SizeType.Percent, 100));
        _pauseButton.Text = "暂停全部快捷键"; _pauseButton.Dock = DockStyle.Fill; Theme.StyleButton(_pauseButton);
        _pauseButton.Click += (_, _) => ToggleGlobalPause(); bottom.Controls.Add(_pauseButton, 0, 0);
        bottom.Controls.Add(Hint("Ctrl+Alt+P 暂停 / 恢复"), 0, 1);
        panel.Controls.Add(bottom, 0, 4);
        return panel;
    }

    private Control BuildIdentity()
    {
        var panel = Grid(3, 2);
        panel.ColumnStyles.Add(new(SizeType.Percent, 40)); panel.ColumnStyles.Add(new(SizeType.Absolute, 170));
        panel.ColumnStyles.Add(new(SizeType.Percent, 60)); panel.RowStyles.Add(new(SizeType.Absolute, 28)); panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(Heading("方案名称"), 0, 0); panel.Controls.Add(Heading("触发方式"), 1, 0); panel.Controls.Add(Heading("按下快捷键即可设置"), 2, 0);
        Theme.StyleTextBox(_nameBox); _nameBox.Dock = DockStyle.Fill; _nameBox.AccessibleName = "方案名称";
        _nameBox.TextChanged += (_, _) =>
        {
            if (_isBinding || CurrentProfile is null) return;
            CurrentProfile.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? UiText.Untitled : _nameBox.Text.Trim();
            _profileList.Invalidate(); MarkDirty();
        };
        panel.Controls.Add(_nameBox, 0, 1);
        _triggerTypeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _triggerTypeBox.Items.AddRange(["快捷键（推荐）", "直接按键监听", "鼠标侧键 / 中键"]);
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
        _tips.SetToolTip(inputs, "推荐使用不与其他应用冲突的组合键。单独的字母按键会影响正常输入。F13–F24 适合映射到专用键。" );
        return panel;
    }

    private Control BuildComposer()
    {
        var panel = Grid(1, 5, 12);
        panel.RowStyles.Add(new(SizeType.Absolute, 36)); panel.RowStyles.Add(new(SizeType.Absolute, 148));
        panel.RowStyles.Add(new(SizeType.Absolute, 52)); panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 46));
        var title = Grid(2, 1); title.ColumnStyles.Add(new(SizeType.Percent, 100)); title.ColumnStyles.Add(new(SizeType.Absolute, 300));
        title.Controls.Add(Heading("01   选择目标位置"), 0, 0);
        _livePositionLabel.Dock = DockStyle.Fill; _livePositionLabel.TextAlign = ContentAlignment.MiddleRight;
        _livePositionLabel.Font = Theme.MonoBodyFont; _livePositionLabel.ForeColor = Theme.CurrentTextSecondary;
        title.Controls.Add(_livePositionLabel, 1, 0); panel.Controls.Add(title, 0, 0);
        _screenMap.Dock = DockStyle.Fill; _screenMap.AccessibleName = "显示器布局：点击设置目标位置，不会移动真实鼠标";
        _screenMap.PointChosen += point => SetTargetPoint(point); panel.Controls.Add(_screenMap, 0, 1);
        var actions = Flow();
        actions.Controls.Add(CreateButton("◎  3 秒后取点", async () => await CapturePointAfterDelayAsync(true), 142));
        actions.Controls.Add(CreateButton("＋ 等待", AddDelayStep, 95));
        _recordButton.Text = "●  录制操作"; _recordButton.Width = 133; _recordButton.Height = 38; _recordButton.Margin = new(3);
        Theme.StyleButton(_recordButton); _recordButton.Click += (_, _) => ToggleRecording(); actions.Controls.Add(_recordButton);
        _recordKeyboardCheck.Text = "含键盘"; _recordKeyboardCheck.AutoSize = true; _recordKeyboardCheck.Margin = new(10, 11, 3, 0);
        actions.Controls.Add(_recordKeyboardCheck);
        actions.Controls.Add(CreateButton("更多动作…", AddCustomStep, 114)); panel.Controls.Add(actions, 0, 2);
        var steps = Grid(1, 2); steps.RowStyles.Add(new(SizeType.Absolute, 36)); steps.RowStyles.Add(new(SizeType.Percent, 100));
        _stepSummary.Dock = DockStyle.Fill; _stepSummary.Font = Theme.SectionFont; _stepSummary.ForeColor = Theme.CurrentTextPrimary;
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
        edit.Controls.Add(Hint("双击编辑 · 拖动排序", 305));
        panel.Controls.Add(edit, 0, 4); return panel;
    }

    private Control BuildOptions()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panel = Grid(2, 9, 20); panel.Dock = DockStyle.Top; panel.AutoSize = true; scroll.Controls.Add(panel);
        panel.ColumnStyles.Add(new(SizeType.Absolute, 210)); panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) panel.RowStyles.Add(new(SizeType.Absolute, 52));
        panel.RowStyles.Add(new(SizeType.Absolute, 56)); panel.RowStyles.Add(new(SizeType.Absolute, 80)); panel.RowStyles.Add(new(SizeType.AutoSize));
        _enabledCheckBox.Text = "允许快捷键与定时触发"; _enabledCheckBox.AutoSize = true;
        _enabledCheckBox.CheckedChanged += (_, _) => { if (_isBinding || CurrentProfile is null) return; CurrentProfile.Enabled = _enabledCheckBox.Checked; MarkDirty(); };
        Option(0, "启用方案", _enabledCheckBox);
        SetupNumber(_repeatCountBox, () => { if (CurrentProfile is { } p) p.RepeatCount = (int)_repeatCountBox.Value; });
        SetupNumber(_repeatIntervalBox, () => { if (CurrentProfile is { } p) p.RepeatIntervalMs = (int)_repeatIntervalBox.Value; });
        SetupNumber(_speedBox, () => { if (CurrentProfile is { } p) p.SpeedMultiplier = (double)_speedBox.Value; });
        SetupNumber(_scheduleIntervalBox, () => { if (CurrentProfile is { } p) { p.ScheduleIntervalMinutes = (int)_scheduleIntervalBox.Value; p.ScheduleNextRun = null; } });
        Option(1, "重复次数（0 = 持续）", _repeatCountBox); Option(2, "每轮间隔（毫秒）", _repeatIntervalBox);
        Option(3, "回放速度（倍）", _speedBox); Option(4, "定时运行（分钟；0 = 关闭）", _scheduleIntervalBox);
        _targetWindowBox.PlaceholderText = "可选：运行前激活的完整窗口标题"; Theme.StyleTextBox(_targetWindowBox);
        _targetWindowBox.TextChanged += (_, _) => { if (_isBinding || CurrentProfile is null) return; CurrentProfile.TargetWindowTitle = _targetWindowBox.Text; MarkDirty(); };
        Option(5, "目标窗口", _targetWindowBox);
        _releaseRemoteCheck.Text = "切屏前尝试释放 VMware / UU 鼠标捕获"; _releaseRemoteCheck.AutoSize = true;
        _releaseRemoteCheck.CheckedChanged += (_, _) => { if (_isBinding || CurrentProfile is null) return; CurrentProfile.ReleaseRemoteInput = _releaseRemoteCheck.Checked; MarkDirty(); };
        Option(6, "一键切回宿主", _releaseRemoteCheck);
        var note = Hint("常用切屏方案保持：重复 1 次、速度 1 倍、定时关闭。\n录制保留操作间隔；键盘录制默认关闭。停止键：Ctrl+Alt+F10。");
        panel.Controls.Add(note, 0, 7); panel.SetColumnSpan(note, 2);
        return scroll;
        void Option(int row, string name, Control input)
        { panel.Controls.Add(Heading(name), 0, row); input.Dock = DockStyle.Top; input.Margin = new(4, 9, 4, 4); panel.Controls.Add(input, 1, row); }
    }

    private Control BuildDiagnostics()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panel = Grid(1, 8, 18); panel.Dock = DockStyle.Top; panel.AutoSize = true;
        scroll.Controls.Add(panel);
        panel.RowStyles.Add(new(SizeType.Absolute, 40)); panel.RowStyles.Add(new(SizeType.Absolute, 46));
        panel.RowStyles.Add(new(SizeType.Absolute, 74)); panel.RowStyles.Add(new(SizeType.Absolute, 52));
        panel.RowStyles.Add(new(SizeType.Absolute, 68)); panel.RowStyles.Add(new(SizeType.Absolute, 48)); panel.RowStyles.Add(new(SizeType.Absolute, 32)); panel.RowStyles.Add(new(SizeType.Absolute, 170));
        _backendLabel.Dock = DockStyle.Fill; _backendLabel.Font = Theme.SectionFont; _backendLabel.ForeColor = Theme.CurrentTextPrimary;
        panel.Controls.Add(_backendLabel, 0, 0);
        var tools = Flow();
        _diagnosticCheck.Text = "只检测快捷键，不执行方案"; _diagnosticCheck.AutoSize = true; _diagnosticCheck.Margin = new(4, 10, 16, 0);
        _diagnosticCheck.CheckedChanged += (_, _) => { if (_hotkeys is not null) _hotkeys.DiagnosticMode = _diagnosticCheck.Checked; };
        tools.Controls.Add(_diagnosticCheck); tools.Controls.Add(CreateButton("重新连接监听", () => RebuildHotkeys(true), 144)); panel.Controls.Add(tools, 0, 1);
        _rawInputCheck.Text = "启用后台输入补充（VMware / UU 远程）"; _rawInputCheck.AutoSize = true;
        _rawInputCheck.CheckedChanged += (_, _) => { if (_isBinding) return; SavePreferences(); RebuildHotkeys(); };
        var raw = Grid(1, 2); raw.RowStyles.Add(new(SizeType.Absolute, 30)); raw.RowStyles.Add(new(SizeType.Percent, 100));
        raw.Controls.Add(_rawInputCheck, 0, 0); raw.Controls.Add(Hint("此方式不拦截按键，远端可能同时收到同一快捷键。建议使用 F13–F24 等专用映射键。"), 0, 1);
        panel.Controls.Add(raw, 0, 2);
        _lastSignal.Dock = DockStyle.Fill; _lastSignal.Font = Theme.BodyFont; _lastSignal.ForeColor = Theme.CurrentAccent;
        _lastSignal.Text = "尚未检测。勾选“只检测”，切到 VMware / UU 后按方案快捷键，再回来查看来源。";
        panel.Controls.Add(_lastSignal, 0, 3);
        panel.Controls.Add(Hint("宿主 Windows 仍收到输入时，兼容监听可帮助触发；USB 键盘直通、驱动独占或安全桌面无法保证。\nVMware 可先用 Ctrl+Alt 释放输入。跨机器执行需要各系统自己的坐标；这里的目标始终是宿主显示器。"), 0, 4);
        var external = Flow();
        _externalTriggerCheck.Text = "允许 iCUE 启动切屏方案"; _externalTriggerCheck.AutoSize = true; _externalTriggerCheck.Margin = new(4, 10, 12, 0);
        _externalTriggerCheck.CheckedChanged += (_, _) => { if (_isBinding) return; SavePreferences(); ConfigureExternalTrigger(); };
        _tips.SetToolTip(_externalTriggerCheck, "允许当前桌面的本地程序触发已启用的一步移动／点击方案；不允许外部执行脚本、程序或任意命令。只在使用 iCUE 启动器时开启。");
        external.Controls.Add(_externalTriggerCheck); external.Controls.Add(CreateButton("生成当前方案启动器…", ExportLauncher, 186));
        panel.Controls.Add(external, 0, 5);
        panel.Controls.Add(Heading("最近活动"), 0, 6);
        _logList.Dock = DockStyle.Fill; _logList.IntegralHeight = false; Theme.StyleListBox(_logList);
        panel.Controls.Add(_logList, 0, 7); return scroll;
    }

    private void SetupNumber(NumericUpDown input, Action update)
    { Theme.StyleNumericUpDown(input); input.ValueChanged += (_, _) => { if (_isBinding) return; update(); MarkDirty(); }; }
    private static TableLayoutPanel Grid(int columns, int rows, int padding = 0)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows,
            Padding = new(padding), Margin = Padding.Empty, BackColor = Theme.CurrentBg };
        if (columns == 1) grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        if (rows == 1) grid.RowStyles.Add(new(SizeType.Percent, 100));
        return grid;
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.CurrentBg };
    private static TabPage Page(string text) => new(text) { BackColor = Theme.CurrentBg, Padding = Padding.Empty };
    private static Label Heading(string text) => new() { Text = text, Tag = ThemeRoleSectionHeader, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.SectionFont, ForeColor = Theme.CurrentTextPrimary, AutoEllipsis = true };
    private static Label Hint(string text, int width = 0) => new() { Text = text, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width, Height = 38, Font = Theme.SmallFont, ForeColor = Theme.CurrentTextSecondary, TextAlign = ContentAlignment.MiddleLeft };
    private Button CreateButton(string text, Action action, int width)
    { var b = new Button { Text = text, AccessibleName = text, Width = width, Height = 38, Margin = new(3) }; Theme.StyleButton(b); b.Click += (_, _) => action(); return b; }
    private Button CreateAccentButton(string text, Action action, int width)
    { var b = CreateButton(text, action, width); b.Tag = ThemeRoleAccentButton; Theme.StyleAccentButton(b); return b; }
    private void AddCustomStep()
    {
        if (IsCapturing || CurrentProfile is null) return;
        var step = new MacroStep { Type = MacroStepType.Move };
        using var dialog = new StepEditDialog(step) { StartPosition = FormStartPosition.CenterParent };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyStepEdit(step, dialog); AddStep(step);
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
        _stepSummary.Text = $"02   动作时间线     {steps.Count} 步 · 间隔 {ms / 1000.0:0.##} 秒";
        _screenMap.Targets = steps.Where(s => IsPositionStep(s.Type)).Select(s => new Point(s.X, s.Y)).ToArray();
        _screenMap.Invalidate();
    }
}
