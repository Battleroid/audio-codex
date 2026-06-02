using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tiger;

/// <summary>Runs NVIDIA Parakeet (TDT v2) via sherpa-onnx-offline.exe as an alternative ASR engine.
/// Mirrors <see cref="Whisper"/>: a bundled CLI invoked as a subprocess on a 16 kHz mono WAV.</summary>
public sealed class Parakeet
{
    private readonly string _exe;
    private readonly string _modelDir;
    private readonly bool _cuda;

    public Parakeet(string exePath, string modelDir, bool preferCuda)
    {
        _exe = exePath; _modelDir = modelDir;
        // CUDA needs cuBLAS (cublasLt64_12.dll) which the sherpa-onnx bundle omits; use the GPU
        // only when that runtime is actually resolvable, otherwise fall back to (fast) CPU.
        _cuda = preferCuda && CublasPresent(Path.GetDirectoryName(exePath));
    }

    public bool UsesCuda => _cuda;

    private static bool CublasPresent(string? binDir)
    {
        if (binDir != null && File.Exists(Path.Combine(binDir, "cublasLt64_12.dll"))) return true;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), "cublasLt64_12.dll"))) return true; }
            catch { }
        }
        return false;
    }

    private string? Find(string pattern) =>
        Directory.Exists(_modelDir)
            ? Directory.EnumerateFiles(_modelDir, pattern).OrderBy(p => p.Length).FirstOrDefault()
            : null;

    public string? Encoder => Find("encoder*.onnx");
    public string? Decoder => Find("decoder*.onnx");
    public string? Joiner  => Find("joiner*.onnx");
    public string? Tokens  => Find("tokens*.txt");

    public bool Available =>
        File.Exists(_exe) && Encoder != null && Decoder != null && Joiner != null && Tokens != null;

    public Whisper.Result? Transcribe(string wav16kPath, int threads, CancellationToken ct)
    {
        if (!Available || ct.IsCancellationRequested) return null;

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            WorkingDirectory = Path.GetDirectoryName(_exe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add($"--tokens={Tokens}");
        psi.ArgumentList.Add($"--encoder={Encoder}");
        psi.ArgumentList.Add($"--decoder={Decoder}");
        psi.ArgumentList.Add($"--joiner={Joiner}");
        psi.ArgumentList.Add($"--num-threads={Math.Max(1, threads)}");
        psi.ArgumentList.Add($"--provider={(_cuda ? "cuda" : "cpu")}");
        psi.ArgumentList.Add(wav16kPath);

        try
        {
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();   // drain logs (config dump etc.)
            p.WaitForExit();
            if (ct.IsCancellationRequested || p.ExitCode != 0) return null;

            string text = ExtractText(outp);
            var res = new Whisper.Result { Text = text.Trim(), Language = "en" };
            res.NoSpeech = res.Text.Length == 0;
            if (!res.NoSpeech) res.Segments.Add(new TranscriptSegment { From = 0, To = 0, Text = res.Text });
            return res;
        }
        catch { return null; }
    }

    /// <summary>sherpa-onnx-offline prints a JSON result containing "text"; fall back to a regex.</summary>
    private static string ExtractText(string output)
    {
        foreach (Match m in Regex.Matches(output, "\\{[^{}]*\"text\"[^{}]*\\}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Value);
                if (doc.RootElement.TryGetProperty("text", out var t)) return t.GetString() ?? "";
            }
            catch { }
        }
        var rx = Regex.Match(output, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return rx.Success ? Regex.Unescape(rx.Groups[1].Value) : "";
    }
}
