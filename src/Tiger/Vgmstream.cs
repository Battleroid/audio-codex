using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Tiger;

/// <summary>Wraps vgmstream-cli for decoding/metadata of Wwise WEM audio.</summary>
public sealed class Vgmstream
{
    private readonly string _exe;
    public Vgmstream(string exePath) => _exe = exePath;
    public bool Available => File.Exists(_exe);

    public sealed class Meta
    {
        public int SampleRate;
        public int Channels;
        public long TotalSamples;
        public double Seconds => SampleRate > 0 ? (double)TotalSamples / SampleRate : 0;
        public string Encoding = "";
    }

    /// <summary>Decode a WEM file to a WAV file. Returns true on success.</summary>
    public bool DecodeToWav(string wemPath, string wavPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(wemPath);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && File.Exists(wavPath);
    }

    /// <summary>Get metadata via vgmstream -m (no decode).</summary>
    public Meta? GetMetadata(string wemPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(wemPath);
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) return null;

        var meta = new Meta();
        foreach (Match mm in Regex.Matches(outp, @"sample rate:\s*(\d+)")) meta.SampleRate = int.Parse(mm.Groups[1].Value);
        foreach (Match mm in Regex.Matches(outp, @"channels:\s*(\d+)")) meta.Channels = int.Parse(mm.Groups[1].Value);
        var ts = Regex.Match(outp, @"stream total samples:\s*(\d+)");
        if (ts.Success) meta.TotalSamples = long.Parse(ts.Groups[1].Value);
        var enc = Regex.Match(outp, @"encoding:\s*(.+)");
        if (enc.Success) meta.Encoding = enc.Groups[1].Value.Trim();
        return meta;
    }
}
