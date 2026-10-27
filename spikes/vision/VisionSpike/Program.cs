using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SearchAnything;

namespace VisionSpike;

internal static class Program
{
    /// <summary>
    /// Every query runs on every screen. Most screens don't contain most of these, which is the point:
    /// a good model must also stay quiet. The last three never appear anywhere.
    /// </summary>
    private static readonly string[] Queries =
    {
        "dog", "cat", "horse", "bird", "elephant", "car", "bicycle", "airplane", "boat", "pizza", "cake",
        "coffee cup", "flower", "mountain", "beach", "laptop",
        "chart", "bar chart", "line chart", "pie chart", "table", "map", "calendar", "qr code", "source code",
        "giraffe", "guitar", "fire truck",
    };

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var opt = Options.Parse(args);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var models = Path.Combine(root, "models");
        var screens = Path.Combine(root, "data", "screens");
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(results);

        if (opt.Command == "tokens")
        {
            var gemma = new GemmaTokenizer(Path.Combine(models, "siglip2", "tokenizer.json"));
            var clip = new ClipTokenizer(Path.Combine(models, "owlvit", "vocab.json"), Path.Combine(models, "owlvit", "merges.txt"), 0);
            foreach (var text in opt.Rest)
            {
                Console.WriteLine($"gemma  {text}: {string.Join(" ", gemma.Encode(text).TakeWhile(i => i != 0))}");
                Console.WriteLine($"clip   {text}: {string.Join(" ", clip.Encode(text, 16).Ids)}");
            }
            return 0;
        }

        var scenes = Directory.GetFiles(screens, "scene*.png").OrderBy(f => f).Select(Scene.Load).ToList();
        if (opt.Scenes != null)
            scenes = scenes.Where(s => opt.Scenes.Contains(s.Index)).ToList();
        string runName = opt.RunName ?? $"{opt.Model}-{(opt.Gpu ? "gpu" : "cpu")}{(opt.Quantized ? "-int8" : "")}{(opt.Model == "owl" ? "-" + opt.Tiling : "")}";
        var log = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); log.AppendLine(line); }
        Say($"# {runName}");
        Say($"Machine: {Environment.ProcessorCount} logical CPUs; {scenes.Count} screens; {Queries.Length} queries.");

        var predictions = new List<Prediction>();
        var timings = new Timings();
        var load = Stopwatch.StartNew();

        if (opt.Model == "siglip")
        {
            using var model = new Siglip(models, opt.Variant == "b32" ? SiglipVariant.B32 : SiglipVariant.B16, opt.Gpu, opt.VisionFile ?? (opt.Quantized ? "vision_model_quantized.onnx" : "vision_model.onnx"), opt.FullText) { BatchSize = opt.Batch };
            timings.Load = load.Elapsed.TotalMilliseconds;
            if (opt.Command == "inspect") { Console.WriteLine(model.Describe()); return 0; }
            var ocr = OcrService.CreateAll()[0];

            var text = new Dictionary<string, float[]>();
            model.EncodeText(Prompt(Queries[0], opt.Template)); // warm-up
            foreach (var q in Queries)
            {
                var sw = Stopwatch.StartNew();
                text[q] = model.EncodeText(Prompt(q, opt.Template));
                timings.PerQuery.Add(sw.Elapsed.TotalMilliseconds);
            }

            bool warm = false;
            foreach (var scene in scenes)
            {
                var sw = Stopwatch.StartNew();
                var lines = await ocr.RecognizeAsync(scene.Capture);
                var words = lines.SelectMany(l => l.Words).Select(w => Rectangle.Round(new RectangleF((float)w.Box.X, (float)w.Box.Y, (float)w.Box.Width, (float)w.Box.Height)));
                var regions = Proposals.Find(scene.Capture.Pixels, scene.Capture.Width, scene.Capture.Height, words, opt.MaxRegions, opt.SplitGrids);
                double proposalMs = sw.Elapsed.TotalMilliseconds;
                if (!warm) { model.EncodeRegions(scene.Capture, regions.Take(opt.Batch).ToList()); warm = true; }
                sw.Restart();
                var embeddings = model.EncodeRegions(scene.Capture, regions);
                timings.PerScreen.Add(sw.Elapsed.TotalMilliseconds);
                timings.Preprocess.Add(model.LastPreprocessMs);
                timings.Proposals.Add(proposalMs);
                timings.Regions.Add(regions.Count);
                foreach (var q in Queries)
                {
                    var found = regions.Select((r, i) => new Detection(r, model.Probability(embeddings[i], text[q]))).ToList();
                    predictions.AddRange(Suppress(found, 0.5).Select(d => new Prediction(scene.Index, q, d.Box, d.Score)));
                }
                Console.WriteLine($"  scene {scene.Index:D2}: {regions.Count} regions, {Proposals.GridsSplit} grids split so far, {timings.PerScreen[^1]:F0} ms");
                if (opt.DrawRegions)
                    Draw(scene, regions.Select(r => (new RectangleF(r.X, r.Y, r.Width, r.Height), "")).ToList(), Path.Combine(results, $"regions-{scene.Index:D2}.png"));
            }
        }
        else
        {
            using var model = new OwlVit(Path.Combine(models, "owlvit"), opt.Gpu, opt.Quantized);
            timings.Load = load.Elapsed.TotalMilliseconds;
            if (opt.Command == "inspect") { Console.WriteLine(model.Describe()); return 0; }
            var prompts = Queries.Select(q => Prompt(q, opt.Template)).ToList();
            bool warm = false;
            foreach (var scene in scenes)
            {
                var tiles = Tiles(scene.Bitmap.Width, scene.Bitmap.Height, opt.Tiling);
                if (!warm) { model.Detect(scene.Bitmap, tiles[0], prompts.Take(1).ToList(), 1f); warm = true; }

                // What one keystroke costs: image and a single query through the whole model, every tile.
                var sw = Stopwatch.StartNew();
                foreach (var tile in tiles)
                    model.Detect(scene.Bitmap, tile, prompts.Take(1).ToList(), 1f);
                timings.PerScreen.Add(sw.Elapsed.TotalMilliseconds);
                timings.Regions.Add(tiles.Count);

                var perQuery = Queries.Select(_ => new List<Detection>()).ToArray();
                foreach (var tile in tiles)
                {
                    var found = model.Detect(scene.Bitmap, tile, prompts, 0.02f);
                    for (int q = 0; q < Queries.Length; q++)
                        perQuery[q].AddRange(found[q]);
                }
                for (int q = 0; q < Queries.Length; q++)
                    predictions.AddRange(Suppress(perQuery[q], 0.3).Select(d => new Prediction(scene.Index, Queries[q], d.Box, d.Score)));
                Console.WriteLine($"  scene {scene.Index:D2}: {tiles.Count} tiles, {timings.PerScreen[^1]:F0} ms per query");
            }
        }

        timings.Report(Say, opt.Model);
        Metrics.Report(scenes, Queries, predictions, opt.Model == "siglip" ? Metrics.SiglipThresholds : Metrics.OwlThresholds, Say);

        if (opt.DrawThreshold is float t)
            foreach (var scene in scenes)
                Draw(scene, predictions.Where(p => p.Scene == scene.Index && p.Score >= t).Select(p => (p.Box, $"{p.Query} {p.Score:F2}")).ToList(),
                     Path.Combine(results, $"{runName}-scene{scene.Index:D2}.png"));

        File.WriteAllText(Path.Combine(results, runName + ".md"), log.ToString());
        File.WriteAllText(Path.Combine(results, runName + ".predictions.json"), JsonSerializer.Serialize(predictions.Select(p =>
            new { p.Scene, p.Query, Box = new[] { p.Box.X, p.Box.Y, p.Box.Width, p.Box.Height }, p.Score })));
        return 0;
    }

    private static string Prompt(string query, string template)
    {
        string article = "aeiou".Contains(query[0]) ? "an" : "a";
        return template.Replace("{a}", article).Replace("{q}", query);
    }

    /// <summary>Square tiles for OWL-ViT: the whole screen at once, or overlapping screen-height squares.</summary>
    private static List<Rectangle> Tiles(int width, int height, string mode)
    {
        if (mode == "whole")
            return new List<Rectangle> { new(0, 0, width, height) };
        int side = mode == "tiles" ? height : int.Parse(mode, CultureInfo.InvariantCulture);
        var tiles = new List<Rectangle>();
        int cols = (int)Math.Ceiling((double)(width - side) / (side * 0.75)) + 1;
        int rows = (int)Math.Ceiling((double)(height - side) / (side * 0.75)) + 1;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x = cols == 1 ? 0 : (int)Math.Round((double)c * (width - side) / (cols - 1));
                int y = rows == 1 ? 0 : (int)Math.Round((double)r * (height - side) / (rows - 1));
                tiles.Add(new Rectangle(x, y, side, side));
            }
        return tiles;
    }

    /// <summary>Greedy suppression: drop a box if it mostly overlaps a better one (IoU, or half of the smaller box).</summary>
    private static List<Detection> Suppress(List<Detection> found, double overlap)
    {
        var kept = new List<Detection>();
        foreach (var d in found.OrderByDescending(d => d.Score))
            if (!kept.Any(k => Geometry.IoU(k.Box, d.Box) > overlap || Geometry.Contained(k.Box, d.Box) > 0.7))
                kept.Add(d);
        return kept;
    }

    private static void Draw(Scene scene, List<(RectangleF Box, string Label)> boxes, string path)
    {
        using var image = new Bitmap(scene.Bitmap);
        using var g = Graphics.FromImage(image);
        using var gtPen = new Pen(Color.LimeGreen, 3);
        using var pen = new Pen(Color.Magenta, 3);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        foreach (var o in scene.Objects)
            g.DrawRectangle(gtPen, o.Box.X, o.Box.Y, o.Box.Width, o.Box.Height);
        foreach (var (box, label) in boxes)
        {
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            if (label.Length > 0)
            {
                g.FillRectangle(Brushes.Black, box.X, box.Y, g.MeasureString(label, font).Width, 20);
                g.DrawString(label, font, Brushes.White, box.X, box.Y);
            }
        }
        image.Save(path);
    }
}

internal sealed record Prediction(int Scene, string Query, RectangleF Box, float Score);

internal sealed record GroundTruth(string[] Labels, string[] Ignore, RectangleF Box);

internal sealed class Scene
{
    public int Index { get; private init; }
    public CapturedScreen Capture { get; private init; } = null!;
    public Bitmap Bitmap { get; private init; } = null!;
    public List<GroundTruth> Objects { get; private init; } = new();

    public static Scene Load(string png)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(png, ".json")));
        var objects = doc.RootElement.GetProperty("objects").EnumerateArray().Select(o =>
        {
            var b = o.GetProperty("box").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new GroundTruth(o.GetProperty("labels").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                                   o.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                                   new RectangleF(b[0], b[1], b[2], b[3]));
        }).ToList();
        return new Scene
        {
            Index = doc.RootElement.GetProperty("scene").GetInt32(),
            Capture = ScreenCapture.Load(png),
            Bitmap = new Bitmap(png),
            Objects = objects,
        };
    }
}

internal sealed class Timings
{
    public double Load;
    public List<double> PerScreen = new(), PerQuery = new(), Proposals = new(), Preprocess = new();
    public List<int> Regions = new();

    public void Report(Action<string> say, string model)
    {
        static string Stats(List<double> v) => v.Count == 0 ? "-" : $"median {Median(v):F0} ms, max {v.Max():F0} ms";
        say("");
        say("## Speed");
        say($"- Model load: {Load:F0} ms");
        if (model == "siglip")
        {
            say($"- Per screenshot, image encoding ({Regions.Min()}-{Regions.Max()} regions, median {Median(Regions.Select(x => (double)x).ToList()):F0}): {Stats(PerScreen)}");
            say($"- ... of which resizing crops: {Stats(Preprocess)}");
            say($"- Per screenshot, OCR + finding regions: {Stats(Proposals)}");
            say($"- Per query (text encoding only, what each keystroke costs): {Stats(PerQuery)}");
        }
        else
            say($"- Per query, whole model on all {Regions.Max()} tile(s) (what each keystroke costs): {Stats(PerScreen)}");
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}

internal static class Geometry
{
    public static double Area(RectangleF r) => (double)r.Width * r.Height;

    public static double Intersection(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b);
        return i.IsEmpty ? 0 : Area(i);
    }

    public static double IoU(RectangleF a, RectangleF b)
    {
        double i = Intersection(a, b);
        return i / (Area(a) + Area(b) - i);
    }

    /// <summary>How much of the smaller box lies inside the other.</summary>
    public static double Contained(RectangleF a, RectangleF b) => Intersection(a, b) / Math.Min(Area(a), Area(b));
}

internal sealed class Options
{
    public string Command = "eval", Model = "siglip", Variant = "b16", Tiling = "whole", Template = "a photo of {a} {q}.";
    public string? RunName, VisionFile;
    public bool Gpu, Quantized, FullText, DrawRegions, SplitGrids;
    public int Batch = 16, MaxRegions = 64;
    public float? DrawThreshold;
    public HashSet<int>? Scenes;
    public List<string> Rest = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": o.Model = args[++i]; break;
                case "--gpu": o.Gpu = true; break;
                case "--int8": o.Quantized = true; break;
                case "--text-fp32": o.FullText = true; break;
                case "--vision-file": o.VisionFile = args[++i]; break;
                case "--variant": o.Variant = args[++i]; break;
                case "--split-grids": o.SplitGrids = true; break;
                case "--tiles": o.Tiling = args[++i]; break;
                case "--template": o.Template = args[++i]; break;
                case "--name": o.RunName = args[++i]; break;
                case "--batch": o.Batch = int.Parse(args[++i]); break;
                case "--max-regions": o.MaxRegions = int.Parse(args[++i]); break;
                case "--draw": o.DrawThreshold = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--draw-regions": o.DrawRegions = true; break;
                case "--scenes": o.Scenes = args[++i].Split(',').Select(int.Parse).ToHashSet(); break;
                default:
                    if (i == 0) o.Command = args[i]; else o.Rest.Add(args[i]);
                    break;
            }
        }
        return o;
    }
}
