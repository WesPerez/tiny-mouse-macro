namespace TinyMouseMacro;

internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF6, 0xFA);
    public static readonly Color Surface = Color.FromArgb(0xEA, 0xEE, 0xF5);
    public static readonly Color Card = Color.FromArgb(0xE1, 0xE8, 0xF3);
    public static readonly Color Border = Color.FromArgb(0xD7, 0xDF, 0xEC);
    public static readonly Color Accent = Color.FromArgb(0x35, 0x61, 0xD8);
    public static readonly Color AccentDim = Color.FromArgb(0x28, 0x49, 0xB0);
    public static readonly Color TextPrimary = Color.FromArgb(0x1A, 0x2A, 0x3A);
    public static readonly Color TextSecondary = Color.FromArgb(0x4A, 0x5E, 0x72);
    public static readonly Color TextMuted = Color.FromArgb(0x7A, 0x8E, 0xA2);
    public static readonly Color Success = Color.FromArgb(0x10, 0x8C, 0x4E);
    public static readonly Color Warning = Color.FromArgb(0xD4, 0x7E, 0x10);
    public static readonly Color Danger = Color.FromArgb(0xD4, 0x30, 0x3A);
    public static readonly Color InputBg = Color.White;
    public static readonly Color ListBg = Color.White;
    public static readonly Color ListSelected = Color.FromArgb(0x35, 0x61, 0xD8);
    public static readonly Color ListSelectedBg = Color.FromArgb(0xEA, 0xEE, 0xF5);

    public static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI");
    public static readonly FontFamily MonoFont = new FontFamily("Consolas");

    public static Color CurrentBg => IsDark ? Color.FromArgb(0x1E, 0x1E, 0x2E) : Bg;
    public static Color CurrentSurface => IsDark ? Color.FromArgb(0x2D, 0x2D, 0x3F) : Surface;
    public static Color CurrentCard => IsDark ? Color.FromArgb(0x3A, 0x3A, 0x50) : Card;
    public static Color CurrentBorder => IsDark ? Color.FromArgb(0x50, 0x50, 0x6E) : Border;
    public static Color CurrentAccent => IsDark ? Color.FromArgb(0x4F, 0xC3, 0xF7) : Accent;
    public static Color CurrentAccentDim => IsDark ? Color.FromArgb(0x39, 0xAD, 0xE5) : AccentDim;
    public static Color CurrentTextPrimary => IsDark ? Color.FromArgb(0xE0, 0xE0, 0xF0) : TextPrimary;
    public static Color CurrentTextSecondary => IsDark ? Color.FromArgb(0xA0, 0xA0, 0xC0) : TextSecondary;
    public static Color CurrentTextMuted => IsDark ? Color.FromArgb(0x70, 0x70, 0x90) : TextMuted;
    public static Color CurrentInputBg => IsDark ? Color.FromArgb(0x2A, 0x2A, 0x3C) : InputBg;
    public static Color CurrentListBg => IsDark ? Color.FromArgb(0x2A, 0x2A, 0x3C) : ListBg;
    public static Color CurrentListSelected => IsDark ? Color.FromArgb(0x4F, 0xC3, 0xF7) : ListSelected;
    public static Color CurrentListSelectedBg => IsDark ? Color.FromArgb(0x2D, 0x2D, 0x4F) : ListSelectedBg;

    public static readonly Font TitleFont = new(UiFont, 20F, FontStyle.Bold);
    public static readonly Font BodyFont = new(UiFont, 9.5F);
    public static readonly Font SmallFont = new(UiFont, 8.5F);
    public static readonly Font SectionFont = new(UiFont, 9.5F, FontStyle.Bold);
    public static readonly Font MonoBodyFont = new(MonoFont, 9.5F);
    public static void SetDark(bool dark) => IsDark = dark;

    public static void Toggle()
    {
        IsDark = !IsDark;
    }

    public static void ApplyToForm(Form form)
    {
        form.BackColor = CurrentBg;
        form.ForeColor = CurrentTextPrimary;
    }

    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = CurrentBorder;
        button.FlatAppearance.MouseOverBackColor = CurrentCard;
        button.FlatAppearance.MouseDownBackColor = CurrentAccentDim;
        button.BackColor = CurrentSurface;
        button.ForeColor = CurrentTextPrimary;
        button.Font = SmallFont;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleAccentButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = CurrentAccent;
        button.BackColor = CurrentAccent;
        button.ForeColor = Color.White;
        button.FlatAppearance.MouseOverBackColor = CurrentAccentDim;
        button.FlatAppearance.MouseDownBackColor = CurrentAccent;
    }

    public static void StyleDangerButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = Danger;
        button.BackColor = IsDark ? Color.FromArgb(0x5A, 0x20, 0x28) : Color.FromArgb(0xFD, 0xE8, 0xEA);
        button.ForeColor = IsDark ? Color.FromArgb(0xFF, 0x80, 0x80) : Danger;
        button.FlatAppearance.MouseOverBackColor = IsDark ? Color.FromArgb(0x70, 0x28, 0x30) : Color.FromArgb(0xFC, 0xD4, 0xD8);
    }

    public static void StyleLabel(Label label, bool isHeader = false)
    {
        label.ForeColor = isHeader ? CurrentTextSecondary : CurrentTextMuted;
        label.Font = isHeader ? SectionFont : SmallFont;
    }

    public static void StyleTextBox(TextBoxBase textBox)
    {
        textBox.BackColor = CurrentInputBg;
        textBox.ForeColor = CurrentTextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = BodyFont;
    }

    public static void StyleListBox(ListBox listBox)
    {
        listBox.BackColor = CurrentListBg;
        listBox.ForeColor = CurrentTextPrimary;
        listBox.BorderStyle = BorderStyle.FixedSingle;
        listBox.Font = BodyFont;
        listBox.DrawMode = DrawMode.OwnerDrawFixed;
        listBox.ItemHeight = listBox.Tag as string == "Profiles" ? 64 : listBox.Tag as string == "Steps" ? 48 : 30;
        listBox.DrawItem -= OnListBoxDrawItem;
        listBox.DrawItem += OnListBoxDrawItem;
    }

    private static void OnListBoxDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var lb = (ListBox)sender!;
        var text = lb.GetItemText(lb.Items[e.Index]);
        var isSelected = (e.State & DrawItemState.Selected) != 0;

        using var bgBrush = new SolidBrush(isSelected ? CurrentListSelectedBg : CurrentListBg);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        if (isSelected)
        {
            using var accentPen = new Pen(CurrentListSelected, 2);
            e.Graphics.DrawLine(accentPen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom);
        }

        var color = isSelected ? CurrentAccent : CurrentTextPrimary;
        var textRect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 3, e.Bounds.Width - 24, e.Bounds.Height - 6);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (lb.Items[e.Index] is MacroProfile profile)
        {
            TextRenderer.DrawText(e.Graphics, profile.Name, SectionFont, new Rectangle(textRect.X, textRect.Y + 5, textRect.Width, 23), color, flags);
            TextRenderer.DrawText(e.Graphics, $"{(profile.Enabled ? "●" : "○")}  {profile.ShortcutText}   ·   {profile.Steps.Count} 步", SmallFont,
                new Rectangle(textRect.X, textRect.Y + 30, textRect.Width, 22), CurrentTextSecondary, flags);
        }
        else if (lb.Items[e.Index] is MacroStep step)
        {
            var timing = step.RecordedDelayMs is int ms ? $"  ·  间隔 {ms / 1000.0:0.###} 秒" : "";
            TextRenderer.DrawText(e.Graphics, $"{e.Index + 1:00}   {step.Display}", BodyFont,
                new Rectangle(textRect.X, textRect.Y + 3, textRect.Width, 24), color, flags);
            TextRenderer.DrawText(e.Graphics, $"{(step.RecordedDelayMs.HasValue ? "录制时序" : "手动动作")}{timing}", SmallFont,
                new Rectangle(textRect.X + 30, textRect.Y + 25, textRect.Width - 30, 18), CurrentTextSecondary, flags);
        }
        else TextRenderer.DrawText(e.Graphics, text, BodyFont, textRect, color, flags | TextFormatFlags.VerticalCenter);
    }

    public static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = CurrentInputBg;
        comboBox.ForeColor = CurrentTextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = BodyFont;
        comboBox.DrawMode = DrawMode.OwnerDrawFixed;
        comboBox.ItemHeight = Math.Max(22, TextRenderer.MeasureText("快捷键", BodyFont).Height + 2);
        comboBox.DrawItem -= DrawComboItem;
        comboBox.DrawItem += DrawComboItem;
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        var combo = (ComboBox)sender!;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? CurrentListSelectedBg : CurrentInputBg);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
        TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -4, 0),
            CurrentTextPrimary, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public static void StyleNumericUpDown(NumericUpDown numeric)
    {
        numeric.BackColor = CurrentInputBg;
        numeric.ForeColor = CurrentTextPrimary;
        numeric.BorderStyle = BorderStyle.FixedSingle;
        numeric.Font = BodyFont;
    }

    public static void StylePanel(Control panel)
    {
        panel.BackColor = CurrentBg;
    }

    public static void StyleSectionHeader(Label label)
    {
        label.ForeColor = CurrentAccent;
        label.Font = SectionFont;
    }

    public static void StyleStatusBar(Label label)
    {
        label.BackColor = CurrentSurface;
        label.ForeColor = CurrentTextSecondary;
        label.Font = BodyFont;
        label.Padding = new Padding(8, 0, 0, 0);
    }

    public static void StyleMonoLabel(Label label)
    {
        label.ForeColor = CurrentAccent;
        label.Font = MonoBodyFont;
    }
}
