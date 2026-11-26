using System.Windows;
using System.Windows.Media;

namespace PcMonitor.Core;

public static class ThemeManager
{
    private static readonly string[] Keys =
    {
        "WindowBg", "PanelBg", "PanelAltBg", "InputBg", "ChromeBg", "BorderBrush",
        "HoverBg", "SelectedBg", "TextBrush", "TextDimBrush", "TextFaintBrush",
        "AccentBrush", "GoodBrush", "WarnBrush", "BadBrush",
        "ScrollBarTrackBrush", "ScrollBarThumbBrush", "ScrollBarBorderBrush"
    };

    public static void Apply(string theme)
    {
        if (Application.Current is null) return;
        string selected = theme is "Gray" or "Light" ? theme : "Dark";
        string[] values = selected switch
        {
            "Light" => new[] { "#FFFFFF", "#FFFFFF", "#F7F8FA", "#FFFFFF", "#FFFFFF", "#E1E5EA", "#F1F5F9", "#E8F0FE", "#111827", "#374151", "#6B7280", "#2563EB", "#16803C", "#9A6700", "#B42318", "#F1F5F9", "#C4CBD4", "#E1E5EA" },
            "Gray" => new[] { "#686D74", "#7A8088", "#858B93", "#70767E", "#5D636B", "#969CA4", "#90969E", "#AEB9C5", "#20252B", "#353B43", "#4B535C", "#245A86", "#146C36", "#855900", "#9F1D1D", "#686D74", "#AAB0B7", "#969CA4" },
            _ => new[] { "#131517", "#1B1D20", "#202327", "#15171A", "#17191C", "#303338", "#292D32", "#29363E", "#E8EAED", "#A2A8B1", "#868E99", "#83B5CC", "#8AC49E", "#D6B875", "#E18B8B", "#14171B", "#3B434D", "#252B32" }
        };
        var palette = Keys.Select((key, index) => (key, value: values[index])).ToDictionary(x => x.key, x => x.value);
        for (int i = 0; i < Keys.Length; i++)
        {
            Application.Current.Resources[Keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
        }
        Theme.ApplyPalette(palette);
    }
}
