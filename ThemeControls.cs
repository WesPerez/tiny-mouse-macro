using System.Drawing.Drawing2D;

namespace TinyMouseMacro;

internal sealed class ProfileListBox : ListBox
{
    public string? ActiveProfileId { get; set; }
    public string ActivityText { get; set; } = "";
}

internal sealed class ThemeToggleButton : Button
{
    public ThemeToggleButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand; DoubleBuffered = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.CurrentBg); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(Width, Height); var scale = size / 42f;
        e.Graphics.TranslateTransform(Width / 2f, Height / 2f); e.Graphics.ScaleTransform(scale, scale);
        using var pen = new Pen(Theme.CurrentTextPrimary, 1.8f);
        using var fill = new SolidBrush(Theme.CurrentTextPrimary);
        if (!Theme.IsDark)
        {
            e.Graphics.DrawEllipse(pen, -5, -5, 10, 10);
            for (var i = 0; i < 8; i++)
            { e.Graphics.DrawLine(pen, 0, -8, 0, -12); e.Graphics.RotateTransform(45); }
        }
        else
        {
            e.Graphics.FillEllipse(fill, -10, -10, 20, 20);
            using var cutout = new SolidBrush(Theme.CurrentBg); e.Graphics.FillEllipse(cutout, -3, -13, 20, 20);
        }
        e.Graphics.ResetTransform();
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }
}
