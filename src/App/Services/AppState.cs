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
    public string Engine { get; set; } = "whisper";   // "whisper" | "parakeet"
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
    public Parakeet Parakeet { get; private set; }
    public DeepFilter DeepFilter { get; }
    public bool CudaAvailable { get; }
    public MetaCache MetaCache { get; }

    private static bool DetectCuda()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi", Arguments = "-L",
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            // Drain both pipes asynchronously and bound the wait: if nvidia-smi stalls (driver
            // init / hung driver) the synchronous ReadToEnd would block app startup forever.
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { } return false; }
            string o = outTask.Wait(500) ? outTask.Result : "";
            return o.Contains("GPU");
        }
        catch { return false; }
    }
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

        CudaAvailable = DetectCuda();
        string sherpaExe = Path.Combine(AppContext.BaseDirectory, "tools", "parakeet", "bin", "sherpa-onnx-offline.exe");
        string parakeetModel = Path.Combine(AppContext.BaseDirectory, "tools", "parakeet", "model");
        Parakeet = new Parakeet(sherpaExe, parakeetModel, CudaAvailable);

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
                root["Engine"] = Config.Engine;

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
        // The corpus is built from the previous package set; drop it so it's rebuilt against the
        // newly indexed packages (otherwise ASR output is snapped to stale localized strings).
        lock (_corpusLock) { Corpus = null; }
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

    /// <summary>Decode a sound to a private per-entry WAV for transcription. The shared playback
    /// cache is keyed by TagId alone, but two SoundEntry records can share a TagId (different
    /// package/size); keying this on TagId+Size — matching the transcript cache key — stops
    /// parallel workers reading each other's audio or deleting a decode still in use. Returns the
    /// path (caller deletes it) or null on failure.</summary>
    private string? DecodeToWavForTranscribe(SoundEntry s)
    {
        if (Manager == null) return null;
        string baseName = $"tr_{s.TagId}_{s.Size}";
        string wem = Path.Combine(TempDir, baseName + ".wem");
        string wav = Path.Combine(DecodeCacheDir, baseName + ".wav");
        Directory.CreateDirectory(TempDir);
        try { File.WriteAllBytes(wem, Manager.ReadWem(s)); } catch { return null; }
        bool ok = Vgm.DecodeToWav(wem, wav);
        try { File.Delete(wem); } catch { }
        return ok ? wav : null;
    }
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

    private const string SherpaUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.2/sherpa-onnx-v1.13.2-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2";
    // int8, not fp16: the fp16 TDT model returns empty output under onnxruntime's CUDA provider,
    // while int8 runs correctly on the GPU (and is half the size).
    private const string ParakeetModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2";

    // CUDA 12.x runtime that onnxruntime's CUDA provider needs (the sherpa bundle ships only the
    // onnxruntime dlls, not cuBLAS/cudart/cuFFT/cuRAND/cuDNN). Driver is forward-compatible.
    private const string CudaRedist = "https://developer.download.nvidia.com/compute/cuda/redist/";
    private const string CudnnRedist = "https://developer.download.nvidia.com/compute/cudnn/redist/";
    // Each redist zip paired with a representative DLL it must produce, so an interrupted setup
    // can resume by fetching only the zips whose marker is still missing.
    private static readonly (string url, string marker)[] CudaLibZips =
    {
        (CudaRedist + "libcublas/windows-x86_64/libcublas-windows-x86_64-12.6.4.1-archive.zip", "cublasLt64_12.dll"),
        (CudaRedist + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.6.77-archive.zip", "cudart64_12.dll"),
        (CudaRedist + "libcufft/windows-x86_64/libcufft-windows-x86_64-11.3.0.4-archive.zip", "cufft64_11.dll"),
        (CudaRedist + "libcurand/windows-x86_64/libcurand-windows-x86_64-10.3.7.77-archive.zip", "curand64_10.dll"),
        (CudnnRedist + "cudnn/windows-x86_64/cudnn-windows-x86_64-9.8.0.87_cuda12-archive.zip", "cudnn64_9.dll"),
    };

    /// <summary>Download + extract the sherpa-onnx runtime and the Parakeet model on first enable.</summary>
    public async Task<bool> EnsureParakeetAsync(Action<double, string> progress, CancellationToken ct)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "tools", "parakeet");
        string binDir = Path.Combine(root, "bin");
        string modelDir = Path.Combine(root, "model");
        Directory.CreateDirectory(root);

        if (!File.Exists(Path.Combine(binDir, "sherpa-onnx-offline.exe")))
        {
            await FetchTarBz2Async(SherpaUrl, root, "sherpa.tar.bz2", "Downloading speech engine", 0, 0.05, progress, ct);
            string? ex = Directory.GetDirectories(root, "sherpa-onnx-v*").FirstOrDefault();
            if (ex != null) { CopyDir(Path.Combine(ex, "bin"), binDir); try { Directory.Delete(ex, true); } catch { } }
        }

        // GPU runtime: onnxruntime's CUDA provider needs cuBLAS/cudart/cuFFT/cuRAND/cuDNN. Fetch
        // only the zips whose marker DLL is missing, so a partial/interrupted download resumes
        // correctly instead of leaving sherpa to fail at transcribe time on an absent DLL.
        if (CudaAvailable)
        {
            var missing = CudaLibZips.Where(z => !File.Exists(Path.Combine(binDir, z.marker))).ToList();
            for (int i = 0; i < missing.Count; i++)
                await FetchZipDllsAsync(missing[i].url, binDir, "Downloading GPU runtime",
                    0.05 + 0.20 * i / missing.Count, 0.05 + 0.20 * (i + 1) / missing.Count, progress, ct);
        }

        // Require the *full* model (encoder + decoder + joiner + tokens) — an interrupted prior
        // run could have left only encoder*.onnx, which would otherwise skip the download forever
        // and leave Parakeet permanently unavailable.
        bool ModelComplete() => Directory.Exists(modelDir)
            && Directory.EnumerateFiles(modelDir, "encoder*.onnx").Any()
            && Directory.EnumerateFiles(modelDir, "decoder*.onnx").Any()
            && Directory.EnumerateFiles(modelDir, "joiner*.onnx").Any()
            && Directory.EnumerateFiles(modelDir, "tokens*.txt").Any();
        if (!ModelComplete())
        {
            await FetchTarBz2Async(ParakeetModelUrl, root, "model.tar.bz2", "Downloading Parakeet model (~1 GB)", 0.25, 1.0, progress, ct);
            string? ex = Directory.GetDirectories(root, "sherpa-onnx-nemo-parakeet*").FirstOrDefault();
            if (ex != null)
            {
                Directory.CreateDirectory(modelDir);
                foreach (string f in Directory.GetFiles(ex)) File.Move(f, Path.Combine(modelDir, Path.GetFileName(f)), true);
                try { Directory.Delete(ex, true); } catch { }
            }
        }
        Parakeet = new Parakeet(Path.Combine(binDir, "sherpa-onnx-offline.exe"), modelDir, CudaAvailable);
        progress(1, Parakeet.Available ? $"Parakeet ready ({(Parakeet.UsesCuda ? "GPU" : "CPU")})." : "Parakeet download failed.");
        return Parakeet.Available;
    }

    private static async Task FetchTarBz2Async(string url, string dir, string fname, string label,
        double lo, double hi, Action<double, string> progress, CancellationToken ct)
    {
        string archive = Path.Combine(dir, fname);
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(40) })
        using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = File.Create(archive);
            var buf = new byte[1 << 20]; long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total is long t && t > 0) progress(lo + (hi - lo) * 0.9 * read / t, $"{label}: {read / 1048576}/{t / 1048576} MB");
            }
        }
        progress(lo + (hi - lo) * 0.92, "Extracting…");
        // Windows' bundled tar (bsdtar) auto-detects bzip2.
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "tar", UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-xf"); psi.ArgumentList.Add(archive); psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir);
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            // Cancelling only the await would leave tar writing into tools/parakeet; kill the child too.
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            await p.WaitForExitAsync(ct);
        }
        try { File.Delete(archive); } catch { }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    /// <summary>Download an NVIDIA redist .zip and extract its bin/*.dll into <paramref name="binDir"/>.</summary>
    private static async Task FetchZipDllsAsync(string url, string binDir, string label,
        double lo, double hi, Action<double, string> progress, CancellationToken ct)
    {
        string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(40) })
        using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = File.Create(tmp);
            var buf = new byte[1 << 20]; long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total is long t && t > 0) progress(lo + (hi - lo) * read / t, $"{label}: {read / 1048576} MB");
            }
        }
        try
        {
            using var za = System.IO.Compression.ZipFile.OpenRead(tmp);
            foreach (var e in za.Entries)
            {
                string fn = e.FullName.Replace('\\', '/');
                if (fn.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && fn.Contains("/bin/"))
                {
                    using var es = e.Open();
                    using var fs = File.Create(Path.Combine(binDir, e.Name));
                    es.CopyTo(fs);
                }
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public Whisper.Result? Transcribe(SoundEntry s, int threads, CancellationToken ct, bool cleanupDecoded = false, bool force = false)
    {
        string key = CacheKey(s);
        if (!force && TranscriptCache.TryGet(key, out var cached))
            return new Whisper.Result
            {
                Text = cached.Text, Language = cached.Language,
                NoSpeech = cached.NoSpeech, Segments = cached.Segments.ToList(),
            };

        if (Manager == null) return null;
        bool useParakeet = Config.Engine == "parakeet" && Parakeet.Available;
        if (!useParakeet && !Whisper.Available) return null;
        EnsureCorpus();

        // Per-entry decode (not the shared {TagId}.wav cache) so same-TagId entries don't collide
        // in a parallel batch. Reused below for the optional denoise pass.
        string? full = DecodeToWavForTranscribe(s);
        string? wav16 = full != null ? Resample16kMono(full) : null;
        if (wav16 == null)
        {
            if (full != null) try { File.Delete(full); } catch { }
            return null;
        }

        // Engine dispatch: Parakeet (sherpa-onnx, GPU) when selected/available, else whisper.cpp.
        Whisper.Result? Run(string w) => useParakeet
            ? Parakeet.Transcribe(w, threads, ct)
            : Whisper.Transcribe(w, threads, ct, WhisperPrompt, useVad: false);
        try
        {
            Whisper.Result? r = Run(wav16);
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
                    string dnDir = Path.Combine(TempDir, "denoise");
                    string? dnFull = full != null ? DeepFilter.Denoise(full, dnDir, ct) : null;
                    string? dn16 = dnFull != null ? Resample16kMono(dnFull) : null;
                    if (dn16 != null)
                    {
                        var r2 = Run(dn16);
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
            // The per-entry decode is transcription-only; drop it in batches to bound temp growth.
            if (cleanupDecoded && full != null)
                try { File.Delete(full); } catch { }
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
        int threadsPerWorker = 0,
        bool force = false)
    {
        var (w, t) = ResolveConcurrency(workers, threadsPerWorker);
        // force => re-transcribe everything (e.g. user switched engines); otherwise skip cached.
        // Empty/placeholder WEM stubs can't decode, so drop them up front — otherwise an all-catalog
        // run keeps re-queuing the same known-invalid entries on every rerun (no "all cached" fast path).
        var todo = (force ? targets : targets.Where(s => !TranscriptCache.TryGet(CacheKey(s), out _)))
                   .Where(s => !s.IsEmpty).ToList();
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
                Whisper.Result? r = Transcribe(s, t, token, cleanupDecoded, force);
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
