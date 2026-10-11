namespace TinyMouseMacro;

public sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer _windowPlacementTimer = new() { Interval = 500 };
    private WindowPlacementStore _windowPlacementStore = null!;
    private WindowPlacement? _normalWindowPlacement;
    private WindowPlacement? _unchangedWindowPlacement;
    private bool _windowPlacementReady;
    private bool _windowWasMaximized;

    private void InitializeWindowPlacement()
    {
        if (_preview) return;
        RestoreWindowPlacement();
        _normalWindowPlacement ??= CaptureNormalWindowPlacement();
        _windowPlacementReady = true;
        if (!File.Exists(_windowPlacementStore.FilePath)) SaveWindowPlacement();
    }

    private void RestoreWindowPlacement()
    {
        var saved = _windowPlacementStore.Load();
        if (saved is null) return;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == saved.Monitor)
            ?? Screen.FromRectangle(new(saved.X, saved.Y, saved.Width, saved.Height));
        StartPosition = FormStartPosition.Manual;
        // Move to the target monitor first so WinForms applies its DPI before sizing.
        Location = screen.WorkingArea.Location;
        Bounds = saved.FitToWorkingArea(screen.WorkingArea, DeviceDpi, MinimumSize);
        _normalWindowPlacement = CaptureNormalWindowPlacement();
        _windowWasMaximized = saved.Maximized;
        _unchangedWindowPlacement = _normalWindowPlacement with { Maximized = saved.Maximized };
        if (saved.Maximized) WindowState = FormWindowState.Maximized;
    }

    private WindowPlacement CaptureNormalWindowPlacement() => new(Left, Top, Width, Height, DeviceDpi,
        Monitor: Screen.FromControl(this).DeviceName);

    private void QueueWindowPlacementSave(object? sender, EventArgs e)
    {
        if (!_windowPlacementReady || !Visible || _uiDisposed) return;
        if (WindowState == FormWindowState.Normal) _normalWindowPlacement = CaptureNormalWindowPlacement();
        if (WindowState != FormWindowState.Minimized) _windowWasMaximized = WindowState == FormWindowState.Maximized;
        _windowPlacementTimer.Stop();
        _windowPlacementTimer.Start();
    }

    private void SaveWindowPlacement()
    {
        if (!_windowPlacementReady || _uiDisposed) return;
        _windowPlacementTimer.Stop();
        if (WindowState == FormWindowState.Normal && Visible) _normalWindowPlacement = CaptureNormalWindowPlacement();
        if (WindowState != FormWindowState.Minimized) _windowWasMaximized = WindowState == FormWindowState.Maximized;
        if (_normalWindowPlacement is not { IsValid: true } normal) return;
        var placement = normal with { Maximized = _windowWasMaximized };
        // Loading on a temporarily different monitor/DPI must not itself overwrite the saved layout.
        if (placement == _unchangedWindowPlacement) return;
        try { _windowPlacementStore.Save(placement); _unchangedWindowPlacement = placement; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("窗口位置保存失败：" + ex.Message); }
    }
}
