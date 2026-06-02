using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MarathonAudio.App.Services;

namespace MarathonAudio.App.Views;

public sealed class SettingsResult
{
    public bool GameDirChanged;
    public bool WordlistChanged;
}

public partial class SettingsWindow : Window
{
    private readonly AppState _state = AppState.Instance;
    private readonly string? _origGameDir;
    private readonly string? _origWordlist;

    public SettingsWindow()
    {
        InitializeComponent();
        GameBox.Text = _state.Config.GameDir ?? "";
        ExportBox.Text = _state.Config.ExportDir ?? "";
        WordlistBox.Text = _state.Config.WordlistFile ?? "";
        _origGameDir = _state.Config.GameDir;
        _origWordlist = _state.Config.WordlistFile;
    }

    private async Task<string?> PickFolder()
    {
        var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return f.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void BrowseGame(object? s, RoutedEventArgs e)
    {
        var p = await PickFolder(); if (p != null) GameBox.Text = p;
    }

    private async void BrowseExport(object? s, RoutedEventArgs e)
    {
        var p = await PickFolder(); if (p != null) ExportBox.Text = p;
    }

    private async void BrowseWordlist(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Text / wordlist") { Patterns = new[] { "*.txt", "*.*" } } }
        });
        var p = files.FirstOrDefault()?.TryGetLocalPath();
        if (p != null) WordlistBox.Text = p;
    }

    private void ClearWordlist(object? s, RoutedEventArgs e) => WordlistBox.Text = "";

    private void OnCancel(object? s, RoutedEventArgs e) => Close(null);

    private void OnSave(object? s, RoutedEventArgs e)
    {
        string game = (GameBox.Text ?? "").Trim();
        string export = (ExportBox.Text ?? "").Trim();
        string wordlist = (WordlistBox.Text ?? "").Trim();

        _state.SetGameDir(game);
        _state.SetExportDir(string.IsNullOrEmpty(export) ? null : export);
        _state.SetWordlistFile(string.IsNullOrEmpty(wordlist) ? null : wordlist);

        Close(new SettingsResult
        {
            GameDirChanged = !string.Equals(_origGameDir ?? "", game),
            WordlistChanged = !string.Equals(_origWordlist ?? "", wordlist),
        });
    }
}
