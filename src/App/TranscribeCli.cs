using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarathonAudio.App.Services;
using Tiger;

namespace MarathonAudio.App;

/// <summary>Headless batch transcription: `--transcribe` (voice banks) or `--transcribe all`.
/// Intended for the one-time multi-hour run; resumes from the cache, so re-running only
/// processes what's left.</summary>
public static class TranscribeCli
{
    public static async Task Run(string[] args)
    {
        var state = AppState.Instance;
        Console.WriteLine($"GameDir valid: {state.GameDirValid} ({state.Config.GameDir})");
        Console.WriteLine($"whisper available: {state.Whisper.Available}");
        if (!state.GameDirValid) { Console.WriteLine("ABORT: game dir invalid"); return; }
        if (!state.Whisper.Available)
        {
            Console.WriteLine("ABORT: whisper-cli.exe / ggml-base.en.bin not found under tools/whisper");
            return;
        }

        var mgr = state.BuildIndex((p, msg) => { });
        Console.WriteLine($"Indexed {mgr.Sounds.Count} sounds / {mgr.PackageCount} packages");

        bool all = args.Skip(1).Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));
        if (!all)
        {
            // Voice-bank detection needs resolved names; build soundbanks so they're populated.
            mgr.BuildSoundbanks((p, msg) => { });
        }

        var targets = all ? (System.Collections.Generic.IReadOnlyList<SoundEntry>)mgr.Sounds
                          : state.VoiceBankSounds();
        if (!all && targets.Count == 0)
        {
            Console.WriteLine("No voice banks identified (a wordlist is needed to name banks).");
            Console.WriteLine("Use '--transcribe all', or transcribe a specific group/selection in the UI.");
            return;
        }

        var (w, t) = state.ResolveConcurrency();
        Console.WriteLine($"Transcribing {targets.Count:N0} sounds ({(all ? "all" : "voice banks")}) " +
                          $"with {w} workers × {t} threads…");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (processed, speech) = await state.BuildTranscripts(
            targets,
            (p, msg) => Console.Write($"\r{p:P0} {msg}                              "),
            CancellationToken.None,
            cleanupDecoded: true);
        Console.WriteLine();
        state.FlushTranscripts();
        Console.WriteLine($"Done in {sw.Elapsed:hh\\:mm\\:ss}: processed {processed:N0}, " +
                          $"{speech:N0} with speech, cache size {state.TranscriptCache.Count:N0}");
    }
}
