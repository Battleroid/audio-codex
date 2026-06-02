using System;
using System.Collections.Concurrent;
using System.IO;

namespace MarathonAudio.App.Services;

public sealed class CachedMeta
{
    public ushort Codec;
    public int Channels;
    public int SampleRate;
    public double Duration;
    public float[] Peaks = Array.Empty<float>();
}

/// <summary>Disk-persistent cache of per-sound metadata + waveform peaks, keyed by tag id + size.
/// Avoids re-decoding sounds for previews/waveforms across sessions.</summary>
public sealed class MetaCache
{
    private const uint Magic = 0x4341_434D; // "MCAC"
    private const int Version = 1;

    private readonly string _path;
    private readonly ConcurrentDictionary<string, CachedMeta> _map = new();
    private volatile bool _dirty;
    private readonly object _ioLock = new();

    public MetaCache(string path) { _path = path; }
    public int Count => _map.Count;

    public bool TryGet(string key, out CachedMeta meta) => _map.TryGetValue(key, out meta!);

    public void Set(string key, CachedMeta meta)
    {
        _map[key] = meta;
        _dirty = true;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var fs = File.OpenRead(_path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return;
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                var m = new CachedMeta
                {
                    Codec = r.ReadUInt16(),
                    Channels = r.ReadInt32(),
                    SampleRate = r.ReadInt32(),
                    Duration = r.ReadDouble(),
                };
                int n = r.ReadInt32();
                var peaks = new float[n];
                for (int j = 0; j < n; j++) peaks[j] = r.ReadSingle();
                m.Peaks = peaks;
                _map[key] = m;
            }
        }
        catch { _map.Clear(); }
    }

    public void Flush()
    {
        if (!_dirty) return;
        lock (_ioLock)
        {
            if (!_dirty) return;
            try
            {
                var snapshot = System.Linq.Enumerable.ToArray(_map);
                string tmp = _path + ".tmp";
                using (var fs = File.Create(tmp))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(snapshot.Length);
                    foreach (var kv in snapshot)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value.Codec);
                        w.Write(kv.Value.Channels);
                        w.Write(kv.Value.SampleRate);
                        w.Write(kv.Value.Duration);
                        w.Write(kv.Value.Peaks.Length);
                        foreach (float p in kv.Value.Peaks) w.Write(p);
                    }
                }
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
                _dirty = false;
            }
            catch { /* best-effort cache */ }
        }
    }
}
