using System.Collections.Generic;
using System.Windows;
using Xunit;

namespace SearchAnything.Tests;

public class CombinedIndexTests
{
    // Same fake layout as SearchIndexTests: 10px per character, 10px between words, 30px per row.
    private static List<OcrLineBox> Reading(params string[][] rows)
    {
        var lines = new List<OcrLineBox>();
        for (int row = 0; row < rows.Length; row++)
        {
            var boxes = new List<OcrWordBox>();
            double x = 0;
            foreach (var word in rows[row])
            {
                boxes.Add(new OcrWordBox(word, new Rect(x, row * 30, word.Length * 10, 20)));
                x += word.Length * 10 + 10;
            }
            lines.Add(new OcrLineBox(boxes));
        }
        return lines;
    }

    // What the two engines really produced for the same screen (see OcrService.CreateAll).
    private static CombinedIndex SpanishAndEnglish() => new(new[]
    {
        Reading(new[] { "Acción", "requerida" }, new[] { "Fecha:", "15/18/2826" }),
        Reading(new[] { "Acci6n", "requerida" }, new[] { "Fecha:", "15/10/2026" }),
    });

    [Fact]
    public void Finds_accents_from_one_engine_and_digits_from_the_other()
    {
        var index = SpanishAndEnglish();

        Assert.Single(index.Find("accion"));
        Assert.Single(index.Find("15/10/2026"));
    }

    [Fact]
    public void A_spot_both_engines_found_is_counted_once()
    {
        Assert.Single(SpanishAndEnglish().Find("requerida"));
    }

    [Fact]
    public void Separate_matches_next_to_each_other_are_kept()
    {
        var index = new CombinedIndex(new[] { Reading(new[] { "banana" }), Reading(new[] { "banana" }) });

        Assert.Equal(2, index.Find("an").Count);
    }

    [Fact]
    public void Word_count_and_text_come_from_the_first_reading()
    {
        var index = SpanishAndEnglish();

        Assert.Equal(4, index.WordCount);
        Assert.StartsWith("Acción", index.AllText);
    }

    [Fact]
    public void No_readings_finds_nothing()
    {
        var index = new CombinedIndex(new List<List<OcrLineBox>>());

        Assert.Empty(index.Find("anything"));
        Assert.Equal(0, index.WordCount);
    }
}
