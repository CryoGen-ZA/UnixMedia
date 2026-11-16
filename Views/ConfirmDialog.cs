using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnixMedia.Views;

/// <summary>Minimal yes/no dialog.</summary>
public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var yes = new Button { Content = "Remove", Classes = { "accent" }, IsDefault = true, MinWidth = 90 };
        var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };

        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#111827")),
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { no, yes },
                    },
                },
            },
        };

        yes.Click += (_, _) => window.Close(true);
        no.Click += (_, _) => window.Close(false);
        window.Opened += (_, _) => no.Focus();   // safe default
        return window.ShowDialog<bool>(owner);
    }
}
