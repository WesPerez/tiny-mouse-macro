using System.Drawing.Drawing2D;

namespace TinyMouseMacro;

internal static class RoundedGeometry
{
    internal static GraphicsPath Path(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) return path;
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
    internal static void Clip(Control control, int radius)
    {
        using var path = Path(control.ClientRectangle, radius * control.DeviceDpi / 96f);
        var old = control.Region; control.Region = new Region(path); old?.Dispose();
    }
}

internal class SoftButton : Button
{
    private bool _hover, _pressed;
    public SoftButton() { DoubleBuffered = true; FlatStyle = FlatStyle.Flat; }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.CurrentBg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = !Enabled ? Theme.CurrentCard : _pressed ? Theme.CurrentListSelectedBg : _hover ? FlatAppearance.MouseOverBackColor : BackColor;
        using var path = RoundedGeometry.Path(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * DeviceDpi / 96f);
        using var fill = new SolidBrush(color); g.FillPath(fill, path);
        using var pen = new Pen(Focused ? Theme.CurrentAccent : Enabled ? FlatAppearance.BorderColor : Theme.CurrentBorder);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.CurrentTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class SurfaceGrid : TableLayoutPanel
{
    public SurfaceGrid() { DoubleBuffered = true; }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RoundedGeometry.Clip(this, 12); }
}

internal sealed class PillLabel : Label
{
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RoundedGeometry.Clip(this, 10); }
}

// Native combo painting retains keyboard navigation, accessibility and WM_PRINT support.
// Theme supplies the same owner-drawn text/background for both the face and the popup.
internal class SoftComboBox : ComboBox
{
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RoundedGeometry.Clip(this, 5); }
}

// Keep a native single-line editor (including its keyboard behavior), centered in
// a field with the same outside height as the adjacent native dropdown.
internal sealed class InputSurface : Panel
{
    public InputSurface() { DoubleBuffered = true; TabStop = false; }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.Enter += (_, _) => Invalidate();
        e.Control.Leave += (_, _) => Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var inset = (int)Math.Round(10 * DeviceDpi / 96f);
        foreach (Control child in Controls)
        {
            child.Dock = DockStyle.None;
            var height = child is TextBoxBase textBox ? textBox.PreferredHeight : child.PreferredSize.Height;
            child.SetBounds(inset, Math.Max(1, (ClientSize.Height - height) / 2),
                Math.Max(1, ClientSize.Width - 2 * inset), height);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach (Control child in Controls) if (child.Visible && child.CanFocus) { child.Focus(); break; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.CurrentBg);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedGeometry.Path(new RectangleF(.5f, .5f, Width - 1, Height - 1), 5 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Theme.CurrentInputBg);
        using var pen = new Pen(ContainsFocus ? Theme.CurrentAccent : Theme.CurrentBorder);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class AppMenu : ContextMenuStrip
{
    public AppMenu()
    {
        Font = Theme.BodyFont; ShowImageMargin = false; Padding = new(6); Renderer = new AppMenuRenderer();
        Opening += (_, _) => { BackColor = Theme.CurrentSurface; ForeColor = Theme.CurrentTextPrimary; foreach (ToolStripItem item in Items) item.Padding = new(10, 7, 10, 7); };
    }
    private sealed class AppMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.CurrentSurface);
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedGeometry.Path(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 6);
            using var fill = new SolidBrush(Theme.CurrentListSelectedBg); e.Graphics.FillPath(fill, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? Theme.CurrentTextPrimary : Theme.CurrentTextMuted; base.OnRenderItemText(e); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { using var pen = new Pen(Theme.CurrentBorder); e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    }
}
