namespace MiniRedis.App.Ui;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xEE, 0xF0, 0xF3);
    public static readonly Color Panel = Color.White;
    public static readonly Color Ink = Color.FromArgb(0x1C, 0x21, 0x28);
    public static readonly Color Muted = Color.FromArgb(0x5A, 0x65, 0x70);
    public static readonly Color Line = Color.FromArgb(0xDD, 0xE1, 0xE6);
    public static readonly Color Accent = Color.FromArgb(0x1B, 0x6B, 0x5A);
    public static readonly Color ReadonlyBg = Color.FromArgb(0xE8, 0xEE, 0xF5);
    public static readonly Color ReadonlyFg = Color.FromArgb(0x3C, 0x58, 0x78);
    public static readonly Color Danger = Color.FromArgb(0xA3, 0x2D, 0x2D);
    public static readonly Color TerminalBg = Color.FromArgb(0x16, 0x1A, 0x1D);
    public static readonly Color TerminalFg = Color.FromArgb(0xD5, 0xDD, 0xD8);

    public static Font Ui()
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            try
            {
                return new Font(name, 9f, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch (ArgumentException)
            {
            }
        }

        return SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
    }

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 6, 0),
            BackColor = primary ? Accent : Panel,
            ForeColor = primary ? Color.White : Ink,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }
}
