using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rect = System.Windows.Rect;

namespace SearchAnything;

/// <summary>One hit. Usually one box, but a match that wraps onto the next line has one box per line.</summary>
internal sealed record TextMatch(IReadOnlyList<Rect> Boxes, string LineText)
{
    public Rect Bounds
    {
        get
        {
            var bounds = Rect.Empty;
            foreach (var box in Boxes)
                bounds.Union(box);
            return bounds;
        }
    }
}

/// <summary>
/// Case- and accent-insensitive substring search over OCR lines, returning the on-screen boxes of each hit.
/// </summary>
internal sealed class SearchIndex
{
    /// <param name="Start">Where this line begins inside the combined, folded text of all lines.</param>
    private sealed record Line(string Text, int Start, int[] WordStarts, IReadOnlyList<OcrWordBox> Words)
    {
        public int End => Start + Text.Length;
    }

    private readonly List<Line> _lines = new();

    /// <summary>All lines, folded and joined with single spaces, so a match can run from one line into the next.</summary>
    private readonly string _folded;

    /// <summary>
    /// The text prepared for number searches (see <see cref="ForNumbers"/>), plus where each of its
    /// characters sits in <see cref="_folded"/>.
    /// </summary>
    private readonly string _numbers;
    private readonly int[] _numbersMap;

    public int WordCount { get; }
    public string AllText { get; }

    public SearchIndex(IReadOnlyList<OcrLineBox> lines)
    {
        var all = new StringBuilder();
        foreach (var line in lines)
        {
            if (all.Length > 0)
                all.Append(' ');

            var starts = new int[line.Words.Count];
            var text = new StringBuilder();
            for (int i = 0; i < line.Words.Count; i++)
            {
                if (i > 0) text.Append(' ');
                starts[i] = text.Length;
                text.Append(line.Words[i].Text);
            }
            _lines.Add(new Line(text.ToString(), all.Length, starts, line.Words));
            all.Append(text);
        }
        _folded = Fold(all.ToString());
        (_numbers, _numbersMap) = ForNumbers(all.ToString(), _folded);
        WordCount = lines.Sum(l => l.Words.Count);
        AllText = string.Join(Environment.NewLine, _lines.Select(l => l.Text));
    }

    public List<TextMatch> Find(string query)
    {
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var folded = Fold(normalized);
        var matches = new List<TextMatch>();
        if (folded.Length == 0)
            return matches;

        // When the user is clearly looking for a number, search the number-friendly version of the text.
        bool number = folded.Any(char.IsDigit);
        var text = number ? _numbers : _folded;
        var needle = number ? ForNumbers(normalized, folded).Text : folded;

        int firstLine = 0;
        int found = 0;
        while ((found = text.IndexOf(needle, found, StringComparison.Ordinal)) >= 0)
        {
            // Translate back to positions in the full text, which the line and word offsets refer to.
            int start = number ? _numbersMap[found] : found;
            int end = number ? _numbersMap[found + needle.Length - 1] + 1 : found + needle.Length;
            found += needle.Length;

            // Matches are found left to right, so lines that end before this one can be skipped for good.
            while (_lines[firstLine].End <= start)
                firstLine++;

            var boxes = new List<Rect>();
            var texts = new List<string>();
            for (int i = firstLine; i < _lines.Count && _lines[i].Start < end; i++)
            {
                var line = _lines[i];
                var box = BoxFor(line, Math.Max(start, line.Start) - line.Start, Math.Min(end, line.End) - line.Start);
                if (box.IsEmpty)
                    continue;
                boxes.Add(box);
                texts.Add(line.Text);
            }
            if (boxes.Count > 0)
                matches.Add(new TextMatch(boxes, string.Join(" ", texts)));
        }

        return InReadingOrder(matches);
    }

    /// <summary>Top-to-bottom in coarse bands, then left-to-right.</summary>
    internal static List<TextMatch> InReadingOrder(IEnumerable<TextMatch> matches) =>
        matches
            .OrderBy(m => (int)(m.Bounds.Top / 12))
            .ThenBy(m => m.Bounds.Left)
            .ToList();

    /// <summary>
    /// Undoes two OCR habits with numbers: reading a zero as the letter O, and adding stray spaces
    /// ("$1, 240.50"). Spaces are dropped, and o becomes 0 inside number-like words only.
    /// Returns the result and, for each of its characters, the position it came from.
    /// </summary>
    /// <param name="original">The text before folding; needed to tell a capital O from a lowercase o.</param>
    /// <param name="folded">The same text after <see cref="Fold"/> (same length).</param>
    private static (string Text, int[] Map) ForNumbers(string original, string folded)
    {
        var words = new List<(int Start, int End)>();
        for (int i = 0; i < folded.Length; i++)
        {
            if (folded[i] == ' ')
                continue;
            int start = i;
            while (i < folded.Length && folded[i] != ' ')
                i++;
            words.Add((start, i));
        }

        var text = new StringBuilder(folded.Length);
        var map = new List<int>(folded.Length);
        for (int w = 0; w < words.Count; w++)
        {
            var (start, end) = words[w];
            bool numberLike = IsNumberLike(original, folded, words, w);
            for (int j = start; j < end; j++)
            {
                text.Append(numberLike && folded[j] == 'o' ? '0' : folded[j]);
                map.Add(j);
            }
        }
        return (text.ToString(), map.ToArray());
    }

    /// <summary>
    /// A word that could be a misread number: no letters other than o, and either a digit somewhere
    /// ("2O26", "1O0%"), or a lone o that is a capital O or sits next to a number or symbol
    /// (a counter like "o / 29"). Words like "TOPWORD", or the Spanish "o" in "uno o dos", don't count.
    /// </summary>
    private static bool IsNumberLike(string original, string folded, List<(int Start, int End)> words, int index)
    {
        var (start, end) = words[index];
        if (!OnlyDigitsSymbolsAndO(folded, start, end, out bool hasDigit))
            return false;
        if (hasDigit)
            return true;
        if (end - start != 1 || folded[start] != 'o')
            return false;
        if (original[start] == 'O')
            return true;

        bool NeighborIsNumeric(int i) =>
            i >= 0 && i < words.Count &&
            OnlyDigitsSymbolsAndO(folded, words[i].Start, words[i].End, out bool digit) &&
            (digit || folded.IndexOf('o', words[i].Start, words[i].End - words[i].Start) < 0);
        return NeighborIsNumeric(index - 1) || NeighborIsNumeric(index + 1);
    }

    private static bool OnlyDigitsSymbolsAndO(string folded, int start, int end, out bool hasDigit)
    {
        hasDigit = false;
        for (int j = start; j < end; j++)
        {
            char c = folded[j];
            if (char.IsDigit(c))
                hasDigit = true;
            else if (char.IsLetter(c) && c != 'o')
                return false;
        }
        return true;
    }

    /// <summary>Union of the word boxes covering [start, end) of one line, trimmed to the matched characters.</summary>
    private static Rect BoxFor(Line line, int start, int end)
    {
        Rect box = Rect.Empty;
        for (int i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            int wordStart = line.WordStarts[i];
            int wordEnd = wordStart + word.Text.Length;
            if (wordEnd <= start || wordStart >= end || word.Text.Length == 0)
                continue;

            // Estimate the sub-word position assuming roughly even character widths.
            double from = (double)(Math.Max(start, wordStart) - wordStart) / word.Text.Length;
            double to = (double)(Math.Min(end, wordEnd) - wordStart) / word.Text.Length;
            var part = new Rect(
                word.Box.X + word.Box.Width * from,
                word.Box.Y,
                word.Box.Width * (to - from),
                word.Box.Height);
            box.Union(part);
        }
        return box;
    }

    /// <summary>Lowercases and strips accents one character at a time, so indexes stay aligned.</summary>
    internal static string Fold(string s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // Half of an emoji (a surrogate) can't be normalized on its own, and has no accents anyway.
            if (c >= 128 && !char.IsSurrogate(c))
                c = c.ToString().Normalize(NormalizationForm.FormD)[0];
            chars[i] = char.ToLowerInvariant(c);
        }
        return new string(chars);
    }
}
