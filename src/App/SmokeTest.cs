using System;
using System.Linq;
using MarathonAudio.App.Services;
using MarathonAudio.App.ViewModels;
using Tiger;

namespace MarathonAudio.App;

/// <summary>Headless exercise of the real service layer used by the GUI.</summary>
public static class SmokeTest
{
    public static void Run()
    {
        var state = AppState.Instance;
        Console.WriteLine($"GameDir valid: {state.GameDirValid} ({state.Config.GameDir})");
        Console.WriteLine($"vgmstream available: {state.Vgm.Available}");
        if (!state.GameDirValid) { Console.WriteLine("ABORT: game dir invalid"); return; }

        var mgr = state.BuildIndex((p, msg) => { });
        Console.WriteLine($"Indexed {mgr.Sounds.Count} sounds / {mgr.PackageCount} packages");

        var s = mgr.Sounds[100];
        var hdr = mgr.LoadHeader(s);
        Console.WriteLine($"Selected {s.TagId} {s.PackageName} codec={hdr.CodecName} ch={hdr.Channels} rate={hdr.SampleRate}");

        string? wav = state.DecodeToWav(s);
        Console.WriteLine($"DecodeToWav -> {(wav ?? "NULL")}");
        if (wav == null) { Console.WriteLine("ABORT: decode failed"); return; }

        var player = new AudioPlayer { Volume = 0.5f };
        float[] peaks = player.LoadFile(wav);
        Console.WriteLine($"Peaks: {peaks.Length} cols, max={peaks.Max():F3}, dur={player.Duration:mm\\:ss}, outputDevice={player.HasOutputDevice}");
        player.Dispose();

        // row preview path + cache
        bool wasCached = state.MetaCache.TryGet(state.CacheKey(s), out _);
        var swp = System.Diagnostics.Stopwatch.StartNew();
        var prev = state.LoadRowPreview(s);
        Console.WriteLine($"RowPreview ({swp.ElapsedMilliseconds} ms, cachedAtStart={wasCached}): meta='{prev?.meta}' dur='{prev?.duration}' peaks={prev?.peaks.Length}");
        var swc = System.Diagnostics.Stopwatch.StartNew();
        var cm2 = state.GetPreview(s);
        Console.WriteLine($"GetPreview cache hit ({swc.ElapsedMilliseconds} ms): peaks={cm2.Peaks.Length}, cacheCount={state.MetaCache.Count}");
        state.FlushCache();
        Console.WriteLine("cache flushed to disk");

        // exercise a multichannel sound through the downmix player
        SoundEntry? mc = mgr.Sounds.Take(2000).FirstOrDefault(x => mgr.LoadHeader(x).Channels > 2);
        if (mc != null)
        {
            string? mcWav = state.DecodeToWav(mc);
            var p2 = new AudioPlayer();
            var mcPeaks = mcWav != null ? p2.LoadFile(mcWav) : Array.Empty<float>();
            Console.WriteLine($"Multichannel {mc.TagId} ({mgr.LoadHeader(mc).Channels}ch) -> downmix loaded, peaks={mcPeaks.Length}");
            p2.Dispose();
        }
        Console.WriteLine("SMOKE OK");
    }
}
