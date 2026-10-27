using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SearchAnything;

namespace VisionSpike;

/// <summary>
/// Visual search over one screenshot, the way the app would use it: <see cref="PrepareAsync"/> once when the
/// screen is captured, then <see cref="Find"/> on every keystroke.
/// </summary>
internal interface IVisualSearch : IDisposable
{
    /// <summary>The default score a highlight needs, picked on the test set (see README).</summary>
    float Threshold { get; }

    /// <summary>The areas looked at on the prepared screen: candidate regions, or detector tiles.</summary>
    IReadOnlyList<Rectangle> Regions { get; }

    Task PrepareAsync(CapturedScreen screen);

    /// <summary>For each query, every scored box on the prepared screen, best first, without near-duplicates.</summary>
    List<Detection>[] Find(IReadOnlyList<string> queries);
}

/// <summary>
/// SigLIP 2 over candidate regions: OCR to know where text is, the region finder, then one image embedding per
/// region. A query costs one text embedding (cached) and a dot product per region.
/// </summary>
internal sealed class SiglipSearch : IVisualSearch
{
    private readonly Siglip _model;
    private readonly OcrService _ocr = OcrService.CreateAll()[0];
    private readonly string _template;
    private readonly int _maxRegions;
    private readonly bool _splitGrids;
    private readonly Dictionary<string, float[]> _text = new();
    private float[][] _embeddings = Array.Empty<float[]>();

    public float Threshold { get; }
    public IReadOnlyList<Rectangle> Regions { get; private set; } = Array.Empty<Rectangle>();

    public SiglipSearch(Siglip model, float threshold, string template, int maxRegions, bool splitGrids)
    {
        (_model, Threshold, _template, _maxRegions, _splitGrids) = (model, threshold, template, maxRegions, splitGrids);
    }

    public async Task PrepareAsync(CapturedScreen screen)
    {
        var lines = await _ocr.RecognizeAsync(screen);
        var words = lines.SelectMany(l => l.Words)
            .Select(w => Rectangle.Round(new RectangleF((float)w.Box.X, (float)w.Box.Y, (float)w.Box.Width, (float)w.Box.Height)));
        var regions = Proposals.Find(screen.Pixels, screen.Width, screen.Height, words, _maxRegions, _splitGrids);
        _embeddings = _model.EncodeRegions(screen, regions);
        Regions = regions;
    }

    public List<Detection>[] Find(IReadOnlyList<string> queries) => queries.Select(q =>
    {
        if (!_text.TryGetValue(q, out var text))
            _text[q] = text = _model.EncodeText(Prompts.Fill(_template, q));
        var scored = Regions.Select((r, i) => new Detection(r, _model.Probability(_embeddings[i], text))).ToList();
        return Detections.Suppress(scored, 0.5);
    }).ToArray();

    public void Dispose() => _model.Dispose();
}

/// <summary>
/// OWL-ViT over square tiles of the screen. This ONNX export runs the image encoder together with the
/// queries, so all the work happens in <see cref="Find"/>, on every keystroke.
/// </summary>
internal sealed class OwlSearch : IVisualSearch
{
    private readonly OwlVit _model;
    private readonly string _template, _tiling;
    private CapturedScreen? _screen;

    public float Threshold { get; }
    public IReadOnlyList<Rectangle> Regions { get; private set; } = Array.Empty<Rectangle>();

    public OwlSearch(OwlVit model, float threshold, string template, string tiling)
    {
        (_model, Threshold, _template, _tiling) = (model, threshold, template, tiling);
    }

    public Task PrepareAsync(CapturedScreen screen)
    {
        _screen = screen;
        Regions = Tiles(screen.Width, screen.Height, _tiling);
        return Task.CompletedTask;
    }

    public List<Detection>[] Find(IReadOnlyList<string> queries)
    {
        var prompts = queries.Select(q => Prompts.Fill(_template, q)).ToList();
        var found = queries.Select(_ => new List<Detection>()).ToArray();
        foreach (var tile in Regions)
        {
            var perQuery = _model.Detect(_screen!, tile, prompts, minScore: 0.02f);
            for (int q = 0; q < queries.Count; q++)
                found[q].AddRange(perQuery[q]);
        }
        return found.Select(f => Detections.Suppress(f, 0.3)).ToArray();
    }

    /// <summary>The whole screen at once ("whole"), or overlapping squares of the given side.</summary>
    private static List<Rectangle> Tiles(int width, int height, string mode)
    {
        if (mode == "whole")
            return new List<Rectangle> { new(0, 0, width, height) };
        int side = Math.Min(int.Parse(mode, CultureInfo.InvariantCulture), Math.Min(width, height));
        int cols = (int)Math.Ceiling((width - side) / (side * 0.75)) + 1;
        int rows = (int)Math.Ceiling((height - side) / (side * 0.75)) + 1;
        var tiles = new List<Rectangle>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x = cols == 1 ? 0 : (int)Math.Round((double)c * (width - side) / (cols - 1));
                int y = rows == 1 ? 0 : (int)Math.Round((double)r * (height - side) / (rows - 1));
                tiles.Add(new Rectangle(x, y, side, side));
            }
        return tiles;
    }

    public void Dispose() => _model.Dispose();
}

internal static class Prompts
{
    /// <summary>Fills "{q}" with the query and "{a}" with "a" or "an".</summary>
    public static string Fill(string template, string query) =>
        template.Replace("{a}", "aeiou".Contains(query[0]) ? "an" : "a").Replace("{q}", query);
}

internal static class Detections
{
    /// <summary>Greedy suppression: drop a box if it overlaps a better one (by IoU, or mostly contains or sits inside it).</summary>
    public static List<Detection> Suppress(List<Detection> found, double overlap)
    {
        var kept = new List<Detection>();
        foreach (var d in found.OrderByDescending(d => d.Score))
            if (!kept.Any(k => Geometry.IoU(k.Box, d.Box) > overlap || Geometry.Contained(k.Box, d.Box) > 0.7))
                kept.Add(d);
        return kept;
    }
}
