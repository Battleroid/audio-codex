using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tiger;

public sealed class SoundEntry
{
    public uint TagHash { get; set; }
    public uint WwiseId { get; set; }          // Wwise source id (entry.reference)
    public ushort PkgId { get; set; }
    public int Index { get; set; }
    public uint Size { get; set; }
    public string PackageName { get; set; } = "";
    public TigerPackage Package { get; set; } = null!;

    // Soundbank linkage (filled by BuildSoundbanks)
    public uint SoundbankTag { get; set; }     // 0 if not referenced by any bank
    public string? SoundbankName { get; set; }

    // Resolved lazily
    public string? Name { get; set; }          // wordlist-resolved or null
    public WemInfo.Info? Header { get; set; }  // RIFF metadata

    public string DisplayName => Name ?? $"{TagHash:X8}";
    public string TagId => $"{TagHash:X8}";
}

public sealed class SoundbankInfo
{
    public uint Tag { get; set; }
    public string? Name { get; set; }
    public int Count { get; set; }
    public string Display => Name ?? $"Bank {Tag:X8}";
}

/// <summary>Indexes Marathon packages and exposes the sound (WEM) catalogue.</summary>
public sealed class PackageManager
{
    public string PackagesDir { get; }
    public List<SoundEntry> Sounds { get; } = new();
    public int PackageCount { get; private set; }
    public Names Names { get; } = new();

    private readonly Dictionary<ushort, TigerPackage> _byId = new();

    public PackageManager(string packagesDir, string oodleDll)
    {
        PackagesDir = packagesDir;
        Oodle.Initialize(oodleDll);
    }

    public void LoadWordlist(string path) => Names.LoadWordlist(path);

    /// <summary>Scan all packages and build the WEM catalogue. Reports progress 0..1.</summary>
    public void Index(Action<double, string>? progress = null)
    {
        string[] files = Directory.GetFiles(PackagesDir, "*.pkg");
        // Group by pkg_id, keep the package whose header has the largest entry table.
        var best = new Dictionary<ushort, (TigerPackage pkg, int count)>();
        int done = 0;
        foreach (string f in files)
        {
            done++;
            TigerPackage pkg;
            try { pkg = new TigerPackage(f); }
            catch { continue; }
            progress?.Invoke(done / (double)files.Length * 0.8, $"Reading {Path.GetFileName(f)}");
            if (!best.TryGetValue(pkg.PkgId, out var cur) || pkg.Entries.Count > cur.count)
                best[pkg.PkgId] = (pkg, pkg.Entries.Count);
        }

        _byId.Clear();
        Sounds.Clear();
        int gi = 0;
        foreach (var (pkgId, (pkg, _)) in best)
        {
            gi++;
            progress?.Invoke(0.8 + gi / (double)best.Count * 0.2, $"Indexing {pkg.Name}");
            _byId[pkgId] = pkg;
            foreach (Entry e in pkg.Entries)
            {
                if (!e.IsWem) continue;
                Sounds.Add(new SoundEntry
                {
                    TagHash = pkg.TagHash(e.Index),
                    WwiseId = e.Reference,
                    PkgId = pkgId,
                    Index = e.Index,
                    Size = e.FileSize,
                    PackageName = pkg.Name,
                    Package = pkg,
                });
            }
        }
        PackageCount = best.Count;
        ResolveNames();
        progress?.Invoke(1.0, $"{Sounds.Count} sounds in {PackageCount} packages");
    }

    private void ResolveNames()
    {
        if (Names.WordlistCount == 0) return;
        foreach (SoundEntry s in Sounds)
        {
            // Wwise source id reversal via wordlist (this is how MIDA labels sounds).
            if (Names.TryResolve(s.WwiseId, out string n)) s.Name = n;
        }
    }

    /// <summary>Reload the wordlist and re-resolve sound + soundbank names in place.</summary>
    public void ApplyWordlist(string path)
    {
        Names.Reset();
        Names.LoadWordlist(path);
        foreach (SoundEntry s in Sounds)
            s.Name = Names.TryResolve(s.WwiseId, out string n) ? n : null;
        foreach (SoundbankInfo b in Soundbanks)
            b.Name = Names.TryResolve(b.Tag, out string bn) ? bn : null;
        var nameByTag = Soundbanks.Where(b => b.Name != null).ToDictionary(b => b.Tag, b => b.Name!);
        foreach (SoundEntry s in Sounds)
            s.SoundbankName = s.SoundbankTag != 0 && nameByTag.TryGetValue(s.SoundbankTag, out var bn) ? bn : null;
    }

    public List<SoundbankInfo> Soundbanks { get; } = new();
    public bool SoundbanksBuilt { get; private set; }

    /// <summary>Parse every Wwise soundbank (type 26/6), link the WEMs it references,
    /// and group sounds by their owning soundbank. Heavy — run off the UI thread.</summary>
    public void BuildSoundbanks(Action<double, string>? progress = null)
    {
        if (SoundbanksBuilt) return;

        // Map Wwise source id -> the sounds that carry it.
        var byWwise = new Dictionary<uint, List<SoundEntry>>();
        foreach (SoundEntry s in Sounds)
        {
            if (!byWwise.TryGetValue(s.WwiseId, out var list)) byWwise[s.WwiseId] = list = new();
            list.Add(s);
        }
        var wanted = new HashSet<uint>(byWwise.Keys);

        // Collect all bank entries across packages.
        var banks = new List<(TigerPackage pkg, Entry e)>();
        foreach (var pkg in _byId.Values)
            foreach (Entry e in pkg.Entries)
                if (e.IsSoundbank) banks.Add((pkg, e));

        var bankCounts = new Dictionary<uint, int>();
        int done = 0;
        foreach (var (pkg, e) in banks)
        {
            done++;
            if (done % 200 == 0)
                progress?.Invoke(done / (double)banks.Count, $"Scanning soundbanks {done}/{banks.Count}");
            byte[] data;
            try { data = pkg.ReadEntry(e.Index); } catch { continue; }
            uint bankTag = pkg.TagHash(e.Index);
            foreach (uint srcId in ScanHircSourceIds(data, wanted))
            {
                if (!byWwise.TryGetValue(srcId, out var list)) continue;
                foreach (SoundEntry s in list)
                {
                    if (s.SoundbankTag != 0) continue;   // first bank wins
                    s.SoundbankTag = bankTag;
                    bankCounts[bankTag] = bankCounts.GetValueOrDefault(bankTag) + 1;
                }
            }
        }

        Soundbanks.Clear();
        foreach (var kv in bankCounts.OrderByDescending(k => k.Value))
        {
            Names.TryResolve(kv.Key, out string? bn);
            Soundbanks.Add(new SoundbankInfo { Tag = kv.Key, Count = kv.Value, Name = bn });
        }
        // propagate names onto entries
        var nameByTag = Soundbanks.Where(b => b.Name != null).ToDictionary(b => b.Tag, b => b.Name!);
        foreach (SoundEntry s in Sounds)
            if (s.SoundbankTag != 0 && nameByTag.TryGetValue(s.SoundbankTag, out var bn)) s.SoundbankName = bn;

        SoundbanksBuilt = true;
        progress?.Invoke(1.0, $"{Soundbanks.Count} soundbanks");
    }

    /// <summary>Brute-scan a bank's HIRC chunk for 32-bit Wwise ids present in <paramref name="wanted"/>.</summary>
    private static IEnumerable<uint> ScanHircSourceIds(byte[] data, HashSet<uint> wanted)
    {
        var found = new HashSet<uint>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            uint sz = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            bool isHirc = data[pos] == 'H' && data[pos + 1] == 'I' && data[pos + 2] == 'R' && data[pos + 3] == 'C';
            int body = pos + 8;
            if (isHirc)
            {
                int end = Math.Min(body + (int)sz, data.Length - 3);
                for (int o = body; o < end; o++)
                {
                    uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
                    if (wanted.Contains(v)) found.Add(v);
                }
            }
            if (sz == 0 || sz > (uint)data.Length) break;
            pos = body + (int)sz;
        }
        return found;
    }

    public byte[] ReadWem(SoundEntry s) => s.Package.ReadEntry(s.Index);

    /// <summary>Read the WEM and fill its RIFF header metadata (cached).</summary>
    public WemInfo.Info LoadHeader(SoundEntry s)
    {
        if (s.Header != null) return s.Header;
        byte[] data = ReadWem(s);
        s.Header = WemInfo.Parse(data);
        return s.Header;
    }

    public string ExtractWemToTemp(SoundEntry s, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        string p = Path.Combine(tempDir, $"{s.TagId}.wem");
        File.WriteAllBytes(p, ReadWem(s));
        return p;
    }
}
