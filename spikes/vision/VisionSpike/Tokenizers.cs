using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionSpike;

/// <summary>Byte-pair merging shared by both tokenizers: repeatedly joins the adjacent pair with the lowest rank.</summary>
internal static class Bpe
{
    public static List<string> Merge(List<string> symbols, Dictionary<(string, string), int> ranks)
    {
        while (symbols.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < symbols.Count - 1; i++)
                if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out int rank) && rank < bestRank)
                    (best, bestRank) = (i, rank);
            if (best < 0)
                break;
            // Merge every occurrence of the winning pair, left to right, like the reference implementations.
            var (a, b) = (symbols[best], symbols[best + 1]);
            var merged = new List<string>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == a && symbols[i + 1] == b)
                {
                    merged.Add(a + b);
                    i++;
                }
                else
                    merged.Add(symbols[i]);
            }
            symbols = merged;
        }
        return symbols;
    }
}

/// <summary>OpenAI CLIP tokenizer (used by OWL-ViT): lowercase, byte-level BPE with an end-of-word marker.</summary>
internal sealed class ClipTokenizer
{
    private const int StartOfText = 49406, EndOfText = 49407;
    private static readonly Regex Words = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly char[] _byteToChar = BytesToUnicode();
    private readonly int _padId;

    public ClipTokenizer(string vocabJson, string mergesTxt, int padId)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJson))!;
        var lines = File.ReadAllLines(mergesTxt).Skip(1).Take(49152 - 256 - 2).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split(' ');
            if (parts.Length == 2)
                _ranks[(parts[0], parts[1])] = i;
        }
        _padId = padId;
    }

    public (long[] Ids, long[] Mask) Encode(string text, int length)
    {
        var ids = new List<long> { StartOfText };
        text = Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();
        foreach (Match m in Words.Matches(text))
        {
            var chars = Encoding.UTF8.GetBytes(m.Value).Select(b => _byteToChar[b].ToString()).ToList();
            chars[^1] += "</w>";
            foreach (var piece in Bpe.Merge(chars, _ranks))
                ids.Add(_vocab[piece]);
        }
        if (ids.Count > length - 1)
            ids.RemoveRange(length - 1, ids.Count - (length - 1));
        ids.Add(EndOfText);
        var mask = new long[length];
        var padded = new long[length];
        for (int i = 0; i < length; i++)
        {
            padded[i] = i < ids.Count ? ids[i] : _padId;
            mask[i] = i < ids.Count ? 1 : 0;
        }
        return (padded, mask);
    }

    /// <summary>GPT-2's reversible byte to printable-character table.</summary>
    private static char[] BytesToUnicode()
    {
        var map = new char[256];
        var direct = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToHashSet();
        int next = 0;
        for (int b = 0; b < 256; b++)
            map[b] = direct.Contains(b) ? (char)b : (char)(256 + next++);
        return map;
    }
}

/// <summary>
/// Gemma SentencePiece-style BPE from tokenizer.json (used by SigLIP 2): spaces become "▁",
/// no leading marker, unknown characters fall back to byte tokens, then &lt;eos&gt; and padding.
/// </summary>
internal sealed class GemmaTokenizer
{
    private const int Pad = 0, Eos = 1, Unk = 3;
    private readonly Dictionary<string, int> _vocab = new();
    private readonly Dictionary<(string, string), int> _ranks = new();

    public GemmaTokenizer(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tokenizerJson));
        var model = doc.RootElement.GetProperty("model");
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
            _vocab[entry.Name] = entry.Value.GetInt32();
        int rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            var pair = merge.ValueKind == JsonValueKind.Array
                ? (merge[0].GetString()!, merge[1].GetString()!)
                : SplitPair(merge.GetString()!);
            _ranks.TryAdd(pair, rank++);
        }
    }

    private static (string, string) SplitPair(string s)
    {
        int space = s.IndexOf(' ', 1);
        return (s[..space], s[(space + 1)..]);
    }

    /// <summary>SigLIP 2 was trained on lowercased text padded to 64 tokens.</summary>
    public long[] Encode(string text, int length = 64)
    {
        var normalized = text.ToLowerInvariant().Replace(' ', '▁');
        var symbols = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
            symbols.Add((string)enumerator.Current);

        var ids = new List<long>();
        foreach (var piece in Bpe.Merge(symbols, _ranks))
        {
            if (_vocab.TryGetValue(piece, out int id))
                ids.Add(id);
            else
                foreach (var b in Encoding.UTF8.GetBytes(piece))
                    ids.Add(_vocab.TryGetValue($"<0x{b:X2}>", out int byteId) ? byteId : Unk);
        }
        if (ids.Count > length - 1)
            ids.RemoveRange(length - 1, ids.Count - (length - 1));
        ids.Add(Eos);
        var padded = new long[length];
        for (int i = 0; i < length; i++)
            padded[i] = i < ids.Count ? ids[i] : Pad;
        return padded;
    }
}
