using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Tiger;

/// <summary>A single timestamped line of a transcript (seconds).</summary>
public sealed class TranscriptSegment
{
    public double From;
    public double To;
    public string Text = "";
}

/// <summary>Wraps whisper.cpp's whisper-cli for offline speech-to-text of decoded audio.
/// Mirrors <see cref="Vgmstream"/>: a bundled CLI invoked as a subprocess.</summary>
public sealed class Whisper
{
    private readonly string _exe;
    private readonly string _model;
    private readonly string? _vadModel;

    public Whisper(string exePath, string modelPath, string? vadModelPath = null)
    {
        _exe = exePath; _model = modelPath; _vadModel = vadModelPath;
    }

    /// <summary>True only when both the executable and the model file are present.</summary>
    public bool Available => File.Exists(_exe) && File.Exists(_model);

    /// <summary>Silero VAD model is bundled — enables speech gating to cut SFX/silence.</summary>
    public bool VadAvailable => !string.IsNullOrEmpty(_vadModel) && File.Exists(_vadModel);

    public sealed class Result
    {
        public string Text = "";
        public string Language = "";
        public bool NoSpeech;
        public List<TranscriptSegment> Segments = new();
    }

    /// <summary>Transcribe a 16 kHz mono WAV. <paramref name="wav16kPath"/> MUST already be
    /// 16 kHz mono 16-bit PCM — whisper-cli does not resample. Returns null on failure/cancel.</summary>
    public Result? Transcribe(string wav16kPath, int threads, CancellationToken ct, string? prompt = null, bool useVad = true)
    {
        if (!Available || ct.IsCancellationRequested) return null;

        string baseOut = Path.Combine(Path.GetTempPath(), "MarathonAudio", "whisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(baseOut)!);
        string jsonPath = baseOut + ".json";

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(_model);
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Math.Max(1, threads).ToString());
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add("en");
        psi.ArgumentList.Add("-oj");                       // write a JSON sidecar
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(baseOut);
        // Accuracy/robustness tuning for short, independent, effected game lines:
        psi.ArgumentList.Add("-mc"); psi.ArgumentList.Add("0");   // no cross-segment context (kills carryover hallucination)
        psi.ArgumentList.Add("-sns");                             // suppress non-speech tokens
        psi.ArgumentList.Add("-bs"); psi.ArgumentList.Add("5");   // beam search
        psi.ArgumentList.Add("-bo"); psi.ArgumentList.Add("5");
        if (useVad && VadAvailable)                               // Silero VAD: gate out SFX/silence
        {
            psi.ArgumentList.Add("--vad");
            psi.ArgumentList.Add("-vm"); psi.ArgumentList.Add(_vadModel!);
            psi.ArgumentList.Add("-vt"); psi.ArgumentList.Add("0.35");  // low threshold so effected speech isn't dropped
            psi.ArgumentList.Add("-vp"); psi.ArgumentList.Add("80");    // pad so onsets aren't clipped
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            psi.ArgumentList.Add("--prompt"); psi.ArgumentList.Add(prompt);
        }
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wav16kPath);

        try
        {
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            // Drain stderr on a separate task so a full pipe can't deadlock the read.
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            try { errTask.Wait(); } catch { }
            p.WaitForExit();
            if (ct.IsCancellationRequested || p.ExitCode != 0 || !File.Exists(jsonPath)) return null;
            return ParseJson(jsonPath);
        }
        catch { return null; }
        finally { try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { } }
    }

    private static Result ParseJson(string jsonPath)
    {
        var res = new Result();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("result", out var r) && r.TryGetProperty("language", out var lang))
            res.Language = lang.GetString() ?? "";

        if (root.TryGetProperty("transcription", out var tr) && tr.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (JsonElement seg in tr.EnumerateArray())
            {
                string text = seg.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
                double from = 0, to = 0;
                if (seg.TryGetProperty("offsets", out var off))
                {
                    if (off.TryGetProperty("from", out var ff)) from = ff.GetDouble() / 1000.0;
                    if (off.TryGetProperty("to", out var tt)) to = tt.GetDouble() / 1000.0;
                }
                res.Segments.Add(new TranscriptSegment { From = from, To = to, Text = text.Trim() });
                sb.Append(text);
            }
            res.Text = sb.ToString().Trim();
        }

        res.NoSpeech = IsNoSpeech(res.Text);
        return res;
    }

    // whisper.cpp's CLI does not reliably expose a per-segment no_speech_prob, so treat
    // empty output and its well-known silence/SFX hallucinations as "no speech".
    private static readonly string[] Boilerplate =
    {
        "[blank_audio]", "(silence)", "[silence]", "[ silence ]", "[music]", "(music)",
        "[no speech]", "you", "thank you", "thanks for watching",
    };

    private static bool IsNoSpeech(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return true;
        string low = t.ToLowerInvariant().Trim('.', ' ', '!', '?', ',', '-', '…', '*');
        if (low.Length == 0) return true;
        return Boilerplate.Contains(low);
    }
}
