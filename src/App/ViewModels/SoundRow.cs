using CommunityToolkit.Mvvm.ComponentModel;
using Tiger;

namespace MarathonAudio.App.ViewModels;

/// <summary>A row in the sound list; preview fields fill in lazily as the row scrolls into view.</summary>
public partial class SoundRow : ObservableObject
{
    public SoundEntry Entry { get; }
    public SoundRow(SoundEntry e) { Entry = e; }

    public string DisplayName => Entry.DisplayName;
    public string TagId => Entry.TagId;
    public string PackageName => Entry.PackageName;

    /// <summary>1-based position within the current filtered list.</summary>
    public int Ordinal { get; set; }

    public bool LoadRequested { get; set; }

    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private float[]? _miniPeaks;

    /// <summary>Notify that the proxied name may have changed (e.g. after loading a wordlist).</summary>
    public void RefreshName() => OnPropertyChanged(nameof(DisplayName));
}
