using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Tiger;

/// <summary>Wraps DeepFilterNet's `deep-filter` CLI for speech denoising (48 kHz WAV in/out).</summary>
public sealed class DeepFilter
{
    private readonly string _exe;
    public DeepFilter(string exePath) => _exe = exePath;
    public bool Available => File.Exists(_exe);

    /// <summary>Denoise a WAV; returns the denoised file path (same name under <paramref name="outDir"/>) or null.
    /// Honours <paramref name="ct"/> so a Cancel/window-close kills the in-flight child.</summary>
    public string? Denoise(string wavPath, string outDir, CancellationToken ct = default)
    {
        if (!Available || !File.Exists(wavPath) || ct.IsCancellationRequested) return null;
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        try
        {
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            // Drain stderr on a separate task so a full pipe can't deadlock the stdout read.
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            try { errTask.Wait(); } catch { }
            p.WaitForExit();
            if (ct.IsCancellationRequested) return null;
            string outp = Path.Combine(outDir, Path.GetFileName(wavPath));
            return p.ExitCode == 0 && File.Exists(outp) ? outp : null;
        }
        catch { return null; }
    }
}
