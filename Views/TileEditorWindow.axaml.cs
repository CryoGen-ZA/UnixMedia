using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using UnixMedia.Models;
using UnixMedia.Services;

namespace UnixMedia.Views;

public partial class TileEditorWindow : Window
{
    private static readonly string[] Palette =
    [
        "#334155", "#0078D4", "#1D4ED8", "#7C3AED", "#9146FF", "#DB2777",
        "#CC0000", "#B20710", "#EA580C", "#CA8A04", "#16A34A", "#1DB954",
        "#0D9488", "#0891B2", "#000000", "#4B5563",
    ];

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico"];

    private readonly Tile _tile;
    private string? _iconPath;      // currently selected icon (managed or a newly picked external file)
    private Bitmap? _preview;

    // Required by the XAML loader / previewer.
    public TileEditorWindow() : this(null) { }

    public TileEditorWindow(Tile? existing)
    {
        InitializeComponent();

        _tile = existing?.Clone() ?? new Tile();
        _iconPath = _tile.IconPath;

        if (existing is not null)
        {
            Title = HeaderText.Text = "Edit tile";
            NameBox.Text = existing.Name;
            UrlBox.Text = existing.Url;
        }

        var colors = Palette.Contains(_tile.Color, StringComparer.OrdinalIgnoreCase)
            ? Palette
            : Palette.Prepend(_tile.Color).ToArray();
        ColorList.ItemsSource = colors.Select(c => new Border
        {
            Width = 34, Height = 34, CornerRadius = new Avalonia.CornerRadius(6),
            Background = MainWindow.ParseBrush(c), Tag = c,
        }).ToList();
        ColorList.SelectedIndex = Math.Max(0, Array.FindIndex(colors, c => string.Equals(c, _tile.Color, StringComparison.OrdinalIgnoreCase)));

        ColorList.SelectionChanged += (_, _) => UpdatePreview();
        NameBox.TextChanged += (_, _) => UpdatePreview();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        ClearIconButton.Click += (_, _) => { _iconPath = null; UpdatePreview(); };
        CancelButton.Click += (_, _) => Close(null);
        SaveButton.Click += (_, _) => Save();

        Opened += (_, _) => NameBox.Focus();
        Closed += (_, _) => _preview?.Dispose();

        UpdatePreview();
    }

    private string SelectedColor => (ColorList.SelectedItem as Border)?.Tag as string ?? Palette[0];

    private void UpdatePreview()
    {
        PreviewFrame.Background = MainWindow.ParseBrush(SelectedColor);

        _preview?.Dispose();
        _preview = null;
        if (_iconPath is not null && File.Exists(_iconPath))
        {
            try { _preview = new Bitmap(_iconPath); }
            catch { ShowError("That image could not be read."); _iconPath = null; }
        }

        PreviewImage.Source = _preview;
        PreviewInitial.IsVisible = _preview is null;
        var name = NameBox.Text?.Trim();
        PreviewInitial.Text = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a tile icon",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ImageExtensions.Select(e => "*" + e).ToArray() },
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            ShowError("Please choose a PNG, JPG, BMP, GIF, WebP or ICO image.");
            return;
        }

        ErrorText.IsVisible = false;
        _iconPath = path;
        UpdatePreview();
    }

    private void Save()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { ShowError("Please enter a name."); NameBox.Focus(); return; }

        var url = BrowserLauncher.Normalize(UrlBox.Text);
        if (url is null) { ShowError("Please enter a valid web address (http:// or https://)."); UrlBox.Focus(); return; }

        try
        {
            // Copy newly picked images into the app's own folder so tiles survive the source being moved.
            if (_iconPath is not null && !TileStore.IsManagedIcon(_iconPath))
                _iconPath = TileStore.ImportIcon(_iconPath);
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy the image: {ex.Message}");
            return;
        }

        _tile.Name = name;
        _tile.Url = url;
        _tile.IconPath = _iconPath;
        _tile.Color = SelectedColor;
        Close(_tile);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
