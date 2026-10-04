using System.Drawing.Drawing2D;

namespace TinyMouseMacro;

internal sealed class ScreenMap : Control
{
    private Point? _selectedPoint;
    private int _keyboardScreen;
    public Point[] Targets { get; set; } = [];
    public Point? SelectedPoint { get => _selectedPoint; set { _selectedPoint = value; Invalidate(); } }
    public event Action<Point>? PointChosen;
    public ScreenMap()
    {
        DoubleBuffered = true; TabStop = true; Cursor = Cursors.Cross;
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
    }
    private (Rectangle Bounds, float Scale, PointF Origin) Projection()
    {
        var bounds = Screen.AllScreens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        var scale = Math.Min(Math.Max(1, Width - 40) / (float)bounds.Width, Math.Max(1, Height - 36) / (float)bounds.Height);
        return (bounds, scale, new((Width - bounds.Width * scale) / 2, (Height - bounds.Height * scale) / 2));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Theme.CurrentSurface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (bounds, scale, origin) = Projection();
        PointF Map(Point p) => new(origin.X + (p.X - bounds.X) * scale, origin.Y + (p.Y - bounds.Y) * scale);
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i]; var pos = Map(screen.Bounds.Location);
            var rect = new RectangleF(pos.X, pos.Y, screen.Bounds.Width * scale, screen.Bounds.Height * scale);
            using var fill = new SolidBrush(Theme.CurrentInputBg);
            using var line = new Pen(Focused && i == _keyboardScreen ? Theme.CurrentAccent : Theme.CurrentBorder, 2);
            e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(line, rect.X, rect.Y, rect.Width, rect.Height);
            TextRenderer.DrawText(e.Graphics, $"屏幕 {i + 1}{(screen.Primary ? " · 主屏" : "")}", Theme.SectionFont,
                Rectangle.Round(rect), Theme.CurrentTextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        foreach (var point in Targets)
        {
            var p = Map(point); using var fill = new SolidBrush(Theme.CurrentAccent);
            e.Graphics.FillEllipse(fill, p.X - 4, p.Y - 4, 8, 8);
        }
        if (_selectedPoint is Point selected)
        { var p = Map(selected); using var pen = new Pen(Theme.CurrentAccent, 2); e.Graphics.DrawEllipse(pen, p.X - 8, p.Y - 8, 16, 16); }
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e); if (e.Button != MouseButtons.Left) return;
        Focus(); var (bounds, scale, origin) = Projection();
        var point = new Point(bounds.X + (int)((e.X - origin.X) / scale), bounds.Y + (int)((e.Y - origin.Y) / scale));
        if (Screen.AllScreens.Any(s => s.Bounds.Contains(point))) { SelectedPoint = point; PointChosen?.Invoke(point); }
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); var screens = Screen.AllScreens;
        if (e.KeyCode is Keys.Left or Keys.Right) { _keyboardScreen = (_keyboardScreen + screens.Length + (e.KeyCode == Keys.Left ? -1 : 1)) % screens.Length; Invalidate(); e.Handled = true; }
        if (e.KeyCode == Keys.Enter) { var r = screens[_keyboardScreen].Bounds; var p = new Point(r.X + r.Width / 2, r.Y + r.Height / 2); SelectedPoint = p; PointChosen?.Invoke(p); e.Handled = true; }
    }
}
