using System.Collections.Generic;
using System.Windows;
using Xunit;

namespace SearchAnything.Tests;

public class SearchIndexTests
{
    // Fake OCR output: every character is 10px wide, words are 10px apart, rows are 30px apart.
    private static OcrLineBox Line(int row, params string[] words)
    {
        var boxes = new List<OcrWordBox>();
        double x = 0;
        foreach (var word in words)
        {
            boxes.Add(new OcrWordBox(word, new Rect(x, row * 30, word.Length * 10, 20)));
            x += word.Length * 10 + 10;
        }
        return new OcrLineBox(boxes);
    }

    private static SearchIndex Index(params OcrLineBox[] lines) => new(lines);

    [Fact]
    public void Ignores_case_and_accents()
    {
        var index = Index(Line(0, "Factura", "de", "compra", "-", "Acción", "requerida"));

        var match = Assert.Single(index.Find("ACCION"));
        Assert.Equal("Factura de compra - Acción requerida", match.LineText);
    }

    [Fact]
    public void Accented_query_matches_plain_text()
    {
        var index = Index(Line(0, "Configuracion"));

        Assert.Single(index.Find("configuración"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_query_finds_nothing(string query)
    {
        Assert.Empty(Index(Line(0, "hello")).Find(query));
    }

    [Fact]
    public void Emoji_does_not_break_search()
    {
        var index = Index(Line(0, "launch", "🚀", "today"));

        Assert.Single(index.Find("🚀"));
        Assert.Empty(index.Find("🔥"));
    }

    [Fact]
    public void Partial_word_highlights_only_the_matched_letters()
    {
        var index = Index(Line(0, "Factura"));

        var box = Assert.Single(index.Find("tur")).Bounds;
        Assert.Equal(30, box.X, 3);
        Assert.Equal(30, box.Width, 3);
    }

    [Fact]
    public void Multi_word_query_spans_both_words()
    {
        var index = Index(Line(0, "de", "compra"));

        var box = Assert.Single(index.Find("de compra")).Bounds;
        Assert.Equal(0, box.X, 3);
        Assert.Equal(90, box.Right, 3); // "de" 0-20, gap, "compra" 30-90
    }

    [Fact]
    public void Extra_spaces_in_the_query_are_ignored()
    {
        Assert.Single(Index(Line(0, "de", "compra")).Find("  de    compra "));
    }

    [Fact]
    public void Match_can_continue_onto_the_next_line()
    {
        var index = Index(Line(0, "hola"), Line(1, "mundo"));

        var match = Assert.Single(index.Find("hola mundo"));
        Assert.Equal(2, match.Boxes.Count);
        Assert.Equal("hola mundo", match.LineText);
    }

    [Fact]
    public void Finds_every_occurrence_inside_a_word()
    {
        Assert.Equal(2, Index(Line(0, "banana")).Find("an").Count);
    }

    [Fact]
    public void Results_come_in_reading_order()
    {
        // OCR returned the lower line first; results should still start at the top.
        var index = Index(Line(1, "foo", "below"), Line(0, "above", "foo"));

        var matches = index.Find("foo");
        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Bounds.Top, 3);
        Assert.Equal(30, matches[1].Bounds.Top, 3);
    }

    [Fact]
    public void Counts_words_and_exposes_all_text()
    {
        var index = Index(Line(0, "one", "two"), Line(1, "three"));

        Assert.Equal(3, index.WordCount);
        Assert.Equal("one two" + System.Environment.NewLine + "three", index.AllText);
    }

    [Fact]
    public void Number_query_also_matches_zero_misread_as_letter_O()
    {
        // Windows OCR read the counter "0 / 29" as "O / 29".
        var index = Index(Line(0, "Help", "O", "/", "29"));

        Assert.Single(index.Find("0 / 29"));
        Assert.Single(index.Find("0"));
    }

    [Fact]
    public void Zero_query_ignores_the_letter_o_inside_real_words()
    {
        // From a real screenshot: searching "0" used to light up every o on screen.
        var index = Index(Line(0, "TOPWORD", "notification", "o", "Ok"));

        Assert.Empty(index.Find("0"));
    }

    [Fact]
    public void Letter_O_inside_a_number_is_read_as_zero()
    {
        Assert.Single(Index(Line(0, "Fecha:", "15/1O/2O26")).Find("2026"));
    }

    [Fact]
    public void Number_query_ignores_stray_spaces_from_OCR()
    {
        // The English engine read "$1,240.50" as "$1, 240.50".
        var index = Index(Line(0, "total:", "$1,", "240.50"));

        var box = Assert.Single(index.Find("1,240.50")).Bounds;
        Assert.Equal(80, box.X, 3);       // starts at the "1" in "$1," (word at x=70, second character)
        Assert.Equal(170, box.Right, 3);  // ends with "240.50" (x=110..170)
    }

    [Fact]
    public void Lone_lowercase_o_next_to_a_number_is_read_as_zero()
    {
        // The playground counter "0 / 29" came back from OCR as "o / 29".
        Assert.Single(Index(Line(0, "Checklist", "o", "/", "29")).Find("0 / 29"));
    }

    [Fact]
    public void Spanish_word_o_between_words_stays_a_letter()
    {
        Assert.Empty(Index(Line(0, "uno", "o", "dos")).Find("0"));
    }

    [Fact]
    public void Letter_query_does_not_match_zeros()
    {
        Assert.Empty(Index(Line(0, "100%", "2026")).Find("o"));
    }

    [Fact]
    public void Empty_screen_finds_nothing()
    {
        Assert.Empty(Index().Find("anything"));
    }
}
