using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tiger;

/// <summary>
/// A searchable corpus of the game's English localized-string text (tag class 0x8080b9ba).
/// The strings are stored concatenated with no separators, so instead of reverse-engineering
/// the part/combination tables we keep the whole token stream and, given a noisy ASR transcript,
/// find the contiguous span of real game text that best matches it ("snap to canonical line").
/// </summary>
public sealed class StringCorpus
{
    public const uint LocalizedStringsRef = 0x8080b9ba;

    private static readonly Regex Run = new(@"[\x20-\x7e]{6,}", RegexOptions.Compiled);
    private static readonly Regex Tok = new(@"[A-Za-z0-9][A-Za-z0-9']*", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(
        "the and you are this that with have your for not but was they will from what here out get i'm we is to of a it on".Split(' '));

    private readonly List<string> _words = new();   // original case (with "" sentinels between runs)
    private readonly List<string> _norm = new();    // lowercased
    private readonly Dictionary<string, List<int>> _index = new();
    private readonly Dictionary<string, int> _freq = new();

    public int WordCount => _words.Count;
    public bool Ready => _words.Count > 0;

    /// <summary>Add one localized-strings container's bytes. Only English containers contribute.</summary>
    public void AddContainer(byte[] data)
    {
        string text = Encoding.Latin1.GetString(data);
        var toks = new List<string>();
        foreach (Match run in Run.Matches(text))
            foreach (Match t in Tok.Matches(run.Value))
                toks.Add(t.Value);

        // English detection: needs a healthy fraction of common English stopwords.
        if (toks.Count < 20) return;
        int stop = toks.Count(w => Stop.Contains(w.ToLowerInvariant()));
        if (stop < 8 || stop / (double)toks.Count <= 0.04) return;

        foreach (Match run in Run.Matches(text))
        {
            foreach (Match t in Tok.Matches(run.Value))
            {
                _words.Add(t.Value);
                _norm.Add(t.Value.ToLowerInvariant());
            }
            _words.Add(""); _norm.Add("");   // sentinel gap between runs
        }
    }

    public void Finish()
    {
        _index.Clear(); _freq.Clear();
        for (int i = 0; i < _norm.Count; i++)
        {
            string n = _norm[i];
            if (n.Length == 0) continue;
            if (!_index.TryGetValue(n, out var list)) _index[n] = list = new List<int>();
            list.Add(i);
            _freq[n] = _freq.GetValueOrDefault(n) + 1;
        }
    }

    /// <summary>Snap a transcript to the nearest canonical span. Returns null if nothing matches well.</summary>
    public (string text, double score)? Correct(string transcript, double threshold = 0.62)
    {
        if (!Ready || string.IsNullOrWhiteSpace(transcript)) return null;
        var t = Tok.Matches(transcript).Select(m => m.Value.ToLowerInvariant()).ToArray();
        if (t.Length < 3) return null;

        // Anchor on the rarest transcript words present in the corpus to keep candidates few.
        var anchors = t.Select((w, i) => (w, i))
            .Where(x => _freq.TryGetValue(x.w, out int f) && f > 0 && f < 4000)
            .OrderBy(x => _freq[x.w]).Take(3).ToList();
        if (anchors.Count == 0) return null;

        var starts = new HashSet<int>();
        foreach (var (w, ai) in anchors)
            foreach (int pos in _index[w])
            {
                int s = pos - ai;
                if (s >= 0 && s + t.Length <= _words.Count) starts.Add(s);
                if (starts.Count > 8000) break;
            }

        double best = 0; int bestStart = -1, bestLen = 0;
        foreach (int s in starts)
            foreach (int len in new[] { t.Length, t.Length + 1 })
            {
                if (s + len > _words.Count) continue;
                double sc = Ratio(t, s, len);
                if (sc > best) { best = sc; bestStart = s; bestLen = len; }
            }

        if (bestStart < 0 || best < threshold) return null;

        // Trim sentinel gaps and edge words that don't appear in the transcript (kills bleed
        // into adjacent strings), then join in original case.
        var tset = new HashSet<string>(t);
        int a = bestStart, b = bestStart + bestLen - 1;
        while (a <= b && (_norm[a].Length == 0 || !tset.Contains(_norm[a]))) a++;
        while (b >= a && (_norm[b].Length == 0 || !tset.Contains(_norm[b]))) b--;
        var span = new List<string>();
        for (int i = a; i <= b; i++) if (_words[i].Length != 0) span.Add(_words[i]);
        string text = string.Join(" ", span).Trim();
        return text.Length == 0 ? null : (text, best);
    }

    /// <summary>Token-LCS F1 similarity between the transcript and a corpus window.</summary>
    private double Ratio(string[] t, int start, int len)
    {
        int n = t.Length;
        var dp = new int[len + 1];
        for (int i = 1; i <= n; i++)
        {
            int prev = 0;
            for (int j = 1; j <= len; j++)
            {
                int tmp = dp[j];
                string cw = _norm[start + j - 1];
                dp[j] = (cw.Length != 0 && t[i - 1] == cw) ? prev + 1 : Math.Max(dp[j], dp[j - 1]);
                prev = tmp;
            }
        }
        return 2.0 * dp[len] / (n + len);
    }
}
