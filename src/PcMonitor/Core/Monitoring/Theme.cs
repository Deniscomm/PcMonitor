using System.Windows.Media;

namespace PcMonitor.Core;

/// <summary>Палитра приложения.</summary>
public static class Theme
{
    public static Brush Text { get; private set; } = Frozen("#E8EAED");
    public static Brush TextDim { get; private set; } = Frozen("#A2A8B1");
    public static Brush TextFaint { get; private set; } = Frozen("#868E99");
    public static Brush Good { get; private set; } = Frozen("#8AC49E");
    public static Brush Warn { get; private set; } = Frozen("#D6B875");
    public static Brush Bad { get; private set; } = Frozen("#E18B8B");
    public static Brush Accent { get; private set; } = Frozen("#83B5CC");
    public static Brush Panel { get; private set; } = Frozen("#1B1D20");
    public static Brush PanelAlt { get; private set; } = Frozen("#202327");
    public static Brush Border { get; private set; } = Frozen("#303338");

    public static void ApplyPalette(IReadOnlyDictionary<string, string> colors)
    {
        Text = Frozen(colors["TextBrush"]);
        TextDim = Frozen(colors["TextDimBrush"]);
        TextFaint = Frozen(colors["TextFaintBrush"]);
        Good = Frozen(colors["GoodBrush"]);
        Warn = Frozen(colors["WarnBrush"]);
        Bad = Frozen(colors["BadBrush"]);
        Accent = Frozen(colors["AccentBrush"]);
        Panel = Frozen(colors["PanelBg"]);
        PanelAlt = Frozen(colors["PanelAltBg"]);
        Border = Frozen(colors["BorderBrush"]);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
