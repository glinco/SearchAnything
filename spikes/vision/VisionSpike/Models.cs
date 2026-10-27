using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace VisionSpike;

internal sealed record Detection(RectangleF Box, float Score);

internal static class Sessions
{
    public static InferenceSession Open(string path, bool gpu)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (gpu)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        return new InferenceSession(path, options);
    }

    public static (float[] Data, long[] Shape) Run(InferenceSession session, string output, params (string Name, OrtValue Value)[] inputs)
    {
        using var run = new RunOptions();
        using var results = session.Run(run, inputs.Select(i => i.Name).ToArray(), inputs.Select(i => i.Value).ToArray(), new[] { output });
        var info = results[0].GetTensorTypeAndShape();
        return (results[0].GetTensorDataAsSpan<float>().ToArray(), info.Shape);
    }

    public static string Describe(InferenceSession s) =>
        "  inputs:  " + string.Join(", ", s.InputMetadata.Select(m => $"{m.Key}:{m.Value.ElementDataType}[{string.Join(",", m.Value.Dimensions)}]")) + "\n" +
        "  outputs: " + string.Join(", ", s.OutputMetadata.Select(m => $"{m.Key}:{m.Value.ElementDataType}[{string.Join(",", m.Value.Dimensions)}]"));
}

/// <summary>One SigLIP 2 checkpoint: where its files are, its input size, and its score calibration.</summary>
/// <remarks>LogitScale and LogitBias are read from Google's model.safetensors (logit_scale is stored as a log).</remarks>
internal sealed record SiglipVariant(string Folder, int Size, float LogitScale, float LogitBias)
{
    public static readonly SiglipVariant B16 = new("siglip2", 224, 4.724453449249268f, -16.771724700927734f);
    public static readonly SiglipVariant B32 = new("siglip2-b32", 256, 4.717002868652344f, -16.766876220703125f);
}

/// <summary>
/// SigLIP 2 base (patch 16 at 224 px, or patch 32 at 256 px): an image encoder and a text encoder whose
/// outputs are compared directly. Each image region is encoded once per screenshot; each query costs only
/// one text encoding.
/// </summary>
internal sealed class Siglip : IDisposable
{
    private static readonly float[] Mean = { 0.5f, 0.5f, 0.5f }, Std = { 0.5f, 0.5f, 0.5f };

    private readonly InferenceSession _vision, _text;
    private readonly GemmaTokenizer _tokenizer;
    private readonly SiglipVariant _variant;
    private int Size => _variant.Size;
    public int BatchSize { get; init; } = 16;

    public Siglip(string models, SiglipVariant variant, bool gpu, string visionFile, bool fullText)
    {
        _variant = variant;
        string dir = Path.Combine(models, variant.Folder);
        _vision = Sessions.Open(Path.Combine(dir, "onnx", visionFile), gpu);
        _text = Sessions.Open(Path.Combine(dir, "onnx", fullText ? "text_model.onnx" : "text_model_quantized.onnx"), gpu);
        _tokenizer = new GemmaTokenizer(Path.Combine(dir, "tokenizer.json"));
    }

    public string Describe() => "vision\n" + Sessions.Describe(_vision) + "\ntext\n" + Sessions.Describe(_text);

    public long[] Tokenize(string text) => _tokenizer.Encode(text);

    /// <summary>Time spent resizing crops (as opposed to running the model) in the last <see cref="EncodeRegions"/>.</summary>
    public double LastPreprocessMs { get; private set; }

    public float[][] EncodeRegions(SearchAnything.CapturedScreen screen, IReadOnlyList<Rectangle> regions)
    {
        var embeddings = new List<float[]>();
        LastPreprocessMs = 0;
        for (int start = 0; start < regions.Count; start += BatchSize)
        {
            int n = Math.Min(BatchSize, regions.Count - start);
            var tensor = new float[n * 3 * Size * Size];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.For(0, n, i =>
                ImageOps.WriteTensor(screen.Pixels, screen.Width, screen.Height, regions[start + i], Size, Mean, Std, tensor, i * 3 * Size * Size));
            LastPreprocessMs += sw.Elapsed.TotalMilliseconds;
            using var input = OrtValue.CreateTensorValueFromMemory(tensor, new long[] { n, 3, Size, Size });
            var (data, shape) = Sessions.Run(_vision, "pooler_output", ("pixel_values", input));
            embeddings.AddRange(Split(data, (int)shape[1]));
        }
        return embeddings.ToArray();
    }

    public float[] EncodeText(string text)
    {
        var ids = _tokenizer.Encode(text);
        using var input = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, ids.Length });
        var (data, _) = Sessions.Run(_text, "pooler_output", ("input_ids", input));
        return Normalize(data);
    }

    /// <summary>SigLIP is trained with a sigmoid per image-text pair, so this is a probability on its own.</summary>
    public float Probability(float[] image, float[] text)
    {
        float dot = 0;
        for (int i = 0; i < image.Length; i++)
            dot += image[i] * text[i];
        return 1f / (1f + MathF.Exp(-(dot * MathF.Exp(_variant.LogitScale) + _variant.LogitBias)));
    }

    private static IEnumerable<float[]> Split(float[] data, int dim)
    {
        for (int i = 0; i < data.Length / dim; i++)
            yield return Normalize(data.AsSpan(i * dim, dim).ToArray());
    }

    private static float[] Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}

/// <summary>
/// OWL-ViT base/32 at 768 px: an open-vocabulary detector. This export runs image and text together,
/// so every query pays for the image encoder too.
/// </summary>
internal sealed class OwlVit : IDisposable
{
    private const int Size = 768, TokenLength = 16;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f }, Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    private readonly InferenceSession _session;
    private readonly ClipTokenizer _tokenizer;

    public OwlVit(string dir, bool gpu, bool quantized)
    {
        _session = Sessions.Open(Path.Combine(dir, "onnx", quantized ? "model_quantized.onnx" : "model.onnx"), gpu);
        _tokenizer = new ClipTokenizer(Path.Combine(dir, "vocab.json"), Path.Combine(dir, "merges.txt"), padId: 0);
    }

    public string Describe() => Sessions.Describe(_session);

    public long[] Tokenize(string text) => _tokenizer.Encode(text, TokenLength).Ids;

    /// <summary>Detections per query, in screen pixels, for one square tile of the screen.</summary>
    public List<Detection>[] Detect(Bitmap screen, Rectangle tile, IReadOnlyList<string> queries, float minScore)
    {
        var pixels = new float[3 * Size * Size];
        ImageOps.WriteTensor(screen, tile, Size, Mean, Std, padSquare: true, InterpolationMode.HighQualityBicubic, pixels, 0);
        var ids = new long[queries.Count * TokenLength];
        var mask = new long[queries.Count * TokenLength];
        for (int q = 0; q < queries.Count; q++)
        {
            var (i, m) = _tokenizer.Encode(queries[q], TokenLength);
            i.CopyTo(ids, q * TokenLength);
            m.CopyTo(mask, q * TokenLength);
        }

        using var pixelValue = OrtValue.CreateTensorValueFromMemory(pixels, new long[] { 1, 3, Size, Size });
        using var idValue = OrtValue.CreateTensorValueFromMemory(ids, new long[] { queries.Count, TokenLength });
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, new long[] { queries.Count, TokenLength });
        using var run = new RunOptions();
        using var results = _session.Run(run, new[] { "input_ids", "pixel_values", "attention_mask" },
            new[] { idValue, pixelValue, maskValue }, new[] { "logits", "pred_boxes" });
        var logits = results[0].GetTensorDataAsSpan<float>();
        var boxes = results[1].GetTensorDataAsSpan<float>();
        int patches = (int)results[0].GetTensorTypeAndShape().Shape[1];

        double side = Math.Max(tile.Width, tile.Height);
        var found = Enumerable.Range(0, queries.Count).Select(_ => new List<Detection>()).ToArray();
        for (int p = 0; p < patches; p++)
            for (int q = 0; q < queries.Count; q++)
            {
                float score = 1f / (1f + MathF.Exp(-logits[p * queries.Count + q]));
                if (score < minScore)
                    continue;
                float cx = boxes[p * 4], cy = boxes[p * 4 + 1], w = boxes[p * 4 + 2], h = boxes[p * 4 + 3];
                var box = new RectangleF((float)(tile.X + (cx - w / 2) * side), (float)(tile.Y + (cy - h / 2) * side),
                                         (float)(w * side), (float)(h * side));
                found[q].Add(new Detection(box, score));
            }
        return found;
    }

    public void Dispose() => _session.Dispose();
}
