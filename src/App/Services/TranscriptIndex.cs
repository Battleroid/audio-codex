using System;
using System.Collections.Generic;
using System.Linq;
using Tiger;

namespace MarathonAudio.App.Services;

/// <summary>In-memory inverted index over transcripts. Rebuilt from <see cref="TranscriptCache"/>
/// (the single source of truth) — never persisted separately. Supports raw substring matching,
/// phrase matching, and fuzzy "similar word" lookup over the transcript vocabulary.</summary>
public sealed class TranscriptIndex
{
    private SoundEntry[] _sounds = Array.Empty<SoundEntry>();
    private string[] _texts = Array.Empty<string>();                 // lowercased transcripts, aligned with _sounds
    private Dictionary<string, List<int>> _postings = new();         // token -> indices into _sounds

    public int Count => _sounds.Length;

    public void Build(IReadOnlyList<(SoundEntry sound, string text)> items)
    {
        var sounds = new SoundEntry[items.Count];
        var texts = new string[items.Count];
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < items.Count; i++)
        {
            sounds[i] = items[i].sound;
            string low = items[i].text.ToLowerInvariant();
            texts[i] = low;
            foreach (string tok in Tokenize(low))
            {
                if (!postings.TryGetValue(tok, out var list)) postings[tok] = list = new List<int>();
                if (list.Count == 0 || list[^1] != i) list.Add(i);   // de-dup adjacent
            }
        }

        _sounds = sounds; _texts = texts; _postings = postings;
    }

    /// <summary>Sounds whose transcript contains <paramref name="query"/> as a substring.</summary>
    public IEnumerable<SoundEntry> Substring(string query)
    {
        string q = query.ToLowerInvariant();
        for (int i = 0; i < _texts.Length; i++)
            if (_texts[i].Contains(q, StringComparison.Ordinal)) yield return _sounds[i];
    }

    /// <summary>Sounds containing every query token, confirmed by an ordered substring match.</summary>
    public IEnumerable<SoundEntry> Phrase(string query)
    {
        string q = query.ToLowerInvariant().Trim();
        var tokens = Tokenize(q).ToList();
        if (tokens.Count == 0) yield break;

        // Narrow to candidates present in all token posting lists, then confirm the raw phrase.
        IEnumerable<int>? candidates = null;
        foreach (string tok in tokens)
        {
            if (!_postings.TryGetValue(tok, out var list)) yield break;   // a token nobody has
            candidates = candidates == null ? list : candidates.Intersect(list);
        }
        foreach (int i in candidates ?? Enumerable.Empty<int>())
            if (_texts[i].Contains(q, StringComparison.Ordinal)) yield return _sounds[i];
    }

    /// <summary>Sounds whose transcript contains a word similar to any query token
    /// (edit distance ≤ 2 over the vocabulary). Powers "find similar words/phrases".</summary>
    public IEnumerable<SoundEntry> SimilarWords(string query)
    {
        var qTokens = Tokenize(query.ToLowerInvariant()).ToList();
        if (qTokens.Count == 0) return Enumerable.Empty<SoundEntry>();

        var hits = new HashSet<int>();
        foreach (string vocab in _postings.Keys)
        {
            foreach (string qt in qTokens)
            {
                int budget = qt.Length <= 4 ? 1 : 2;
                if (vocab.StartsWith(qt, StringComparison.Ordinal) || EditWithin(vocab, qt, budget))
                {
                    foreach (int i in _postings[vocab]) hits.Add(i);
                    break;
                }
            }
        }
        return hits.Select(i => _sounds[i]);
    }

    private static IEnumerable<string> Tokenize(string lower)
    {
        int n = lower.Length, start = -1;
        for (int i = 0; i < n; i++)
        {
            bool alnum = char.IsLetterOrDigit(lower[i]);
            if (alnum && start < 0) start = i;
            else if (!alnum && start >= 0) { yield return lower.Substring(start, i - start); start = -1; }
        }
        if (start >= 0) yield return lower.Substring(start);
    }

    /// <summary>Bounded Levenshtein: true if edit distance(a, b) ≤ max, with an early length cutoff.</summary>
    private static bool EditWithin(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return false;
        int la = a.Length, lb = b.Length;
        var prev = new int[lb + 1];
        var cur = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;
        for (int i = 1; i <= la; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return false;   // whole row already exceeds the budget
            (prev, cur) = (cur, prev);
        }
        return prev[lb] <= max;
    }
}
