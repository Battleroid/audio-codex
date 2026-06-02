using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Tiger;

namespace MarathonAudio.App.Services;

public sealed class AppConfig
{
    public string? GameDir { get; set; }
    public string? ExportDir { get; set; }
    public string? WordlistFile { get; set; }
    public float Volume { get; set; } = 0.6f;
    public bool DenoiseFallback { get; set; }   // retry borderline transcripts on denoised audio
    // Transcription concurrency overrides; null/0 = auto-adapt to the machine.
    public int? TranscribeWorkers { get; set; }
    public int? TranscribeThreads { get; set; }
}

/// <summary>Holds configuration, the package manager, and decode services.</summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public AppConfig Config { get; private set; } = new();
    public PackageManager? Manager { get; private set; }
    public Vgmstream Vgm { get; }
    public Whisper Whisper { get; }
    public DeepFilter DeepFilter { get; }
    public MetaCache MetaCache { get; }
    public TranscriptCache TranscriptCache { get; }
    public TranscriptIndex Index { get; } = new();

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

        string whisperExe = Path.Combine(AppContext.BaseDirectory, "tools", "whisper", "whisper-cli.exe");
        string whisperModel = Path.Combine(AppContext.BaseDirectory, "tools", "whisper", "ggml-base.en.bin");
        string whisperVad = Path.Combine(AppContext.BaseDirectory, "tools", "whisper", "ggml-silero-v5.1.2.bin");
        Whisper = new Whisper(whisperExe, whisperModel, whisperVad);
        DeepFilter = new DeepFilter(Path.Combine(AppContext.BaseDirectory, "tools", "deepfilter", "deep-filter.exe"));

        MetaCache = new MetaCache(Path.Combine(appData, "meta-cache.bin"));
        MetaCache.Load();

        TranscriptCache = new TranscriptCache(Path.Combine(appData, "transcript-cache.bin"));
        TranscriptCache.Load();

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
                root["TranscribeWorkers"] = Config.TranscribeWorkers;
                root["TranscribeThreads"] = Config.TranscribeThreads;
                root["DenoiseFallback"] = Config.DenoiseFallback;

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
    public void SetTranscribeConcurrency(int? workers, int? threads)
    {
        Config.TranscribeWorkers = workers is > 0 ? workers : null;
        Config.TranscribeThreads = threads is > 0 ? threads : null;
        Save();
    }

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
    public void FlushTranscripts() => TranscriptCache.Flush();

    // ================= Transcription =================

    private static readonly string[] VoiceTokens =
        { "vo", "vox", "voice", "dlg", "dialog", "dialogue", "narr", "bark", "chatter", "comm", "announc" };

    /// <summary>Resolve effective (workers, threads-per-worker): configured override, else
    /// auto-adapt to the core count (≈ N/4 processes, the rest as threads each).</summary>
    public (int workers, int threads) ResolveConcurrency(int workers = 0, int threads = 0)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        int w = workers > 0 ? workers : Config.TranscribeWorkers ?? 0;
        int t = threads > 0 ? threads : Config.TranscribeThreads ?? 0;
        if (w <= 0) w = Math.Clamp(cores / 4, 1, 8);
        if (t <= 0) t = Math.Max(1, cores / w);
        return (w, t);
    }

    /// <summary>Likely voice/dialogue sounds, identified by soundbank/name tokens. Empty when no
    /// wordlist is loaded (names unresolved) — transcribe a group or "all" in that case.</summary>
    public IReadOnlyList<SoundEntry> VoiceBankSounds()
    {
        if (Manager == null) return Array.Empty<SoundEntry>();
        return Manager.Sounds.Where(IsVoiceCandidate).ToList();
    }

    private static bool IsVoiceCandidate(SoundEntry s)
    {
        string hay = ((s.SoundbankName ?? "") + " " + (s.Name ?? "")).ToLowerInvariant();
        return hay.Length > 0 && VoiceTokens.Any(tok => hay.Contains(tok));
    }

    /// <summary>Decode a sound and produce a 16 kHz mono 16-bit WAV (what whisper requires).
    /// Returns the temp path (caller deletes it) or null on failure.</summary>
    public string? DecodeTo16kMonoWav(SoundEntry s)
    {
        string? src = DecodeToWav(s);
        return src == null ? null : Resample16kMono(src);
    }

    /// <summary>Resample any WAV to the 16 kHz mono 16-bit WAV whisper requires.</summary>
    private string? Resample16kMono(string src)
    {
        if (!File.Exists(src)) return null;
        string dir = Path.Combine(TempDir, "whisper");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, $"{Guid.NewGuid():N}.16k.wav");
        using var reader = new AudioFileReader(src);
        ISampleProvider mono = reader.WaveFormat.Channels == 1 ? reader : new MonoSampleProvider(reader);
        var resampler = new WdlResamplingSampleProvider(mono, 16000);
        WaveFileWriter.CreateWaveFile16(outPath, resampler);
        return outPath;
    }

    /// <summary>Transcribe a single sound, cache-first (like <see cref="GetPreview"/>). When
    /// <paramref name="cleanupDecoded"/> is set the cached playback WAV is removed afterwards
    /// (used by big batches to bound temp-disk growth).</summary>
    // Light domain prime so whisper expects terse comms/AI-announcement register and key terms.
    private const string WhisperPrompt =
        "UESC mission comms: terse AI announcements and callouts. " +
        "Terms include exfil, compiler, husk, runner, cryo, signal detected.";

    public StringCorpus? Corpus { get; private set; }
    private readonly object _corpusLock = new();

    /// <summary>Build (once) the English localized-string corpus used to correct ASR output.</summary>
    public StringCorpus EnsureCorpus(Action<double, string>? progress = null)
    {
        if (Corpus != null) return Corpus;
        lock (_corpusLock)
        {
            if (Corpus == null && Manager != null) Corpus = Manager.BuildStringCorpus(progress);
            return Corpus ??= new StringCorpus();
        }
    }

    /// <summary>Re-run corpus correction over already-cached transcripts (e.g. ones made before
    /// the corpus existed). Returns the number whose text changed.</summary>
    public int RecorrectCache(Action<double, string>? progress = null)
    {
        progress?.Invoke(0, "Building text corpus…");
        EnsureCorpus((p, m) => progress?.Invoke(p * 0.3, m));
        if (Corpus is not { Ready: true } corp) return 0;

        var entries = TranscriptCache.Entries.ToList();
        int changed = 0, done = 0;
        foreach (var (key, c) in entries)
        {
            done++;
            if (done % 200 == 0) progress?.Invoke(0.3 + 0.7 * done / entries.Count, $"Correcting {done}/{entries.Count}");
            if (c.NoSpeech) continue;
            string raw = string.IsNullOrEmpty(c.RawText) ? c.Text : c.RawText;
            var m = corp.Correct(raw);
            string newText = m is { } hit ? hit.text : raw;
            bool nowCorrected = m is not null;
            if (c.Text != newText || c.Corrected != nowCorrected || c.RawText != raw)
            {
                c.RawText = raw; c.Text = newText; c.Corrected = nowCorrected; c.MatchScore = m?.score ?? 0;
                TranscriptCache.Set(key, c);
                changed++;
            }
        }
        TranscriptCache.Flush();
        progress?.Invoke(1, $"Corrected {changed} transcripts.");
        return changed;
    }

    public Whisper.Result? Transcribe(SoundEntry s, int threads, CancellationToken ct, bool cleanupDecoded = false)
    {
        string key = CacheKey(s);
        if (TranscriptCache.TryGet(key, out var cached))
            return new Whisper.Result
            {
                Text = cached.Text, Language = cached.Language,
                NoSpeech = cached.NoSpeech, Segments = cached.Segments.ToList(),
            };

        if (Manager == null || !Whisper.Available) return null;
        EnsureCorpus();

        string? wav16 = DecodeTo16kMonoWav(s);
        if (wav16 == null) return null;
        try
        {
            // VAD is bundled but off by default: testing showed it drops ~38% of real speech
            // (it kills hallucinations too, but losing genuine short lines is worse for our purpose).
            Whisper.Result? r = Whisper.Transcribe(wav16, threads, ct, WhisperPrompt, useVad: false);
            if (r != null)
            {
                string rawText = r.Text;
                bool ready = !r.NoSpeech && Corpus is { Ready: true };
                var m = ready ? Corpus!.Correct(rawText, 0.2) : null;   // best corpus match (score >= 0.2)

                // Gated denoise retry (opt-in): only for promising-but-unconfirmed lines, and only
                // adopt the denoised result if it matches the corpus strictly better. Blanket denoise
                // hurts ASR (validated), so this never touches confirmed or clearly-garbage lines.
                if (Config.DenoiseFallback && DeepFilter.Available && ready && m is { } mm && mm.score < 0.62)
                {
                    string? full = DecodeToWav(s);
                    string dnDir = Path.Combine(TempDir, "denoise");
                    string? dnFull = full != null ? DeepFilter.Denoise(full, dnDir) : null;
                    string? dn16 = dnFull != null ? Resample16kMono(dnFull) : null;
                    if (dn16 != null)
                    {
                        var r2 = Whisper.Transcribe(dn16, threads, ct, WhisperPrompt, useVad: false);
                        try { File.Delete(dn16); } catch { }
                        try { File.Delete(dnFull!); } catch { }
                        if (r2 is { NoSpeech: false } && Corpus!.Correct(r2.Text, 0.2) is { } m2 && m2.score > mm.score)
                        { r = r2; rawText = r2.Text; m = m2; }
                    }
                }

                var c = new CachedTranscript
                {
                    RawText = rawText, Text = rawText, Language = r.Language,
                    NoSpeech = r.NoSpeech, Segments = r.Segments.ToArray(),
                };
                if (m is { } hit && hit.score >= 0.62)   // confirmed -> snap to canonical line
                {
                    c.Text = hit.text; c.Corrected = true; c.MatchScore = hit.score; r.Text = hit.text;
                }
                TranscriptCache.Set(key, c);
            }
            return r;
        }
        finally
        {
            try { File.Delete(wav16); } catch { }
            if (cleanupDecoded)
                try { File.Delete(Path.Combine(DecodeCacheDir, $"{s.TagId}.wav")); } catch { }
        }
    }

    /// <summary>Transcribe a list of sounds in parallel (W workers × T threads). Skips anything
    /// already cached so runs resume and never repeat work. Flushes incrementally; honours cancel.
    /// Returns (processed-this-run, with-speech-this-run).</summary>
    public async Task<(int processed, int speech)> BuildTranscripts(
        IReadOnlyList<SoundEntry> targets,
        Action<double, string> progress,
        CancellationToken ct,
        bool cleanupDecoded = true,
        int workers = 0,
        int threadsPerWorker = 0)
    {
        var (w, t) = ResolveConcurrency(workers, threadsPerWorker);
        var todo = targets.Where(s => !TranscriptCache.TryGet(CacheKey(s), out _)).ToList();
        int total = todo.Count;
        if (total == 0)
        {
            progress(1, "Nothing to transcribe (all cached).");
            BuildTranscriptIndex();
            return (0, 0);
        }

        // Build the canonical-text corpus once up front so each transcript can be snapped to it.
        if (Corpus == null) progress(0, "Building text corpus…");
        EnsureCorpus((p, m) => progress(p * 0.05, m));

        int done = 0, speech = 0, sinceFlush = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = w, CancellationToken = ct };
        try
        {
            await Parallel.ForEachAsync(todo, opts, (s, token) =>
            {
                Whisper.Result? r = Transcribe(s, t, token, cleanupDecoded);
                int d = Interlocked.Increment(ref done);
                if (r is { NoSpeech: false, Text.Length: > 0 }) Interlocked.Increment(ref speech);
                if (Interlocked.Increment(ref sinceFlush) >= 50)
                {
                    Interlocked.Exchange(ref sinceFlush, 0);
                    TranscriptCache.Flush();
                }
                progress(d / (double)total, $"Transcribing {d}/{total} • {Volatile.Read(ref speech)} with speech");
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) { /* partial progress is preserved below */ }
        finally
        {
            TranscriptCache.Flush();
            BuildTranscriptIndex();
        }
        return (done, speech);
    }

    /// <summary>Rebuild the in-memory search index from cached transcripts (excludes no-speech).</summary>
    public void BuildTranscriptIndex()
    {
        if (Manager == null) { Index.Build(Array.Empty<(SoundEntry, string)>()); return; }
        var items = new List<(SoundEntry, string)>();
        foreach (SoundEntry s in Manager.Sounds)
            if (TranscriptCache.TryGet(CacheKey(s), out var c) && !c.NoSpeech && c.Text.Length > 0)
                items.Add((s, c.Text));
        Index.Build(items);
    }
}
