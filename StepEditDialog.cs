namespace TinyMouseMacro;

internal sealed class StepEditDialog : Form
{
    private readonly SoftComboBox _typeBox = new();
    private readonly SoftComboBox _categoryBox = new();
    private readonly Label _actionHint = new();
    private readonly KeyCaptureBox _keyCapture = new();
    private readonly TableLayoutPanel _fields;
    private readonly NumericUpDown _recordedDelayBox = new() { Minimum = 0, Maximum = 86400000 };
    private readonly bool _hasRecordedTiming;
    private bool _dialogScaled;
    public int? RecordedDelayMs => _hasRecordedTiming ? (int)_recordedDelayBox.Value : null;
    private readonly NumericUpDown _xBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _yBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _delayBox = new() { Minimum = 0, Maximum = 600, Increment = 0.1m, DecimalPlaces = 3 };
    private readonly NumericUpDown _dragEndXBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _dragEndYBox = new() { Minimum = -100000, Maximum = 100000 };
    private readonly NumericUpDown _wheelDeltaBox = new() { Minimum = -12000, Maximum = 12000, Increment = 120 };
    private readonly SoftComboBox _keyPressBox = new();
    private readonly SoftComboBox _keyComboBox = new();
    private readonly FlowLayoutPanel _modifierPanel = new();
    private readonly CheckBox[] _modifiers = [new(), new(), new(), new()];
    private readonly TextBox _typeTextBox = new();
    private readonly NumericUpDown _pixelColorBox = new() { Minimum = 0, Maximum = 0xFFFFFF, Hexadecimal = true };
    private readonly NumericUpDown _pixelToleranceBox = new() { Minimum = 0, Maximum = 255, Increment = 5 };
    private readonly NumericUpDown _pixelTimeoutBox = new() { Minimum = 0, Maximum = 60000, Increment = 500 };
    private readonly NumericUpDown _searchWBox = new() { Minimum = 1, Maximum = 3840 };
    private readonly NumericUpDown _searchHBox = new() { Minimum = 1, Maximum = 2160 };
    private readonly NumericUpDown _delayMaxBox = new() { Minimum = 0, Maximum = 600, Increment = 0.1m, DecimalPlaces = 3 };
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

    public MacroStepType StepType => (_typeBox.SelectedItem as ActionChoice)?.Type ?? MacroStepType.Move;

    public int X => (int)_xBox.Value;
    public int Y => (int)_yBox.Value;
    public int DelayMs => (int)(_delayBox.Value * 1000);
    public int DragEndX => (int)_dragEndXBox.Value;
    public int DragEndY => (int)_dragEndYBox.Value;
    public int WheelDelta => (int)_wheelDeltaBox.Value;
    public int KeyCode => _keyPressBox.SelectedIndex >= 0 ? (int)(Keys)_keyPressBox.SelectedItem! : 0;
    public int KeyComboCode => _keyComboBox.SelectedIndex >= 0 ? (int)(Keys)_keyComboBox.SelectedItem! : 0;
    public uint KeyModifiers => _modifiers.Where(c => c.Checked).Aggregate(0u, (value, c) => value | (uint)c.Tag!);
    public string TextToType => _typeTextBox.Text;
    public int PixelColor => (int)_pixelColorBox.Value;
    public int PixelTolerance => (int)_pixelToleranceBox.Value;
    public int PixelTimeoutMs => (int)_pixelTimeoutBox.Value;
    public int SearchWidth => (int)_searchWBox.Value;
    public int SearchHeight => (int)_searchHBox.Value;
    public int DelayMsMax => (int)(_delayMaxBox.Value * 1000);
    public int JumpToStepIndex => (int)_jumpToStepBox.Value - 1;
    public string RunProgramPath => _runProgramPathBox.Text;
    public string RunProgramArgs => _runProgramArgsBox.Text;
    public string SoundFilePath => _soundFilePathBox.Text;
    public string ChainMacroId => _chainMacroIdBox.Text;

    public StepEditDialog(MacroStep step)
    {
        SuspendLayout();
        Text = UiText.EditStepTitle;
        _hasRecordedTiming = step.RecordedDelayMs.HasValue;
        Size = new Size(600, 590);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.BodyFont;
        Theme.ApplyToForm(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        AutoScroll = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(22) };
        root.SuspendLayout();
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 164));
        header.RowStyles.Add(new(SizeType.Absolute, 44)); header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "设置动作", Font = Theme.ActivityFont, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _categoryBox.DropDownStyle = ComboBoxStyle.DropDownList; _categoryBox.Dock = DockStyle.Fill;
        _categoryBox.Items.AddRange(["鼠标", "键盘", "延迟", "屏幕与颜色", "其他"]); Theme.StyleComboBox(_categoryBox);
        _categoryBox.AccessibleName = "动作分类"; header.Controls.Add(_categoryBox, 1, 0);
        _actionHint.Dock = DockStyle.Fill; _actionHint.Font = Theme.BodyFont; _actionHint.ForeColor = Theme.CurrentTextSecondary;
        header.Controls.Add(_actionHint, 0, 1); header.SetColumnSpan(_actionHint, 2); root.Controls.Add(header, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.BackColor = Theme.CurrentSurface;
        root.Controls.Add(scroll, 0, 1);
        var panel = _fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 22 };
        panel.SuspendLayout();
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 22; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        scroll.Controls.Add(panel);

        _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        SelectCategory(CategoryOf(step.Type), step.Type);
        _typeBox.Dock = DockStyle.Fill;

        Theme.StyleComboBox(_typeBox);

        _xBox.Value = Math.Min(_xBox.Maximum, Math.Max(_xBox.Minimum, step.X));
        _xBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_xBox);

        _yBox.Value = Math.Min(_yBox.Maximum, Math.Max(_yBox.Minimum, step.Y));
        _yBox.Dock = DockStyle.Fill;
        Theme.StyleNumericUpDown(_yBox);

        _delayBox.Value = Math.Clamp(step.DelayMs / 1000m, _delayBox.Minimum, _delayBox.Maximum);
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
        foreach (var selector in new[] { _keyPressBox, _keyComboBox })
        {
            selector.BeginUpdate();
            selector.FormattingEnabled = true; selector.MaxDropDownItems = 14;
            selector.Format += (_, e) => { if (e.ListItem is Keys value) e.Value = KeyDisplay.Name(value); };
            selector.AccessibleName = "要按下的键";
            Theme.StyleComboBox(selector);
        }
        _keyPressBox.Dock = DockStyle.Fill;
        var keyNames = new List<Keys>();
        for (var k = (int)Keys.A; k <= (int)Keys.Z; k++) keyNames.Add((Keys)k);
        for (var k = (int)Keys.D0; k <= (int)Keys.D9; k++) keyNames.Add((Keys)k);
        keyNames.AddRange([Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12,
            Keys.Escape, Keys.Tab, Keys.Space, Keys.Enter, Keys.Back, Keys.Delete, Keys.Insert,
            Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown, Keys.Up, Keys.Down, Keys.Left, Keys.Right]);
        for (var k = (int)Keys.F13; k <= (int)Keys.F24; k++) keyNames.Add((Keys)k);
        keyNames.AddRange([Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey,
            Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.LWin, Keys.RWin, Keys.CapsLock, Keys.NumLock, Keys.Scroll,
            Keys.Oemplus, Keys.OemMinus, Keys.Oemcomma, Keys.OemPeriod, Keys.OemQuestion, Keys.OemSemicolon,
            Keys.OemQuotes, Keys.OemOpenBrackets, Keys.OemCloseBrackets, Keys.OemPipe, Keys.Oemtilde]);
        for (var k = (int)Keys.NumPad0; k <= (int)Keys.Divide; k++) keyNames.Add((Keys)k);
        if (step.KeyCode > 0 && !keyNames.Contains((Keys)step.KeyCode)) keyNames.Add((Keys)step.KeyCode);
        _keyPressBox.Items.AddRange(keyNames.Cast<object>().ToArray());
        var keyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyPressBox.SelectedIndex = keyIdx >= 0 ? keyIdx : 0;

        _keyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyComboBox.Dock = DockStyle.Fill;
        _keyComboBox.Items.AddRange(keyNames.Cast<object>().ToArray());
        var comboKeyIdx = keyNames.IndexOf((Keys)step.KeyCode);
        _keyComboBox.SelectedIndex = comboKeyIdx >= 0 ? comboKeyIdx : 0;
        foreach (var selector in new[] { _keyPressBox, _keyComboBox })
        {
            selector.EndUpdate();
        }

        _modifierPanel.Dock = DockStyle.Fill; _modifierPanel.WrapContents = false;
        for (var i = 0; i < _modifiers.Length; i++)
        {
            var bit = new[] { NativeMethods.ModControl, NativeMethods.ModAlt, NativeMethods.ModShift, NativeMethods.ModWin }[i];
            var control = _modifiers[i]; control.Text = new[] { "Ctrl", "Alt", "Shift", "Win" }[i];
            control.Tag = bit; control.Checked = (step.KeyModifiers & bit) != 0;
            control.Appearance = Appearance.Button; control.TextAlign = ContentAlignment.MiddleCenter;
            control.Size = new(66, 34); control.Margin = new(0, 0, 8, 0); control.FlatStyle = FlatStyle.Flat;
            void StyleModifier() { control.BackColor = control.Checked ? Theme.CurrentListSelectedBg : Theme.CurrentSurface; control.ForeColor = control.Checked ? Theme.CurrentAccent : Theme.CurrentTextPrimary; control.FlatAppearance.BorderColor = control.Checked ? Theme.CurrentAccent : Theme.CurrentBorder; }
            control.CheckedChanged += (_, _) => StyleModifier(); StyleModifier(); _modifierPanel.Controls.Add(control);
        }
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

        _delayMaxBox.Value = Math.Clamp(step.DelayMsMax / 1000m, _delayMaxBox.Minimum, _delayMaxBox.Maximum);
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
        _delayLabel = CreateDialogLabel("延迟（秒）");
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
        _delayMaxLabel = CreateDialogLabel("最长延迟（秒）");
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
        var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        keyPanel.RowStyles.Add(new(SizeType.Percent, 50)); keyPanel.RowStyles.Add(new(SizeType.Percent, 50));
        _keyCapture.Dock = DockStyle.Fill; Theme.StyleTextBox(_keyCapture);
        _keyCapture.KeyCaptured += ApplyCapturedKey;
        keyPanel.Controls.Add(_keyCapture, 0, 0);
        var keySelectors = new Panel { Dock = DockStyle.Fill };
        keySelectors.Controls.Add(_keyPressBox); keySelectors.Controls.Add(_keyComboBox); keyPanel.Controls.Add(keySelectors, 0, 1);
        panel.Controls.Add(keyPanel, 1, 7);
        panel.Controls.Add(_modifierLabel, 0, 8);
        panel.Controls.Add(_modifierPanel, 1, 8);
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

        var cancelBtn = new SoftButton { Text = UiText.Cancel, Width = 90, Height = 38 };
        Theme.StyleButton(cancelBtn);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var okBtn = new SoftButton { Text = "保存动作", Width = 108, Height = 38 };
        Theme.StyleAccentButton(okBtn);
        okBtn.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        buttonPanel.Controls.Add(cancelBtn);
        buttonPanel.Controls.Add(okBtn);
        var timingLabel = CreateDialogLabel("录制间隔（毫秒）");
        _recordedDelayBox.Value = step.RecordedDelayMs ?? 0;
        _recordedDelayBox.Dock = DockStyle.Fill; Theme.StyleNumericUpDown(_recordedDelayBox);
        timingLabel.Visible = _recordedDelayBox.Visible = _hasRecordedTiming;
        panel.Controls.Add(timingLabel, 0, 21); panel.Controls.Add(_recordedDelayBox, 1, 21);
        root.Controls.Add(buttonPanel, 0, 2);
        foreach (Control field in panel.Controls) field.Margin = new Padding(4, 7, 4, 7);
        _typeBox.SelectedIndexChanged += (_, _) => UpdateFieldVisibility();
        _categoryBox.SelectedIndexChanged += (_, _) => SelectCategory(_categoryBox.SelectedIndex);
        Theme.SurfaceTree(panel);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        UpdateFieldVisibility();
        panel.ResumeLayout(false); root.ResumeLayout(false); ResumeLayout(false);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); PerformAutoScale();
        _dialogScaled = true;
    }

    private void UpdateFieldVisibility()
    {
        _fields.SuspendLayout();
        var type = StepType;
        _actionHint.Text = DescriptionOf(type);
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
        _modifierLabel.Visible = _modifierPanel.Visible = isCombo;
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
                _fields.RowStyles[i].Height = visibleRows[i] ? (i == 7 ? 88 : 48) * (_dialogScaled ? DeviceDpi / 96f : 1) : 0;
            }
        }
        _fields?.ResumeLayout(true);
    }

    internal void ApplyCapturedKey(Keys key, uint modifiers)
    {
        if (StepType is not (MacroStepType.KeyPress or MacroStepType.KeyCombo)) return;
        if (modifiers != 0 && StepType == MacroStepType.KeyPress)
            _typeBox.SelectedItem = Actions.First(a => a.Type == MacroStepType.KeyCombo);
        foreach (var selector in new[] { _keyPressBox, _keyComboBox })
        { if (!selector.Items.Contains(key)) selector.Items.Add(key); selector.SelectedItem = key; }
        if (StepType == MacroStepType.KeyCombo)
            foreach (var modifier in _modifiers) modifier.Checked = (modifiers & (uint)modifier.Tag!) != 0;
        _keyCapture.Text = modifiers == 0 ? KeyDisplay.Name(key) : HotkeyParser.Normalize(modifiers, (uint)key);
    }

    private static Label CreateDialogLabel(string text)
    {
        var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        label.Font = Theme.BodyFont; label.ForeColor = Theme.CurrentTextSecondary;
        return label;
    }

    private sealed record ActionChoice(MacroStepType Type, string Name)
    { public override string ToString() => Name; }
    private static readonly ActionChoice[] Actions = [
        new(MacroStepType.LeftClick, "左键点击"), new(MacroStepType.RightClick, "右键点击"), new(MacroStepType.DoubleClick, "双击"),
        new(MacroStepType.MiddleClick, "中键点击"), new(MacroStepType.Move, "移动到位置"), new(MacroStepType.Drag, "拖拽"),
        new(MacroStepType.MouseWheel, "滚动滚轮"), new(MacroStepType.MoveRelative, "移动一段距离"),
        new(MacroStepType.KeyPress, "按下一个键"), new(MacroStepType.KeyCombo, "组合键"), new(MacroStepType.TypeText, "输入一段文字"),
        new(MacroStepType.Delay, "固定延迟"), new(MacroStepType.RandomDelay, "随机延迟"),
        new(MacroStepType.Screenshot, "截取屏幕区域"), new(MacroStepType.WaitPixel, "等待颜色出现"),
        new(MacroStepType.FindPixel, "查找颜色并移动"), new(MacroStepType.JumpIfPixel, "匹配颜色后跳转"),
        new(MacroStepType.RunProgram, "打开程序"), new(MacroStepType.PlaySound, "播放声音"), new(MacroStepType.ChainMacro, "运行另一个方案")];
    private static int CategoryOf(MacroStepType type) => type switch {
        MacroStepType.KeyPress or MacroStepType.KeyCombo or MacroStepType.TypeText => 1,
        MacroStepType.Delay or MacroStepType.RandomDelay => 2,
        MacroStepType.Screenshot or MacroStepType.WaitPixel or MacroStepType.FindPixel or MacroStepType.JumpIfPixel => 3,
        MacroStepType.RunProgram or MacroStepType.PlaySound or MacroStepType.ChainMacro => 4, _ => 0 };
    private void SelectCategory(int category, MacroStepType? selected = null)
    {
        _typeBox.BeginUpdate();
        _categoryBox.SelectedIndex = category;
        _typeBox.Items.Clear();
        _typeBox.Items.AddRange(Actions.Where(a => CategoryOf(a.Type) == category).Cast<object>().ToArray());
        _typeBox.SelectedItem = Actions.FirstOrDefault(a => a.Type == selected);
        if (_typeBox.SelectedIndex < 0) _typeBox.SelectedIndex = 0;
        _typeBox.EndUpdate();
    }
    private static string DescriptionOf(MacroStepType type) => type switch {
        MacroStepType.KeyPress => "选择要按下并松开的键，包括 Alt、Ctrl、Shift 和功能键。",
        MacroStepType.KeyCombo => "选一个主键，再点选需要同时按住的 Ctrl、Alt、Shift 或 Win。",
        MacroStepType.TypeText => "运行时依次输入这段文字。",
        MacroStepType.Delay or MacroStepType.RandomDelay => "在继续下一个动作之前等待。例如 0.5 秒就是半秒。",
        MacroStepType.Screenshot => "把指定区域保存为图片。",
        MacroStepType.WaitPixel => "等指定位置出现目标颜色，再继续执行。",
        MacroStepType.FindPixel => "在区域内寻找目标颜色，找到后把鼠标移过去。",
        MacroStepType.JumpIfPixel => "位置颜色符合条件时，转到指定步骤。",
        MacroStepType.RunProgram => "打开指定程序，可填写启动参数。",
        MacroStepType.PlaySound => "播放本地音频文件。",
        MacroStepType.ChainMacro => "本轮结束后继续运行指定方案。",
        _ => "位置使用本机多屏坐标；保存后在运行方案时执行。" };
}
