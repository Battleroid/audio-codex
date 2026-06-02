using System;
using NAudio.Wave;

namespace MarathonAudio.App.Services;

/// <summary>Downmixes any channel count to stereo (so 4/7ch sounds play on WaveOut).</summary>
internal sealed class DownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _srcCh;
    private float[] _temp = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }

    public DownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _srcCh = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / 2;
        int srcNeeded = framesRequested * _srcCh;
        if (_temp.Length < srcNeeded) _temp = new float[srcNeeded];
        int srcRead = _source.Read(_temp, 0, srcNeeded);
        int framesRead = srcRead / _srcCh;
        int o = offset;
        for (int f = 0; f < framesRead; f++)
        {
            int b = f * _srcCh;
            float l, r;
            if (_srcCh == 1) { l = r = _temp[b]; }
            else if (_srcCh == 2) { l = _temp[b]; r = _temp[b + 1]; }
            else
            {
                float ls = 0, rs = 0; int lc = 0, rc = 0;
                for (int c = 0; c < _srcCh; c++)
                {
                    if ((c & 1) == 0) { ls += _temp[b + c]; lc++; } else { rs += _temp[b + c]; rc++; }
                }
                l = lc > 0 ? ls / lc : 0; r = rc > 0 ? rs / rc : 0;
            }
            buffer[o++] = l; buffer[o++] = r;
        }
        return framesRead * 2;
    }
}

/// <summary>WAV playback (NAudio/WaveOut) + waveform peak extraction.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public bool HasFile => _reader != null && _out != null;
    public bool HasOutputDevice { get; private set; } = true;

    private float _volume = 0.6f;
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); if (_reader != null) _reader.Volume = _volume; }
    }

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (_reader != null) _reader.CurrentTime = value; }
    }
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public float[] LoadFile(string wavPath, int columns = 1200)
    {
        Stop();
        _reader?.Dispose();
        _reader = new AudioFileReader(wavPath) { Volume = _volume };
        float[] peaks = ComputePeaks(_reader, columns);
        _reader.Position = 0;
        InitOutput();
        return peaks;
    }

    /// <summary>Load a WAV for playback only (waveform peaks already supplied from cache).</summary>
    public void LoadForPlayback(string wavPath)
    {
        Stop();
        _reader?.Dispose();
        _reader = new AudioFileReader(wavPath) { Volume = _volume };
        InitOutput();
    }

    private void InitOutput()
    {
        _out?.Dispose();
        _out = null;
        try
        {
            var outDev = new WaveOutEvent();
            outDev.PlaybackStopped += OnStopped;
            outDev.Init(new DownmixSampleProvider(_reader!).ToWaveProvider());
            _out = outDev;
            HasOutputDevice = true;
        }
        catch { HasOutputDevice = false; }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // Reset to start so the next Play restarts (fixes replay-after-completion).
        if (_reader != null) _reader.Position = 0;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public static float[] ComputePeaks(string wavPath, int columns)
    {
        using var r = new AudioFileReader(wavPath);
        return ComputePeaks(r, columns);
    }

    /// <summary>One-shot: peaks + duration for a WAV (used for list-row previews).</summary>
    public static (float[] peaks, TimeSpan duration) Analyze(string wavPath, int columns)
    {
        using var r = new AudioFileReader(wavPath);
        TimeSpan dur = r.TotalTime;
        return (ComputePeaks(r, columns), dur);
    }

    private static float[] ComputePeaks(AudioFileReader reader, int columns)
    {
        int channels = reader.WaveFormat.Channels;
        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        long frames = totalSamples / Math.Max(1, channels);
        long framesPerCol = Math.Max(1, frames / columns);

        var peaks = new float[columns];
        var buffer = new float[8192];
        int col = 0;
        long frameInCol = 0;
        float curMax = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0 && col < columns)
        {
            for (int i = 0; i < read; i += channels)
            {
                float v = 0;
                for (int c = 0; c < channels && i + c < read; c++)
                    v = Math.Max(v, Math.Abs(buffer[i + c]));
                if (v > curMax) curMax = v;
                if (++frameInCol >= framesPerCol)
                {
                    if (col < columns) peaks[col++] = curMax;
                    curMax = 0; frameInCol = 0;
                }
            }
        }
        while (col < columns) peaks[col++] = curMax;
        return peaks;
    }

    public void Play()
    {
        if (_out == null || _reader == null) return;
        if (_reader.Position >= _reader.Length) _reader.Position = 0;
        _out.Play();
    }
    public void Pause() => _out?.Pause();

    public void Stop()
    {
        if (_out != null) { _out.PlaybackStopped -= OnStopped; _out.Stop(); _out.PlaybackStopped += OnStopped; }
        if (_reader != null) _reader.Position = 0;
    }

    public void Dispose()
    {
        if (_out != null) _out.PlaybackStopped -= OnStopped;
        _out?.Dispose();
        _reader?.Dispose();
        _out = null; _reader = null;
    }
}
