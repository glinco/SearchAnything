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
    /// Every query runs on every test screen. Most screens don't contain most of these, which is the point:
    /// a good model must also stay quiet. The last three never appear anywhere.
    /// </summary>
    private static readonly string[] Queries =
    {
        "dog", "cat", "horse", "bird", "elephant", "car", "bicycle", "airplane", "boat", "pizza", "cake",
        "coffee cup", "flower", "mountain", "beach", "laptop",
        "chart", "bar chart", "line chart", "pie chart", "table", "map", "calendar", "qr code", "source code",
        "giraffe", "guitar", "fire truck",
    };

    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ModelsDir = Path.Combine(Root, "models");
    private static readonly string ResultsDir = Path.Combine(Root, "results");

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }
        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e is ArgumentException ? e.Message : "Missing or invalid option value.");
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        Directory.CreateDirectory(ResultsDir);

        switch (opt.Command)
        {
            case "eval": await EvalAsync(opt); return 0;
            case "try": return await TryAsync(opt);
            case "tokens": Tokens(opt.Queries); return 0;
            case "inspect": Inspect(opt); return 0;
            default:
                Console.Error.WriteLine(Options.Usage);
                return 2;
        }
    }

    private static IVisualSearch CreateSearch(Options opt)
    {
        if (opt.Model == "owl")
            return new OwlSearch(new OwlVit(Path.Combine(ModelsDir, "owlvit"), opt.Gpu, opt.Int8),
                                 opt.Threshold ?? 0.15f, opt.Prompt ?? "a photo of {a} {q}", opt.Tiles);

        var variant = opt.Variant == "b32" ? SiglipVariant.B32 : SiglipVariant.B16;
        // fp16 is faster on the GPU; ONNX Runtime 1.24 can't load it on the CPU.
        string vision = opt.Int8 ? "vision_model_quantized.onnx" : opt.Gpu ? "vision_model_fp16.onnx" : "vision_model.onnx";
        string text = opt.TextInt8 ? "text_model_quantized.onnx" : "text_model.onnx";
        var model = new Siglip(ModelsDir, variant, opt.Gpu, vision, text) { BatchSize = opt.Batch };
        return new SiglipSearch(model, opt.Threshold ?? (variant == SiglipVariant.B32 ? 0.002f : 0.005f),
                                opt.Prompt ?? "{q}", opt.MaxRegions, splitGrids: !opt.NoSplit, snap: !opt.NoSnap);
    }

    /// <summary>Runs every query on every test screen, then reports speed and accuracy against the ground truth.</summary>
    private static async Task EvalAsync(Options opt)
    {
        var scenes = Directory.GetFiles(Path.Combine(Root, "data", "screens"), "scene*.png").OrderBy(f => f).Select(Scene.Load)
            .Where(s => opt.Scenes == null || opt.Scenes.Contains(s.Index)).ToList();
        string runName = opt.RunName ?? $"{opt.Model}{(opt.Model == "siglip" ? "-" + opt.Variant : "")}-{(opt.Gpu ? "gpu" : "cpu")}";
        var log = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); log.AppendLine(line); }
        Say($"# {runName}");
        Say($"{Environment.ProcessorCount} logical CPUs; {scenes.Count} screens; {Queries.Length} queries.");

        var load = Stopwatch.StartNew();
        using var search = CreateSearch(opt);
        double loadMs = load.Elapsed.TotalMilliseconds;

        // Warm-up: the first run of a model is much slower than the rest.
        await search.PrepareAsync(scenes[0].Capture);
        search.Find(new[] { "warm up" });

        var prepareMs = new List<double>();
        var keystrokeMs = new List<double>();
        var regionCounts = new List<double>();
        var predictions = new List<Prediction>();
        foreach (var scene in scenes)
        {
            var sw = Stopwatch.StartNew();
            await search.PrepareAsync(scene.Capture);
            prepareMs.Add(sw.Elapsed.TotalMilliseconds);
            regionCounts.Add(search.Regions.Count);

            // What a keystroke costs: one query the model hasn't seen yet, on this screen.
            if (scene == scenes[0])
                foreach (var q in Queries)
                {
                    sw.Restart();
                    search.Find(new[] { q });
                    keystrokeMs.Add(sw.Elapsed.TotalMilliseconds);
                }

            var found = search.Find(Queries);
            for (int q = 0; q < Queries.Length; q++)
                predictions.AddRange(found[q].Select(d => new Prediction(scene.Index, Queries[q], d.Box, d.Score)));
            Console.WriteLine($"  scene {scene.Index:D2}: {search.Regions.Count} regions, prepared in {prepareMs[^1]:F0} ms");

            if (opt.DrawRegions)
                Draw(scene.Path, scene.Objects.Select(o => o.Box), search.Regions.Select(r => ((RectangleF)r, "")),
                     Path.Combine(ResultsDir, $"{runName}-regions-{scene.Index:D2}.png"));
            if (opt.Draw)
                Draw(scene.Path, scene.Objects.Select(o => o.Box),
                     predictions.Where(p => p.Scene == scene.Index && p.Score >= search.Threshold).Select(p => (p.Box, $"{p.Query} {p.Score:F3}")),
                     Path.Combine(ResultsDir, $"{runName}-scene{scene.Index:D2}.png"));
        }

        static string Stats(List<double> v) => $"median {Median(v):F0} ms, max {v.Max():F0} ms";
        Say("");
        Say("## Speed");
        Say($"- Model load: {loadMs:F0} ms");
        Say($"- Per screenshot, prepare ({regionCounts.Min()}-{regionCounts.Max()} regions, median {Median(regionCounts):F0}): {Stats(prepareMs)}");
        Say($"- Per keystroke (one new query): {Stats(keystrokeMs)}");
        if (search is OwlSearch)
            Say("- This OWL-ViT export runs the image encoder with every query, so its cost is all per keystroke.");

        Metrics.Report(scenes, Queries, predictions, search is OwlSearch ? Metrics.OwlThresholds : Metrics.SiglipThresholds, Say);
        File.WriteAllText(Path.Combine(ResultsDir, runName + ".md"), log.ToString());
        File.WriteAllText(Path.Combine(ResultsDir, runName + ".predictions.json"), JsonSerializer.Serialize(predictions.Select(p =>
            new { p.Scene, p.Query, Box = new[] { p.Box.X, p.Box.Y, p.Box.Width, p.Box.Height }, p.Score })));
    }

    /// <summary>Searches one image of your own and saves a copy with the highlights drawn on it.</summary>
    private static async Task<int> TryAsync(Options opt)
    {
        if (opt.Image == null || opt.Queries.Count == 0)
        {
            Console.Error.WriteLine("try needs --image <file> and at least one query.");
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        if (!File.Exists(opt.Image))
        {
            Console.Error.WriteLine($"No such image: {opt.Image}");
            return 2;
        }

        using var search = CreateSearch(opt);
        var screen = ScreenCapture.Load(opt.Image);
        var sw = Stopwatch.StartNew();
        await search.PrepareAsync(screen);
        Console.WriteLine($"{Path.GetFileName(opt.Image)}: {screen.Width}x{screen.Height}, {search.Regions.Count} regions, prepared in {sw.Elapsed.TotalMilliseconds:F0} ms");

        sw.Restart();
        var found = search.Find(opt.Queries);
        Console.WriteLine($"Searched {opt.Queries.Count} quer{(opt.Queries.Count == 1 ? "y" : "ies")} in {sw.Elapsed.TotalMilliseconds:F0} ms. Highlights need a score of {search.Threshold}.");

        var highlights = new List<(RectangleF Box, string Label)>();
        for (int q = 0; q < opt.Queries.Count; q++)
        {
            var hits = found[q].Where(d => d.Score >= search.Threshold).ToList();
            var best = found[q].FirstOrDefault();
            Console.WriteLine();
            Console.WriteLine(hits.Count == 0
                ? $"{opt.Queries[q]}: nothing highlighted" + (best == null ? "" : $" (best score {best.Score:F4} {Describe(best.Box)})")
                : $"{opt.Queries[q]}: {hits.Count} highlight{(hits.Count == 1 ? "" : "s")}");
            foreach (var hit in hits)
            {
                Console.WriteLine($"  {hit.Score:F3}  {Describe(hit.Box)}");
                highlights.Add((hit.Box, $"{opt.Queries[q]} {hit.Score:F3}"));
            }
        }

        string name = Path.GetFileNameWithoutExtension(opt.Image);
        string output = Path.Combine(ResultsDir, $"try-{name}.png");
        Draw(opt.Image, Array.Empty<RectangleF>(), highlights, output);
        Console.WriteLine();
        Console.WriteLine($"Saved {output}");
        if (opt.DrawRegions)
        {
            string regions = Path.Combine(ResultsDir, $"try-{name}-regions.png");
            Draw(opt.Image, Array.Empty<RectangleF>(), search.Regions.Select(r => ((RectangleF)r, "")), regions);
            Console.WriteLine($"Saved {regions}");
        }
        return 0;
    }

    private static string Describe(RectangleF box) => $"at {box.X:F0},{box.Y:F0} size {box.Width:F0}x{box.Height:F0}";

    /// <summary>Prints token IDs, to compare against the Hugging Face tokenizers.</summary>
    private static void Tokens(IEnumerable<string> texts)
    {
        var gemma = new GemmaTokenizer(Path.Combine(ModelsDir, "siglip2", "tokenizer.json"));
        var clip = new ClipTokenizer(Path.Combine(ModelsDir, "owlvit", "vocab.json"), Path.Combine(ModelsDir, "owlvit", "merges.txt"), padId: 0);
        foreach (var text in texts)
        {
            Console.WriteLine($"gemma  {text}: {string.Join(" ", gemma.Encode(text).TakeWhile(i => i != 0))}");
            Console.WriteLine($"clip   {text}: {string.Join(" ", clip.Encode(text, 16).Ids)}");
        }
    }

    /// <summary>Prints the models' input and output names, types and shapes.</summary>
    private static void Inspect(Options opt)
    {
        if (opt.Model == "owl")
        {
            using var owl = new OwlVit(Path.Combine(ModelsDir, "owlvit"), opt.Gpu, opt.Int8);
            Console.WriteLine(owl.Describe());
            return;
        }
        var variant = opt.Variant == "b32" ? SiglipVariant.B32 : SiglipVariant.B16;
        using var siglip = new Siglip(ModelsDir, variant, opt.Gpu, opt.Gpu ? "vision_model_fp16.onnx" : "vision_model.onnx", "text_model.onnx");
        Console.WriteLine(siglip.Describe());
    }

    /// <summary>Saves a copy of the image with ground truth in green and the given boxes in magenta.</summary>
    private static void Draw(string imagePath, IEnumerable<RectangleF> truth, IEnumerable<(RectangleF Box, string Label)> boxes, string outputPath)
    {
        using var image = new Bitmap(imagePath);
        using var canvas = new Bitmap(image.Width, image.Height);
        using var g = Graphics.FromImage(canvas);
        using var truthPen = new Pen(Color.LimeGreen, 3);
        using var pen = new Pen(Color.Magenta, 3);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        g.DrawImage(image, 0, 0, image.Width, image.Height);
        foreach (var t in truth)
            g.DrawRectangle(truthPen, t.X, t.Y, t.Width, t.Height);
        foreach (var (box, label) in boxes)
        {
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            if (label.Length > 0)
            {
                g.FillRectangle(Brushes.Black, box.X, box.Y, g.MeasureString(label, font).Width, 20);
                g.DrawString(label, font, Brushes.White, box.X, box.Y);
            }
        }
        canvas.Save(outputPath);
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}

internal sealed record Prediction(int Scene, string Query, RectangleF Box, float Score);

internal sealed record GroundTruth(string[] Labels, string[] Ignore, RectangleF Box);

/// <summary>One rendered test screen and its ground truth (written by eval/render.ps1).</summary>
internal sealed class Scene
{
    public int Index { get; private init; }
    public string Path { get; private init; } = "";
    public CapturedScreen Capture { get; private init; } = null!;
    public List<GroundTruth> Objects { get; private init; } = new();

    public static Scene Load(string png)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.ChangeExtension(png, ".json")));
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
            Path = png,
            Capture = ScreenCapture.Load(png),
            Objects = objects,
        };
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
    public const string Usage = """
        Usage: dotnet run -c Release -- <command> [options]

        Commands
          eval                     Score a model on the rendered test screens (default)
          try --image <file> <query> [<query> ...]
                                   Search one image of your own; saves results/try-<name>.png
          tokens <text> [...]      Print token IDs for both tokenizers
          inspect                  Print the model's inputs and outputs

        Model
          --model siglip|owl       Default siglip
          --variant b16|b32        SigLIP 2 size: b16 (patch 16, 224 px, default) or b32 (patch 32, 256 px, faster)
          --gpu                    Run on the GPU with DirectML (SigLIP then uses its fp16 vision model)
          --int8                   Use the 8-bit vision model (SigLIP) or the 8-bit model (OWL-ViT)
          --text-int8              Use the 8-bit SigLIP text model (B/16 only)
          --prompt <template>      Text sent to the model; {q} is the query, {a} is "a" or "an"
                                   Default "{q}" for SigLIP, "a photo of {a} {q}" for OWL-ViT
          --threshold <score>      Score a highlight needs. Default 0.005 (b16), 0.002 (b32), 0.15 (OWL-ViT)

        Regions (SigLIP)
          --max-regions <n>        Most regions encoded per screenshot. Default 128
          --no-split               Don't cut thumbnail walls into one region per thumbnail
          --no-snap                Don't widen highlights to the whole picture they sit on
          --batch <n>              Regions per model call. Default 16

        Tiles (OWL-ViT)
          --tiles whole|<side>     The whole screen at once, or overlapping squares. Default 768

        Output
          --name <run>             Report name under results/
          --scenes 0,3,15          Only these test screens
          --draw                   Save each screen with its highlights drawn on it
          --draw-regions           Also save the regions that were looked at
        """;

    public string Command = "eval", Model = "siglip", Variant = "b16", Tiles = "768";
    public string? RunName, Prompt, Image;
    public bool Gpu, Int8, TextInt8, NoSplit, NoSnap, Draw, DrawRegions;
    public int Batch = 16, MaxRegions = 128;
    public float? Threshold;
    public HashSet<int>? Scenes;
    public List<string> Queries = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": o.Model = OneOf(args[++i], "siglip", "owl"); break;
                case "--variant": o.Variant = OneOf(args[++i], "b16", "b32"); break;
                case "--gpu": o.Gpu = true; break;
                case "--int8": o.Int8 = true; break;
                case "--text-int8": o.TextInt8 = true; break;
                case "--prompt": o.Prompt = args[++i]; break;
                case "--threshold": o.Threshold = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-regions": o.MaxRegions = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-split": o.NoSplit = true; break;
                case "--no-snap": o.NoSnap = true; break;
                case "--batch": o.Batch = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--tiles": o.Tiles = args[++i]; break;
                case "--name": o.RunName = args[++i]; break;
                case "--scenes": o.Scenes = args[++i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToHashSet(); break;
                case "--draw": o.Draw = true; break;
                case "--draw-regions": o.DrawRegions = true; break;
                case "--image": o.Image = args[++i]; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option {args[i]}");
                    if (i == 0)
                        o.Command = args[i];
                    else
                        o.Queries.Add(args[i]);
                    break;
            }
        }
        return o;
    }

    private static string OneOf(string value, params string[] allowed) =>
        allowed.Contains(value) ? value : throw new ArgumentException($"Expected one of {string.Join(", ", allowed)}, got {value}");
}
