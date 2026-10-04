namespace TinyMouseMacro;

public sealed partial class MainForm
{
    private readonly Label _activityTitle = new(), _activityDetail = new(), _activityBadge = new();
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 250 };
    private readonly System.Diagnostics.Stopwatch _activityClock = new();
    private ActivityMode _activityMode;
    private string _activityName = "", _activityProfileId = "", _activityNote = "";
    private string _lastActivityTitle = "尚未运行", _lastActivityDetail = "选择方案，点击运行或按它的快捷键。";
    private bool _lastActivityFailed;
    private string _lastActivityBadge = "未运行";
    private bool _uiDisposed;
    private enum ActivityMode { Idle, Running, Stopping, Recording, Countdown, Picking }

    private Control BuildActivityBar()
    {
        var bar = Grid(3, 1, 12);
        bar.ColumnStyles.Add(new(SizeType.Absolute, 94));
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new(SizeType.Absolute, 292));
        _activityBadge.Dock = DockStyle.Fill; _activityBadge.TextAlign = ContentAlignment.MiddleCenter;
        _activityBadge.Font = Theme.SectionFont; _activityBadge.Margin = new(0, 4, 14, 4);
        bar.Controls.Add(_activityBadge, 0, 0);
        var text = Grid(1, 2); text.RowStyles.Add(new(SizeType.Absolute, 30)); text.RowStyles.Add(new(SizeType.Percent, 100));
        _activityTitle.Dock = DockStyle.Fill; _activityTitle.AutoEllipsis = true;
        _activityTitle.Font = Theme.ActivityFont; _activityTitle.TextAlign = ContentAlignment.MiddleLeft;
        _activityDetail.Dock = DockStyle.Fill; _activityDetail.AutoEllipsis = true;
        _activityDetail.Font = Theme.SmallFont; _activityDetail.TextAlign = ContentAlignment.MiddleLeft;
        text.Controls.Add(_activityTitle, 0, 0); text.Controls.Add(_activityDetail, 0, 1); bar.Controls.Add(text, 1, 0);
        var controls = Flow(); controls.FlowDirection = FlowDirection.RightToLeft; controls.Padding = new(0, 8, 0, 0);
        _cancelButton.Text = "■  停止"; _cancelButton.Size = new(110, 40); _cancelButton.Visible = true;
        _cancelButton.AccessibleName = "停止当前运行或录制";
        _tips.SetToolTip(_cancelButton, "停止当前操作 · Ctrl+Alt+F10");
        _runButton.Text = "▶  运行所选方案"; _runButton.Size = new(164, 40); _runButton.Margin = new(3);
        _runButton.Tag = ThemeRoleAccentButton; Theme.StyleAccentButton(_runButton); _runButton.Click += (_, _) => TestRun();
        controls.Controls.Add(_cancelButton); controls.Controls.Add(_runButton); bar.Controls.Add(controls, 2, 0);
        Theme.SurfaceTree(bar);
        return bar;
    }

    private void BeginActivity(ActivityMode mode, MacroProfile profile, string note = "")
    {
        _activityMode = mode; _activityName = profile.Name; _activityProfileId = profile.Id; _activityNote = note;
        _activityClock.Restart(); RefreshActivityDisplay();
    }

    private void FinishActivity(string title, string detail, bool failed = false, string badge = "已停止")
    {
        _activityMode = ActivityMode.Idle; _activityClock.Stop();
        _lastActivityTitle = title; _lastActivityDetail = detail; _lastActivityFailed = failed;
        _lastActivityBadge = failed ? "未完成" : badge;
        RefreshActivityDisplay();
    }

    private void RefreshActivityDisplay()
    {
        if (_uiDisposed || IsDisposed || Disposing) return;
        var active = _activityMode != ActivityMode.Idle;
        var color = _lastActivityFailed ? Theme.Danger : _lastActivityBadge is "已完成" or "已保存" ? Theme.Success : Theme.CurrentTextSecondary;
        var badge = _lastActivityBadge;
        var title = _lastActivityTitle; var detail = _lastActivityDetail;
        if (active)
        {
            var elapsed = _activityClock.Elapsed.ToString(@"hh\:mm\:ss");
            title = _activityName;
            color = _activityMode is ActivityMode.Recording or ActivityMode.Stopping ? Theme.Danger : Theme.CurrentAccent;
            badge = _activityMode switch { ActivityMode.Running => "运行中", ActivityMode.Stopping => "停止中", ActivityMode.Recording => "录制中", ActivityMode.Countdown => "准备录制", _ => "准备取点" };
            detail = _activityNote;
            if (_activityMode == ActivityMode.Running)
            {
                var progress = _executor.IsRunning ? _executor.Progress : null;
                detail = progress is null ? $"已用 {elapsed} · 准备开始" :
                    $"已用 {elapsed} · 第 {progress.Iteration} 轮{(progress.RepeatCount > 0 ? $" / {progress.RepeatCount}" : " · 持续循环")} · 步骤 {progress.Step} / {progress.StepCount} · {progress.Action}";
            }
            else if (_activityMode == ActivityMode.Recording)
                detail = $"已录制 {elapsed} · {_recordingProfile?.Steps.Count ?? 0} 步 · 鼠标、键盘与操作间隔";
        }
        else if (_globalPaused) { badge = "已暂停"; title = "自动运行已暂停"; detail = "快捷键与定时暂不触发，点击左下方恢复。"; color = Theme.Warning; }
        else if (_diagnosticCheck.Checked) { badge = "测试中"; title = "只测试快捷键"; detail = "当前不会运行方案，关闭运行记录后恢复。"; color = Theme.Warning; }

        _activityBadge.Text = badge; _activityBadge.ForeColor = color; _activityBadge.BackColor = Theme.Tint(color);
        _activityTitle.Text = title; _activityTitle.Font = Theme.ActivityFont; _activityTitle.ForeColor = Theme.CurrentTextPrimary;
        _activityDetail.Text = detail; _activityDetail.Font = Theme.SmallFont; _activityDetail.ForeColor = Theme.CurrentTextSecondary;
        if (_tips.GetToolTip(_activityTitle) != title) _tips.SetToolTip(_activityTitle, title);
        if (_tips.GetToolTip(_activityDetail) != detail) _tips.SetToolTip(_activityDetail, detail);
        _cancelButton.Enabled = active || IsCapturing || _executionCts is not null;
        _runButton.Enabled = !active && !IsCapturing && !_globalPaused && !_diagnosticCheck.Checked && CurrentProfile?.Steps.Count > 0;
        var recording = _activityMode is ActivityMode.Recording or ActivityMode.Countdown;
        _recordButton.Text = recording ? "■  停止录制" : "●  开始录制";
        _recordButton.Enabled = recording || (!active && !IsCapturing && CurrentProfile is not null);
        if (recording) Theme.StyleDangerButton(_recordButton); else Theme.StyleButton(_recordButton);
        if (_profileList.ActiveProfileId != (active ? _activityProfileId : null) || _profileList.ActivityText != badge)
        { _profileList.ActiveProfileId = active ? _activityProfileId : null; _profileList.ActivityText = badge; _profileList.Invalidate(); }
        _themeButton.AccessibleName = Theme.IsDark ? "切换到浅色主题" : "切换到深色主题";
        if (_tips.GetToolTip(_themeButton) != _themeButton.AccessibleName) _tips.SetToolTip(_themeButton, _themeButton.AccessibleName);
        var trayText = active ? $"TinyMouseMacro · {badge}：{_activityName}" : $"TinyMouseMacro · {badge}";
        if (!_preview && !Disposing) _trayIcon.Text = trayText.Length > 63 ? trayText[..60] + "…" : trayText;
    }
}
