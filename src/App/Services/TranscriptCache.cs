using System;
using System.Collections.Concurrent;
using System.IO;
using Tiger;

namespace MarathonAudio.App.Services;

public sealed class CachedTranscript
{
    public string Text = "";        // displayed text (corpus-corrected when matched, else raw)
    public string RawText = "";     // original whisper output
    public bool Corrected;          // Text was snapped to a canonical game line
    public double MatchScore;       // similarity of the corpus match (0..1)
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
    private const int Version = 2;

    private readonly string _path;
    private readonly ConcurrentDictionary<string, CachedTranscript> _map = new();
    private volatile bool _dirty;
    private readonly object _ioLock = new();

    public TranscriptCache(string path) { _path = path; }
    public int Count => _map.Count;

    public bool TryGet(string key, out CachedTranscript meta) => _map.TryGetValue(key, out meta!);
    public System.Collections.Generic.IReadOnlyCollection<System.Collections.Generic.KeyValuePair<string, CachedTranscript>> Entries
        => (System.Collections.Generic.IReadOnlyCollection<System.Collections.Generic.KeyValuePair<string, CachedTranscript>>)_map;

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
            if (r.ReadUInt32() != Magic) return;
            int version = r.ReadInt32();
            if (version != 1 && version != 2) return;   // unknown -> rebuild
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                var t = new CachedTranscript { Text = r.ReadString() };
                if (version >= 2)
                {
                    t.RawText = r.ReadString();
                    t.Corrected = r.ReadBoolean();
                    t.MatchScore = r.ReadDouble();
                }
                else t.RawText = t.Text;                 // v1 migration
                t.Language = r.ReadString();
                t.NoSpeech = r.ReadBoolean();
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
                        w.Write(kv.Value.RawText);
                        w.Write(kv.Value.Corrected);
                        w.Write(kv.Value.MatchScore);
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
