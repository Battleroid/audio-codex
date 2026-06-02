using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Tiger;

/// <summary>FNV-1 hashing + optional wordlist reversal (Tiger StringHash and Wwise IDs).</summary>
public sealed class Names
{
    private readonly ConcurrentDictionary<uint, string> _wordlist = new();
    public int WordlistCount => _wordlist.Count;

    /// <summary>FNV-1 32-bit (multiply then xor), as used by Tiger StringHash and Wwise.</summary>
    public static uint Fnv(string s)
    {
        uint value = 0x811c9dc5;
        foreach (char c in s) { value *= 0x01000193; value ^= (byte)c; }
        return value;
    }

    public static uint FnvLower(string s) => Fnv(s.ToLowerInvariant());

    public void LoadWordlist(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            _wordlist.TryAdd(Fnv(t), t);
            _wordlist.TryAdd(FnvLower(t), t);
        }
    }

    public bool TryResolve(uint hash, out string name) => _wordlist.TryGetValue(hash, out name!);

    public void Reset() => _wordlist.Clear();
}
