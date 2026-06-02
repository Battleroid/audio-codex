using System.Diagnostics;
using System.IO;

namespace Tiger;

/// <summary>Wraps DeepFilterNet's `deep-filter` CLI for speech denoising (48 kHz WAV in/out).</summary>
public sealed class DeepFilter
{
    private readonly string _exe;
    public DeepFilter(string exePath) => _exe = exePath;
    public bool Available => File.Exists(_exe);

    /// <summary>Denoise a WAV; returns the denoised file path (same name under <paramref name="outDir"/>) or null.</summary>
    public string? Denoise(string wavPath, string outDir)
    {
        if (!Available || !File.Exists(wavPath)) return null;
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
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            string outp = Path.Combine(outDir, Path.GetFileName(wavPath));
            return p.ExitCode == 0 && File.Exists(outp) ? outp : null;
        }
        catch { return null; }
    }
}
