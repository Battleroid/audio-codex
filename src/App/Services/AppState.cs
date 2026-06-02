using System;
using System.IO;
using System.Text.Json;
using Tiger;

namespace MarathonAudio.App.Services;

public sealed class AppConfig
{
    public string? GameDir { get; set; }
    public string? ExportDir { get; set; }
    public string? WordlistFile { get; set; }
    public float Volume { get; set; } = 0.6f;
}

/// <summary>Holds configuration, the package manager, and decode services.</summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public AppConfig Config { get; private set; } = new();
    public PackageManager? Manager { get; private set; }
    public Vgmstream Vgm { get; }
    public MetaCache MetaCache { get; }

    public string TempDir { get; }
    public string DecodeCacheDir { get; }
    private readonly string _configPath;

    private AppState()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarathonAudio");
        Directory.CreateDirectory(appData);
        _configPath = Path.Combine(appData, "config.json");
        TempDir = Path.Combine(Path.GetTempPath(), "MarathonAudio");
        DecodeCacheDir = Path.Combine(TempDir, "decoded");
        // Clear last session's decoded cache to avoid unbounded growth.
        try { if (Directory.Exists(DecodeCacheDir)) Directory.Delete(DecodeCacheDir, true); } catch { }
        Directory.CreateDirectory(DecodeCacheDir);

        string toolsExe = Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream", "vgmstream-cli.exe");
        Vgm = new Vgmstream(toolsExe);

        MetaCache = new MetaCache(Path.Combine(appData, "meta-cache.bin"));
        MetaCache.Load();

        Load();
    }

    public string PackagesDir => Path.Combine(Config.GameDir ?? "", "packages");
    public string OodleDll => Path.Combine(Config.GameDir ?? "", "bin", "x64", "oo2core_9_win64.dll");
    public string WordlistPath =>
        !string.IsNullOrEmpty(Config.WordlistFile) && File.Exists(Config.WordlistFile)
            ? Config.WordlistFile!
            : Path.Combine(AppContext.BaseDirectory, "wordlist.txt");
    public bool HasExportDir => !string.IsNullOrEmpty(Config.ExportDir) && Directory.Exists(Config.ExportDir);

    public bool GameDirValid =>
        !string.IsNullOrEmpty(Config.GameDir) &&
        Directory.Exists(PackagesDir) && File.Exists(OodleDll);

    public void Load()
    {
        try
        {
            if (File.Exists(_configPath))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath)) ?? new();
        }
        catch { Config = new(); }

        // Auto-detect common Steam path if not set
        if (string.IsNullOrEmpty(Config.GameDir))
        {
            string guess = @"A:\Steam\steamapps\common\Marathon";
            if (Directory.Exists(guess)) Config.GameDir = guess;
        }
    }

    private readonly object _saveLock = new();

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                // Merge into the existing file so unknown keys (e.g. settings written by a
                // newer/older build) are preserved rather than wiped.
                System.Text.Json.Nodes.JsonObject root;
                try
                {
                    root = File.Exists(_configPath)
                        ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_configPath)) as System.Text.Json.Nodes.JsonObject ?? new()
                        : new();
                }
                catch { root = new(); }

                root["GameDir"] = Config.GameDir;
                root["ExportDir"] = Config.ExportDir;
                root["WordlistFile"] = Config.WordlistFile;
                root["Volume"] = Config.Volume;

                File.WriteAllText(_configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    public void SetGameDir(string dir)
    {
        Config.GameDir = dir;
        Save();
    }

    public void SetVolume(float v) { Config.Volume = v; Save(); }
    public void SetExportDir(string? dir) { Config.ExportDir = dir; Save(); }
    public void SetWordlistFile(string? path) { Config.WordlistFile = path; Save(); }

    /// <summary>Build (or rebuild) the package manager and index. Call off the UI thread.</summary>
    public PackageManager BuildIndex(Action<double, string> progress)
    {
        var mgr = new PackageManager(PackagesDir, OodleDll);
        if (File.Exists(WordlistPath)) mgr.LoadWordlist(WordlistPath);
        mgr.Index(progress);
        Manager = mgr;
        return mgr;
    }

    /// <summary>Extract + decode a sound to a cached WAV; returns the WAV path (or null on failure).</summary>
    public string? DecodeToWav(SoundEntry s)
    {
        string wav = Path.Combine(DecodeCacheDir, $"{s.TagId}.wav");
        if (File.Exists(wav) && new FileInfo(wav).Length > 44) return wav;
        if (Manager == null) return null;
        string wem = Manager.ExtractWemToTemp(s, TempDir);
        bool ok = Vgm.DecodeToWav(wem, wav);
        try { File.Delete(wem); } catch { }
        return ok ? wav : null;
    }

    public string CacheKey(SoundEntry s) => $"{s.TagId}:{s.Size}";
    private const int PeakCols = 600;

    /// <summary>Metadata + waveform peaks for a sound. Served from the on-disk cache;
    /// decodes (to a throwaway temp file) only the first time a sound is encountered.</summary>
    public CachedMeta GetPreview(SoundEntry s)
    {
        string key = CacheKey(s);
        if (MetaCache.TryGet(key, out var cached)) return cached;

        var cm = new CachedMeta();
        if (Manager != null)
        {
            byte[] data = Manager.ReadWem(s);
            WemInfo.Info info = WemInfo.Parse(data);
            cm.Codec = info.Codec; cm.Channels = info.Channels; cm.SampleRate = (int)info.SampleRate;

            string previewDir = Path.Combine(TempDir, "preview");
            Directory.CreateDirectory(previewDir);
            string wem = Path.Combine(previewDir, $"{s.TagId}_{Guid.NewGuid():N}.wem");
            string wav = wem + ".wav";
            try
            {
                File.WriteAllBytes(wem, data);
                if (Vgm.DecodeToWav(wem, wav))
                {
                    var (peaks, dur) = Services.AudioPlayer.Analyze(wav, PeakCols);
                    cm.Peaks = peaks; cm.Duration = dur.TotalSeconds;
                }
            }
            catch { }
            finally { try { File.Delete(wem); } catch { } try { File.Delete(wav); } catch { } }
        }
        MetaCache.Set(key, cm);
        return cm;
    }

    public (string meta, string duration, float[] peaks)? LoadRowPreview(SoundEntry s)
    {
        if (Manager == null) return null;
        CachedMeta cm = GetPreview(s);
        string rate = cm.SampleRate >= 1000 ? $"{cm.SampleRate / 1000.0:0.#} kHz" : $"{cm.SampleRate} Hz";
        string meta = $"{cm.Channels} ch · {rate}";
        string d = cm.Duration > 0 ? $"{(int)(cm.Duration / 60)}:{(int)(cm.Duration % 60):00}" : "—";
        return (meta, d, cm.Peaks);
    }

    public void FlushCache() => MetaCache.Flush();
}
