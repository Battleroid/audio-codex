using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarathonAudio.App.Services;
using Tiger;

namespace MarathonAudio.App.ViewModels;

public sealed class GroupOption
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public bool IsAll { get; init; }
    public bool IsUnassigned { get; init; }
    public string? PackageName { get; init; }   // package mode
    public uint BankTag { get; init; }           // soundbank mode
    public string Display => IsAll ? $"All ({Count:N0})" : $"{Label} ({Count:N0})";
}

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppState _state = AppState.Instance;
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _flushTimer;
    private readonly SemaphoreSlim _previewSem = new(3);
    private List<SoundRow> _allRows = new();
    private List<SoundRow> _selection = new();
    private int _selectGen;
    private bool _pendingAutoPlay;
    private DispatcherTimer? _searchDebounce;
    private CancellationTokenSource? _transcribeCts;

    public Func<Task<string?>>? PickFolderAsync;
    /// <summary>Set by the view: shows a yes/no confirmation; returns true to proceed.</summary>
    public Func<string, Task<bool>>? ConfirmAsync;

    public MainWindowViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => UpdatePlayback();
        _player.PlaybackStopped += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _timer.Stop(); IsPlaying = false; Progress = 0; PositionText = "0:00";
        });
        _player.Volume = _state.Config.Volume;
        _volume = _state.Config.Volume;

        // periodically persist the metadata/waveform cache
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _flushTimer.Tick += (_, _) => { _state.FlushCache(); _state.FlushTranscripts(); };
        _flushTimer.Start();

        NeedsSetup = !_state.GameDirValid;
        StatusText = NeedsSetup ? "Set your Marathon game folder to begin."
                                : "Ready. Click “Load sounds” to index packages.";
        GameDirText = _state.Config.GameDir ?? "(not set)";
        VgmAvailable = _state.Vgm.Available;
        WhisperAvailable = _state.Whisper.Available;
        Groups.Add(new GroupOption { IsAll = true, Count = 0 });
        _selectedGroup = Groups[0];
        UpdateExportTarget();
    }

    // ---- top-level ----
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _needsSetup;
    [ObservableProperty] private string _gameDirText = "";
    [ObservableProperty] private bool _vgmAvailable;
    [ObservableProperty] private bool _indexLoaded;

    // ---- transcription ----
    [ObservableProperty] private bool _whisperAvailable;
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private double _transcribeProgress;
    [ObservableProperty] private string _transcribeStatus = "";
    private bool _fuzzyTranscriptSearch;
    public bool FuzzyTranscriptSearch
    {
        get => _fuzzyTranscriptSearch;
        set { if (SetProperty(ref _fuzzyTranscriptSearch, value)) DebouncedFilter(); }
    }

    // ---- catalogue ----
    [ObservableProperty] private IReadOnlyList<SoundRow> _items = Array.Empty<SoundRow>();
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private string _selectionInfo = "";

    public string[] GroupByOptions { get; } = { "Package", "Soundbank" };
    private string _groupBy = "Package";
    public string GroupBy
    {
        get => _groupBy;
        set { if (SetProperty(ref _groupBy, value)) _ = OnGroupByChangedAsync(); }
    }

    public ObservableCollection<GroupOption> Groups { get; } = new();

    private GroupOption _selectedGroup;
    public GroupOption SelectedGroup
    {
        get => _selectedGroup;
        set { if (SetProperty(ref _selectedGroup, value)) ApplyFilter(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) DebouncedFilter(); }
    }

    private SoundRow? _selectedRow;
    public SoundRow? SelectedRow
    {
        get => _selectedRow;
        set { if (SetProperty(ref _selectedRow, value)) _ = OnSelectedAsync(value); }
    }

    // ---- selected metadata ----
    [ObservableProperty] private string _selName = "";
    [ObservableProperty] private string _selTagId = "";
    [ObservableProperty] private string _selPackage = "";
    [ObservableProperty] private string _selCodec = "";
    [ObservableProperty] private string _selChannels = "";
    [ObservableProperty] private string _selRate = "";
    [ObservableProperty] private string _selDuration = "";
    [ObservableProperty] private string _selSize = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _selTranscript = "";

    // ---- playback ----
    [ObservableProperty] private float[]? _peaks;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _playGlyph = "PLAY";
    [ObservableProperty] private string _exportTargetText = "";

    partial void OnIsPlayingChanged(bool value) => PlayGlyph = value ? "PAUSE" : "PLAY";

    public void UpdateExportTarget() =>
        ExportTargetText = _state.HasExportDir
            ? $"→ {_state.Config.ExportDir}"
            : "→ choose a folder (set a default in Settings)";

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _player.Volume = (float)value;
                _state.SetVolume((float)value);
            }
        }
    }

    // ================= Indexing =================
    [RelayCommand]
    private async Task LoadIndexAsync()
    {
        if (!_state.GameDirValid) { StatusText = "Game folder is not valid."; return; }
        IsBusy = true;
        try
        {
            await Task.Run(() => _state.BuildIndex((p, msg) =>
                Dispatcher.UIThread.Post(() => { ProgressValue = p; ProgressText = msg; })));

            _allRows = _state.Manager!.Sounds
                .OrderBy(s => s.PackageName).ThenBy(s => s.Index)
                .Select(s => new SoundRow(s)).ToList();

            RebuildGroups();
            ApplyFilter();
            _state.BuildTranscriptIndex();   // make any previously-cached transcripts searchable
            IndexLoaded = true;
            string names = _state.Manager.Names.WordlistCount > 0
                ? $" • wordlist: {_state.Manager.Names.WordlistCount}" : "";
            StatusText = $"{_allRows.Count:N0} sounds • {_state.Manager.PackageCount} packages{names}";
        }
        catch (Exception ex) { StatusText = "Index failed: " + ex.Message; }
        finally { IsBusy = false; ProgressText = ""; }
    }

    [RelayCommand]
    private async Task BrowseGameDirAsync()
    {
        if (PickFolderAsync == null) return;
        string? dir = await PickFolderAsync();
        if (string.IsNullOrEmpty(dir)) return;
        _state.SetGameDir(dir);
        GameDirText = dir;
        NeedsSetup = !_state.GameDirValid;
        StatusText = _state.GameDirValid ? "Game folder set. Click “Load sounds”."
            : "That folder doesn't look like a Marathon install (missing packages/ or Oodle dll).";
    }

    // ================= Grouping =================
    private async Task OnGroupByChangedAsync()
    {
        if (!IndexLoaded) return;
        if (_groupBy == "Soundbank") await EnsureSoundbanksBuiltAsync();
        RebuildGroups();
        ApplyFilter();
    }

    /// <summary>Build soundbanks once (off the UI thread) if they haven't been built yet.</summary>
    private async Task EnsureSoundbanksBuiltAsync()
    {
        if (_state.Manager is not { SoundbanksBuilt: false }) return;
        IsBusy = true;
        StatusText = "Building soundbanks…";
        try
        {
            await Task.Run(() => _state.Manager!.BuildSoundbanks((p, msg) =>
                Dispatcher.UIThread.Post(() => { ProgressValue = p; ProgressText = msg; })));
        }
        catch (Exception ex) { StatusText = "Soundbank build failed: " + ex.Message; }
        finally { IsBusy = false; ProgressText = ""; }
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        Groups.Add(new GroupOption { IsAll = true, Count = _allRows.Count });
        if (_groupBy == "Soundbank" && _state.Manager is { SoundbanksBuilt: true } m)
        {
            foreach (SoundbankInfo b in m.Soundbanks)
                Groups.Add(new GroupOption { Label = b.Display, Count = b.Count, BankTag = b.Tag });
            int un = _allRows.Count(r => r.Entry.SoundbankTag == 0);
            if (un > 0) Groups.Add(new GroupOption { Label = "Unassigned", Count = un, IsUnassigned = true });
            StatusText = $"{m.Soundbanks.Count:N0} soundbanks • {_allRows.Count - un:N0} sounds grouped";
        }
        else
        {
            foreach (var g in _allRows.GroupBy(r => r.PackageName).OrderBy(g => g.Key))
                Groups.Add(new GroupOption { Label = g.Key, Count = g.Count(), PackageName = g.Key });
        }
        _selectedGroup = Groups[0];
        OnPropertyChanged(nameof(SelectedGroup));
    }

    // ================= Filtering =================
    private void DebouncedFilter()
    {
        _searchDebounce?.Stop();
        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounce.Tick -= OnDebounceTick;
        _searchDebounce.Tick += OnDebounceTick;
        _searchDebounce.Start();
    }
    private void OnDebounceTick(object? s, EventArgs e) { _searchDebounce!.Stop(); ApplyFilter(); }

    /// <summary>Rows belonging to the currently selected group (or all rows when "All").</summary>
    private IEnumerable<SoundRow> RowsInSelectedGroup()
    {
        IEnumerable<SoundRow> src = _allRows;
        if (_selectedGroup is { IsAll: false } g)
        {
            if (g.IsUnassigned) src = src.Where(r => r.Entry.SoundbankTag == 0);
            else if (g.PackageName != null) src = src.Where(r => r.PackageName == g.PackageName);
            else src = src.Where(r => r.Entry.SoundbankTag == g.BankTag);
        }
        return src;
    }

    private void ApplyFilter()
    {
        string q = _searchText.Trim();
        IEnumerable<SoundRow> src = RowsInSelectedGroup();
        if (q.Length > 0)
        {
            // Fuzzy "similar word" matches come from the inverted index (only when toggled on).
            HashSet<SoundEntry>? fuzzy = _fuzzyTranscriptSearch
                ? new HashSet<SoundEntry>(_state.Index.SimilarWords(q))
                : null;
            src = src.Where(x =>
                x.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.TagId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (_state.TranscriptCache.TryGet(_state.CacheKey(x.Entry), out var tc)
                    && !tc.NoSpeech && tc.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (fuzzy != null && fuzzy.Contains(x.Entry)));
        }
        var result = src as List<SoundRow> ?? src.ToList();
        for (int i = 0; i < result.Count; i++) result[i].Ordinal = i + 1;
        Items = result;
        ResultCountText = result.Count == _allRows.Count
            ? $"{result.Count:N0} sounds"
            : $"{result.Count:N0} of {_allRows.Count:N0}";
    }

    // ================= Lazy row preview =================
    public void EnsureRowLoaded(SoundRow row)
    {
        if (row.LoadRequested || !IndexLoaded) return;
        row.LoadRequested = true;
        _ = Task.Run(async () =>
        {
            await _previewSem.WaitAsync();
            try
            {
                var r = _state.LoadRowPreview(row.Entry);
                bool hasTr = _state.TranscriptCache.TryGet(_state.CacheKey(row.Entry), out var tc) && !tc.NoSpeech;
                if (r is { } v)
                    Dispatcher.UIThread.Post(() =>
                    {
                        row.Meta = v.meta; row.DurationText = v.duration; row.MiniPeaks = v.peaks;
                        if (hasTr) row.Transcript = tc!.Text;
                    });
            }
            catch { row.LoadRequested = false; }
            finally { _previewSem.Release(); }
        });
    }

    // ================= Selection / detail =================
    public void SetSelection(IEnumerable<object?> selected)
    {
        _selection = selected.OfType<SoundRow>().ToList();
        SelectionInfo = _selection.Count > 1 ? $"{_selection.Count} selected" : "";
    }

    private async Task OnSelectedAsync(SoundRow? row)
    {
        int gen = ++_selectGen;
        StopInternal();
        Peaks = null; Progress = 0; HasSelection = row != null;
        if (row == null) return;
        SoundEntry s = row.Entry;

        SelName = s.DisplayName; SelTagId = s.TagId; SelPackage = s.PackageName;
        SelSize = FormatBytes(s.Size);
        SelCodec = SelChannels = SelRate = SelDuration = "…";
        SelTranscript = _state.TranscriptCache.TryGet(_state.CacheKey(s), out var tcs) && !tcs.NoSpeech
            ? tcs.Text : "";

        try
        {
            // Metadata + waveform from the on-disk cache (decodes only on first encounter).
            CachedMeta cm = await Task.Run(() => _state.GetPreview(s));
            if (gen != _selectGen) return;
            SelCodec = WemInfo.CodecNameOf(cm.Codec);
            SelChannels = cm.Channels.ToString();
            SelRate = cm.SampleRate > 0 ? $"{cm.SampleRate:N0} Hz" : "?";
            SelDuration = cm.Duration > 0 ? FmtSec(cm.Duration) : "—";
            DurationText = cm.Duration > 0 ? FmtSec(cm.Duration) : "0:00";
            Peaks = cm.Peaks.Length > 0 ? cm.Peaks : null;   // waveform shows instantly
            PositionText = "0:00";

            // Decode the WAV only for actual playback.
            string? wav = await Task.Run(() => _state.DecodeToWav(s));
            if (gen != _selectGen) return;
            if (wav != null && File.Exists(wav))
            {
                await Task.Run(() => _player.LoadForPlayback(wav));
                if (gen != _selectGen) return;
                if (!_player.HasOutputDevice) StatusText = "No audio output device available.";
                _pendingAutoPlay = false;
                StartPlayback();   // click-to-play
            }
            else { StatusText = $"Could not decode {s.TagId} for playback."; }
        }
        catch (Exception ex) { SelDuration = "error"; StatusText = "Load failed: " + ex.Message; }
    }

    // ================= Playback =================
    [RelayCommand]
    private void PlayPause()
    {
        if (!_player.HasFile) { _pendingAutoPlay = true; return; }
        if (_player.IsPlaying) { _player.Pause(); _timer.Stop(); IsPlaying = false; }
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (!_player.HasFile) return;
        _player.Play(); _timer.Start(); IsPlaying = true;
    }

    /// <summary>Spacebar handler: toggle play/pause for the current sound.</summary>
    public void PlayPauseFromKey() => PlayPause();

    /// <summary>Re-read game folder state (after Settings closes).</summary>
    public void RefreshGameDir()
    {
        GameDirText = _state.Config.GameDir ?? "(not set)";
        NeedsSetup = !_state.GameDirValid;
    }

    /// <summary>Reload the wordlist and refresh all visible names in place.</summary>
    public async Task ApplyWordlistAsync()
    {
        if (_state.Manager == null) return;
        IsBusy = true; StatusText = "Applying wordlist…";
        try
        {
            await Task.Run(() => _state.Manager!.ApplyWordlist(_state.WordlistPath));
            foreach (var r in _allRows) r.RefreshName();
            RebuildGroups();
            ApplyFilter();
            int named = _allRows.Count(r => r.Entry.Name != null);
            StatusText = $"Wordlist applied • {named:N0} sounds named • {_state.Manager!.Names.WordlistCount:N0} entries";
        }
        catch (Exception ex) { StatusText = "Wordlist failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Double-click handler: play the selected row (auto-plays once it finishes loading).</summary>
    public void RequestPlaySelected()
    {
        if (_player.HasFile) StartPlayback();
        else _pendingAutoPlay = true;
    }

    private void StopInternal()
    {
        _player.Stop(); _timer.Stop(); IsPlaying = false; Progress = 0; PositionText = "0:00";
    }

    public void SeekToFraction(double frac)
    {
        if (!_player.HasFile) return;
        frac = Math.Clamp(frac, 0, 1);
        _player.Position = TimeSpan.FromSeconds(_player.Duration.TotalSeconds * frac);
        Progress = frac;
        PositionText = Fmt(_player.Position);
    }

    private void UpdatePlayback()
    {
        if (!_player.HasFile) return;
        double dur = _player.Duration.TotalSeconds;
        Progress = dur > 0 ? _player.Position.TotalSeconds / dur : 0;
        PositionText = Fmt(_player.Position);
    }

    // ================= Export =================
    [RelayCommand]
    private Task ExportSelectedAsync()
    {
        var list = _selection.Count > 0 ? _selection.Select(r => r.Entry).ToList()
                 : SelectedRow != null ? new List<SoundEntry> { SelectedRow.Entry }
                 : new List<SoundEntry>();
        return ExportListAsync(list, "selected");
    }

    [RelayCommand]
    private Task ExportListedAsync() => ExportListAsync(Items.Select(r => r.Entry).ToList(), "listed");

    [RelayCommand]
    private Task ExportAllAsync() => ExportListAsync(_allRows.Select(r => r.Entry).ToList(), "all");

    private async Task ExportListAsync(List<SoundEntry> list, string label)
    {
        if (list.Count == 0) { StatusText = "Nothing to export."; return; }
        // One-click to the default export folder when set; otherwise prompt.
        string? dir = _state.HasExportDir ? _state.Config.ExportDir
                    : (PickFolderAsync != null ? await PickFolderAsync() : null);
        if (string.IsNullOrEmpty(dir)) { StatusText = "Set a default export folder in Settings, or choose one."; return; }
        IsBusy = true;
        int done = 0, fail = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (SoundEntry s in list)
                {
                    try
                    {
                        string? wav = _state.DecodeToWav(s);
                        if (wav != null) File.Copy(wav, Path.Combine(dir, NameFor(s)), true); else fail++;
                    }
                    catch { fail++; }
                    done++;
                    if (done % 5 == 0 || done == list.Count)
                        Dispatcher.UIThread.Post(() =>
                        {
                            ProgressValue = done / (double)list.Count;
                            ProgressText = $"Exporting {done}/{list.Count}";
                        });
                }
            });
            StatusText = $"Exported {done - fail}/{list.Count} ({label}) to {dir}" + (fail > 0 ? $" • {fail} failed" : "");
        }
        catch (Exception ex) { StatusText = "Export failed: " + ex.Message; }
        finally { IsBusy = false; ProgressText = ""; }
    }

    // ================= Transcription =================
    [RelayCommand]
    private Task TranscribeCurrentAsync()
    {
        var s = SelectedRow?.Entry;
        return s == null ? Task.CompletedTask
                         : RunTranscribeAsync(new[] { s }, "current", cleanupDecoded: false);
    }

    [RelayCommand]
    private Task TranscribeSelectedAsync()
    {
        var list = _selection.Count > 0
            ? _selection.Select(r => r.Entry).ToList()
            : SelectedRow != null ? new List<SoundEntry> { SelectedRow.Entry } : new List<SoundEntry>();
        return RunTranscribeAsync(list, "selected", cleanupDecoded: false);
    }

    [RelayCommand]
    private Task TranscribeGroupAsync()
    {
        // The "All" group is the whole catalogue — route it through the confirmed all-sounds
        // path so it can't bypass the warning.
        if (_selectedGroup is null or { IsAll: true }) return TranscribeAllAsync();
        var list = RowsInSelectedGroup().Select(r => r.Entry).ToList();
        return RunTranscribeAsync(list, _selectedGroup.Label, cleanupDecoded: true);
    }

    [RelayCommand]
    private async Task TranscribeAllAsync()
    {
        var list = _allRows.Select(r => r.Entry).ToList();
        if (list.Count == 0) return;
        if (ConfirmAsync != null)
        {
            var (w, t) = _state.ResolveConcurrency();
            bool ok = await ConfirmAsync(
                $"Transcribe all {list.Count:N0} sounds?\n\n" +
                $"This can take a long time (running {w} workers × {t} threads). " +
                "Most non-voice clips are skipped automatically, already-done clips are reused, " +
                "and you can cancel anytime.");
            if (!ok) return;
        }
        await RunTranscribeAsync(list, "all", cleanupDecoded: true);
    }

    [RelayCommand]
    private async Task TranscribeVoiceBanksAsync()
    {
        if (!_state.Whisper.Available)
        {
            StatusText = "Speech recognition unavailable — whisper-cli.exe / model missing under tools/whisper.";
            return;
        }
        // Voice banks are identified by soundbank name, which is only resolved once soundbanks
        // are built — make sure that has happened before resolving the preset.
        await EnsureSoundbanksBuiltAsync();
        await RunTranscribeAsync(_state.VoiceBankSounds(), "voice banks", cleanupDecoded: true);
    }

    [RelayCommand]
    private void CancelTranscribe() => _transcribeCts?.Cancel();

    private async Task RunTranscribeAsync(IReadOnlyList<SoundEntry> targets, string label, bool cleanupDecoded)
    {
        if (!_state.Whisper.Available)
        {
            StatusText = "Speech recognition unavailable — whisper-cli.exe / model missing under tools/whisper.";
            return;
        }
        if (IsTranscribing) { StatusText = "A transcription run is already in progress."; return; }
        if (targets.Count == 0) { StatusText = $"No sounds to transcribe ({label})."; return; }

        _transcribeCts = new CancellationTokenSource();
        CancellationToken ct = _transcribeCts.Token;
        IsTranscribing = true;
        TranscribeProgress = 0;
        TranscribeStatus = $"Starting… ({label})";
        try
        {
            var (processed, speech) = await _state.BuildTranscripts(targets, (p, msg) =>
                Dispatcher.UIThread.Post(() => { TranscribeProgress = p; TranscribeStatus = msg; }),
                ct, cleanupDecoded);

            // Reflect new transcripts on the visible rows and re-run the filter.
            foreach (var row in Items)
                if (_state.TranscriptCache.TryGet(_state.CacheKey(row.Entry), out var c) && !c.NoSpeech)
                    row.Transcript = c.Text;
            if (SelectedRow != null
                && _state.TranscriptCache.TryGet(_state.CacheKey(SelectedRow.Entry), out var sc) && !sc.NoSpeech)
                SelTranscript = sc.Text;
            ApplyFilter();

            StatusText = ct.IsCancellationRequested
                ? $"Transcription cancelled • {processed} done this run ({label}) • cache {_state.TranscriptCache.Count:N0}"
                : $"Transcribed {processed} • {speech} with speech ({label}) • cache {_state.TranscriptCache.Count:N0}";
        }
        catch (Exception ex) { StatusText = "Transcription failed: " + ex.Message; }
        finally
        {
            IsTranscribing = false; TranscribeStatus = "";
            _transcribeCts?.Dispose(); _transcribeCts = null;
        }
    }

    private static string NameFor(SoundEntry s)
    {
        string baseName = s.Name ?? s.TagId;
        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        return s.Name != null ? $"{baseName}_{s.TagId}.wav" : $"{s.PackageName}_{s.TagId}.wav";
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    private static string FmtSec(double sec) => $"{(int)(sec / 60)}:{(int)(sec % 60):00}";
    private static string FormatBytes(uint b) =>
        b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : $"{b / 1024.0 / 1024.0:0.00} MB";

    public void Dispose()
    {
        _flushTimer.Stop();
        _transcribeCts?.Cancel();
        _state.FlushCache();
        _state.FlushTranscripts();
        _player.Dispose();
    }
}
