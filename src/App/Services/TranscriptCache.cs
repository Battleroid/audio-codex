using System;
using System.Collections.Concurrent;
using System.IO;
using Tiger;

namespace MarathonAudio.App.Services;

public sealed class CachedTranscript
{
    public string Text = "";
    public string Language = "";
    public bool NoSpeech;
    public TranscriptSegment[] Segments = Array.Empty<TranscriptSegment>();
}

/// <summary>Disk-persistent cache of per-sound transcripts, keyed by tag id + size.
/// Mirrors <see cref="MetaCache"/>: lets transcription runs resume and avoids ever
/// re-transcribing a sound (including ones found to contain no speech).</summary>
public sealed class TranscriptCache
{
    private const uint Magic = 0x4341_4354; // "TCAC"
    private const int Version = 1;

    private readonly string _path;
    private readonly ConcurrentDictionary<string, CachedTranscript> _map = new();
    private volatile bool _dirty;
    private readonly object _ioLock = new();

    public TranscriptCache(string path) { _path = path; }
    public int Count => _map.Count;

    public bool TryGet(string key, out CachedTranscript meta) => _map.TryGetValue(key, out meta!);

    public void Set(string key, CachedTranscript t)
    {
        _map[key] = t;
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
                var t = new CachedTranscript
                {
                    Text = r.ReadString(),
                    Language = r.ReadString(),
                    NoSpeech = r.ReadBoolean(),
                };
                int n = r.ReadInt32();
                var segs = new TranscriptSegment[n];
                for (int j = 0; j < n; j++)
                    segs[j] = new TranscriptSegment { From = r.ReadDouble(), To = r.ReadDouble(), Text = r.ReadString() };
                t.Segments = segs;
                _map[key] = t;
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
            // Clear the flag *before* snapshotting so a concurrent Set() that lands during the
            // write re-marks the cache dirty and is captured by the next flush (rather than
            // having its dirty marker cleared out from under it and being lost).
            _dirty = false;
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
                        w.Write(kv.Value.Text);
                        w.Write(kv.Value.Language);
                        w.Write(kv.Value.NoSpeech);
                        w.Write(kv.Value.Segments.Length);
                        foreach (var s in kv.Value.Segments)
                        {
                            w.Write(s.From);
                            w.Write(s.To);
                            w.Write(s.Text);
                        }
                    }
                }
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch { _dirty = true; /* write failed — retry on the next flush */ }
        }
    }
}
