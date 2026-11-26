using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Простой диалог ввода строки (переименование датчика, название плитки).</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string title, string initial, string hint = "")
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = Theme.Panel,
            Foreground = Theme.Text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        var root = new StackPanel { Margin = new Thickness(18) };

        if (hint.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = Theme.TextDim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        var box = new TextBox { Text = initial, Padding = new Thickness(8, 5, 8, 5) };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        string? result = null;

        var ok = new Button { Content = "Сохранить", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            result = box.Text;
            window.DialogResult = true;
        };

        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 5, 14, 5) };
        cancel.Click += (_, _) => window.DialogResult = false;

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? result : null;
    }
}
