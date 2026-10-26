using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SearchAnything;

/// <summary>
/// Searches several OCR readings of the same screen (one per language engine) and merges the hits.
/// A spot found by more than one engine is reported once, using the first engine's version.
/// </summary>
internal sealed class CombinedIndex
{
    private readonly IReadOnlyList<SearchIndex> _indexes;

    public CombinedIndex(IEnumerable<IReadOnlyList<OcrLineBox>> readings) =>
        _indexes = readings.Select(r => new SearchIndex(r)).ToList();

    /// <summary>Taken from the first (preferred-language) reading.</summary>
    public int WordCount => _indexes.Count > 0 ? _indexes[0].WordCount : 0;

    /// <summary>Taken from the first (preferred-language) reading.</summary>
    public string AllText => _indexes.Count > 0 ? _indexes[0].AllText : "";

    public List<TextMatch> Find(string query)
    {
        var merged = new List<TextMatch>();
        foreach (var index in _indexes)
            foreach (var match in index.Find(query))
                if (!merged.Any(m => SameSpot(m.Bounds, match.Bounds)))
                    merged.Add(match);
        return SearchIndex.InReadingOrder(merged);
    }

    /// <summary>Two boxes are the same hit if they overlap by at least half of the smaller one.</summary>
    internal static bool SameSpot(Rect a, Rect b)
    {
        var overlap = Rect.Intersect(a, b);
        if (overlap.IsEmpty)
            return false;
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return overlap.Width * overlap.Height >= 0.5 * smaller;
    }
}
