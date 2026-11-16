using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UnixMedia.Services;

namespace UnixMedia.Views;

/// <summary>Asks whether to shut down, restart or exit to the desktop.</summary>
public static class PowerDialog
{
    public static Task<PowerAction?> ShowAsync(Window owner)
    {
        Window? window = null;

        Button Big(string glyph, string text, PowerAction? action, string color)
        {
            var b = new Button
            {
                Width = 150,
                Height = 130,
                Background = MainWindow.ParseBrush(color),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(12),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = glyph, FontSize = 40, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = text, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            b.Click += (_, _) => window!.Close(action);
            return b;
        }

        var shutdown = Big("⏻", "Shut down", PowerAction.Shutdown, "#B91C1C");
        var restart = Big("↻", "Restart", PowerAction.Restart, "#C2410C");
        var exit = Big("⇱", "Exit to desktop", PowerAction.Exit, "#1D4ED8");
        var cancel = Big("✕", "Cancel", null, "#374151");
        cancel.IsCancel = true;

        Button[] buttons = [shutdown, restart, exit, cancel];

        window = new Window
        {
            Title = "Power",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#111827")),
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 22,
                Children =
                {
                    new TextBlock { Text = "Power", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { shutdown, restart, exit, cancel } },
                },
            },
        };

        // TV-remote style: left/right moves between the big buttons.
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right)) return;
            var i = System.Array.FindIndex(buttons, b => b.IsFocused);
            var next = i < 0 ? 0 : System.Math.Clamp(i + (e.Key == Key.Right ? 1 : -1), 0, buttons.Length - 1);
            buttons[next].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        window.Opened += (_, _) => cancel.Focus(NavigationMethod.Directional);   // safe default
        return window.ShowDialog<PowerAction?>(owner);
    }
}
