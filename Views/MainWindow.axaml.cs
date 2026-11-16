using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UnixMedia.Models;
using UnixMedia.Services;

namespace UnixMedia.Views;

public partial class MainWindow : Window
{
    private readonly List<Tile> _tiles;
    private readonly List<Button> _buttons = new();
    private readonly List<Bitmap> _bitmaps = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly AppSettings _settings;
    private bool _dialogOpen;
    private IntroOverlay? _intro;

    public MainWindow()
    {
        InitializeComponent();

        _tiles = TileStore.Load();
        _settings = TileStore.LoadSettings();

        KioskItem.IsChecked = _settings.KioskMode;
        KioskItem.Click += (_, _) =>
        {
            _settings.KioskMode = KioskItem.IsChecked;
            try { TileStore.SaveSettings(_settings); }
            catch (Exception ex) { ShowToast($"Could not save settings: {ex.Message}"); }
        };
        SoundService.Enabled = _settings.Sounds;
        SoundService.Initialize();
        SoundsItem.IsChecked = _settings.Sounds;
        SoundsItem.Click += (_, _) =>
        {
            _settings.Sounds = SoundService.Enabled = SoundsItem.IsChecked;
            try { TileStore.SaveSettings(_settings); }
            catch (Exception ex) { ShowToast($"Could not save settings: {ex.Message}"); }
        };

        IntroItem.IsChecked = _settings.Intro;
        IntroItem.Click += (_, _) =>
        {
            _settings.Intro = IntroItem.IsChecked;
            try { TileStore.SaveSettings(_settings); }
            catch (Exception ex) { ShowToast($"Could not save settings: {ex.Message}"); }
        };
        if (_settings.Intro) StartIntro();

        // Focus sound for keyboard/remote navigation (mouse clicks get the select sound instead).
        SettingsButton.GotFocus += OnNavigableGotFocus;
        SettingsButton.Click += (_, _) => SoundService.Play(UiSound.Select);

        AddTileItem.Click += async (_, _) => await AddTileAsync();
        PowerItem.Click += async (_, _) => await ShowPowerAsync();

        // When the user closes the kiosk browser, bring the launcher back to the front.
        BrowserLauncher.KioskExited += OnKioskExited;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer.Stop(); };

        // Tunnel so arrow keys are handled before the ScrollViewer consumes them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) => RebuildTiles(focusIndex: 0);
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            BrowserLauncher.KioskExited -= OnKioskExited;
            DisposeBitmaps();
        };
    }

    // ---------------------------------------------------------------- intro

    private void StartIntro()
    {
        _intro = new IntroOverlay();
        Grid.SetRowSpan(_intro, 3);
        _intro.Finished += (_, _) =>
        {
            RootGrid.Children.Remove(_intro);
            _intro = null;
        };
        RootGrid.Children.Add(_intro);
    }

    // ---------------------------------------------------------------- UI building

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("dddd, d MMMM");
    }

    private void RebuildTiles(int? focusIndex = null)
    {
        DisposeBitmaps();
        TilePanel.Children.Clear();
        _buttons.Clear();

        foreach (var tile in _tiles)
            _buttons.Add(CreateTileButton(tile));

        foreach (var b in _buttons)
            TilePanel.Children.Add(b);

        if (focusIndex is int i)
        {
            if (_buttons.Count == 0) FocusSettings();
            else FocusTile(Math.Clamp(i, 0, _buttons.Count - 1));
        }
    }

    private Button CreateTileButton(Tile tile)
    {
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        Control visual = CreateInitialText(tile.Name);
        if (tile.IconPath is { } path && File.Exists(path))
        {
            try
            {
                var bmp = new Bitmap(path);
                _bitmaps.Add(bmp);
                visual = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(28, 18, 28, 10) };
            }
            catch
            {
                // Unreadable image: fall back to the initial letter.
            }
        }
        content.Children.Add(visual);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = tile.Name,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        Grid.SetRow(label, 1);
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Background = ParseBrush(tile.Color),
            Tag = tile,
        };
        button.Classes.Add("tile");
        ToolTip.SetTip(button, tile.Url);
        button.Click += (_, _) => OpenTile(tile);
        button.GotFocus += OnNavigableGotFocus;

        var edit = new MenuItem { Header = "Edit…" };
        edit.Click += async (_, _) => await EditTileAsync(tile);
        var left = new MenuItem { Header = "Move left" };
        left.Click += (_, _) => MoveTile(tile, -1);
        var right = new MenuItem { Header = "Move right" };
        right.Click += (_, _) => MoveTile(tile, +1);
        var delete = new MenuItem { Header = "Remove" };
        delete.Click += async (_, _) => await DeleteTileAsync(tile);
        button.ContextMenu = new ContextMenu { ItemsSource = new object[] { edit, left, right, new Separator(), delete } };

        return button;
    }

    private static TextBlock CreateInitialText(string name) => new()
    {
        Text = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant(),
        FontSize = 72,
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static IBrush ParseBrush(string? hex)
    {
        try { return Brush.Parse(hex ?? ""); }
        catch { return new SolidColorBrush(Color.Parse("#334155")); }
    }

    private void DisposeBitmaps()
    {
        foreach (var b in _bitmaps) b.Dispose();
        _bitmaps.Clear();
    }

    // ---------------------------------------------------------------- navigation

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_intro is not null)
        {
            _intro.Skip();
            e.Handled = true;
            return;
        }

        if (_dialogOpen) return;

        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
            return;
        }

        // Let an open context menu / settings menu handle its own keys.
        if (SettingsMenu?.IsOpen == true || _buttons.Any(b => b.ContextMenu?.IsOpen == true)) return;

        if (e.Key is Key.S or Key.Escape)
        {
            FocusSettings();
            SettingsButton.Flyout?.ShowAt(SettingsButton);
            e.Handled = true;
            return;
        }

        if (SettingsButton.IsFocused)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Left when _buttons.Count > 0:
                    FocusTile(0); e.Handled = true; break;
                case Key.Insert or Key.A:
                    _ = AddTileAsync(); e.Handled = true; break;
            }
            return; // Enter/Space open the flyout via the button itself.
        }

        if (_buttons.Count == 0)
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { FocusSettings(); e.Handled = true; }
            else if (e.Key is Key.Insert or Key.A) { _ = AddTileAsync(); e.Handled = true; }
            return;
        }

        var current = FocusManager?.GetFocusedElement() as Button;
        var index = current is null ? -1 : _buttons.IndexOf(current);
        var tile = current?.Tag as Tile;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down when index < 0:
                FocusTile(0);
                e.Handled = true;
                break;

            case Key.Left when ctrl && tile is not null:
                MoveTile(tile, -1); e.Handled = true; break;
            case Key.Right when ctrl && tile is not null:
                MoveTile(tile, +1); e.Handled = true; break;

            case Key.Left:
                FocusTile(Math.Max(0, index - 1)); e.Handled = true; break;
            case Key.Right:
                FocusTile(Math.Min(_buttons.Count - 1, index + 1)); e.Handled = true; break;
            case Key.Up:
                FocusVertical(index, up: true); e.Handled = true; break;
            case Key.Down:
                FocusVertical(index, up: false); e.Handled = true; break;
            case Key.Home:
                FocusTile(0); e.Handled = true; break;
            case Key.End:
                FocusTile(_buttons.Count - 1); e.Handled = true; break;

            case Key.E or Key.Apps when tile is not null:
                _ = EditTileAsync(tile); e.Handled = true; break;
            case Key.Delete when tile is not null:
                _ = DeleteTileAsync(tile); e.Handled = true; break;
            case Key.Insert or Key.A:
                _ = AddTileAsync(); e.Handled = true; break;
        }
    }

    /// <summary>Moves focus to the nearest tile in the row above/below, matching horizontal position.</summary>
    private void FocusVertical(int index, bool up)
    {
        var from = Center(_buttons[index]);
        if (from is null) return;
        var found = false;

        var target = _buttons
            .Select((b, i) => (i, c: Center(b)))
            .Where(x => x.c is not null && x.i != index)
            .Select(x => (x.i, dy: x.c!.Value.Y - from.Value.Y, dx: Math.Abs(x.c!.Value.X - from.Value.X)))
            .Where(x => up ? x.dy < -10 : x.dy > 10)
            .OrderBy(x => Math.Round(Math.Abs(x.dy)))   // nearest row first
            .ThenBy(x => x.dx)                          // then closest column
            .Select(x => (int?)x.i)
            .FirstOrDefault();

        if (target is int t) { FocusTile(t); found = true; }

        // Top row + Up: move into the top bar.
        if (!found && up) FocusSettings();
    }

    private MenuFlyout? SettingsMenu => SettingsButton.Flyout as MenuFlyout;

    private void FocusSettings() =>
        Dispatcher.UIThread.Post(() => SettingsButton.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);

    private void OnKioskExited(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            Activate();
            if (FocusManager?.GetFocusedElement() is not Button) FocusTile(0);
        });

    private Point? Center(Control c) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), TilePanel);

    private void FocusTile(int index)
    {
        if (index < 0 || index >= _buttons.Count) return;
        var b = _buttons[index];
        // Defer so it works right after the panel was rebuilt.
        Dispatcher.UIThread.Post(() =>
        {
            b.Focus(NavigationMethod.Directional);
            b.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- actions

    private void OnNavigableGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod != NavigationMethod.Pointer && IsActive && _intro is null)
            SoundService.Play(UiSound.Focus);
    }

    private void OpenTile(Tile tile)
    {
        SoundService.Play(UiSound.Select);
        if (!BrowserLauncher.TryOpen(tile.Url, _settings.KioskMode, out var error))
            ShowToast(error ?? "Could not open the link.");
    }

    private async Task AddTileAsync()
    {
        var result = await ShowEditorAsync(null);
        if (result is null) return;

        _tiles.Add(result);
        Persist();
        RebuildTiles(focusIndex: _tiles.Count - 1);
    }

    private async Task EditTileAsync(Tile tile)
    {
        var result = await ShowEditorAsync(tile);
        if (result is null) return;

        var i = _tiles.IndexOf(tile);
        if (i < 0) return;
        if (tile.IconPath != result.IconPath)
            TileStore.DeleteIcon(tile.IconPath);
        _tiles[i] = result;
        Persist();
        RebuildTiles(focusIndex: i);
    }

    private async Task DeleteTileAsync(Tile tile)
    {
        var i = _tiles.IndexOf(tile);
        if (i < 0) return;

        _dialogOpen = true;
        bool ok;
        try { ok = await ConfirmDialog.ShowAsync(this, "Remove tile", $"Remove \"{tile.Name}\" from the home screen?"); }
        finally { _dialogOpen = false; }
        if (!ok) { FocusTile(i); return; }

        _tiles.RemoveAt(i);
        TileStore.DeleteIcon(tile.IconPath);
        Persist();
        RebuildTiles(focusIndex: i);
    }

    private void MoveTile(Tile tile, int delta)
    {
        var i = _tiles.IndexOf(tile);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _tiles.Count) return;

        (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
        Persist();
        RebuildTiles(focusIndex: j);
    }

    private async Task ShowPowerAsync()
    {
        PowerAction? action;
        _dialogOpen = true;
        try { action = await PowerDialog.ShowAsync(this); }
        finally { _dialogOpen = false; }

        string? error = null;
        switch (action)
        {
            case PowerAction.Exit:
                BrowserLauncher.CloseKioskBrowser();
                Close();
                return;
            case PowerAction.Shutdown when !PowerService.TryShutdown(out error):
            case PowerAction.Restart when !PowerService.TryRestart(out error):
                ShowToast(error ?? "The power command failed.");
                break;
        }
        FocusSettings();
    }

    private async Task<Tile?> ShowEditorAsync(Tile? existing)
    {
        _dialogOpen = true;
        try { return await new TileEditorWindow(existing).ShowDialog<Tile?>(this); }
        finally { _dialogOpen = false; }
    }

    private void Persist()
    {
        try { TileStore.Save(_tiles); }
        catch (Exception ex) { ShowToast($"Could not save tiles: {ex.Message}"); }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
