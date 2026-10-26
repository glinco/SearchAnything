using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Rect = System.Windows.Rect;

namespace SearchAnything;

internal sealed record OcrWordBox(string Text, Rect Box);

internal sealed record OcrLineBox(IReadOnlyList<OcrWordBox> Words);

/// <summary>Wraps the built-in Windows OCR engine (offline, no downloads).</summary>
internal sealed class OcrService
{
    private readonly OcrEngine _engine;

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    private OcrService(OcrEngine engine) => _engine = engine;

    private const int MaxEngines = 3;

    public static IReadOnlyList<string> AvailableLanguageTags =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>One engine for one specific installed language (used by the self-test).</summary>
    public static OcrService? Create(string languageTag)
    {
        var language = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => string.Equals(l.LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase));
        var engine = language == null ? null : OcrEngine.TryCreateFromLanguage(language);
        return engine == null ? null : new OcrService(engine);
    }

    /// <summary>
    /// One engine per installed OCR language (up to 3), the user's preferred language first.
    /// Engines fail differently: measured on the same screen, the Spanish one read accents correctly
    /// but turned "15/10/2026" into "15/18/2826", while the English one got the digits right but
    /// turned "Acción" into "Acci6n". Searching every reading covers each one's blind spots.
    /// </summary>
    public static IReadOnlyList<OcrService> CreateAll()
    {
        var services = new List<OcrService>();
        var preferred = OcrEngine.TryCreateFromUserProfileLanguages();
        if (preferred != null)
            services.Add(new OcrService(preferred));

        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            if (services.Count >= MaxEngines)
                break;
            if (services.Any(s => s.LanguageTag == language.LanguageTag))
                continue;
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine != null)
                services.Add(new OcrService(engine));
        }
        return services;
    }

    /// <summary>
    /// Recognizes text, optionally enlarging the image first (small UI text reads better when bigger).
    /// Returned boxes are always in the original image's pixel coordinates.
    /// </summary>
    public async Task<IReadOnlyList<OcrLineBox>> RecognizeAsync(CapturedScreen shot, double scale = 1.0)
    {
        double maxScale = (double)OcrEngine.MaxImageDimension / Math.Max(shot.Width, shot.Height);
        scale = Math.Min(scale, maxScale);

        byte[] pixels = shot.Pixels;
        int width = shot.Width, height = shot.Height;
        if (Math.Abs(scale - 1.0) > 0.01)
            (pixels, width, height) = await Task.Run(() => ScreenCapture.Resize(shot, scale));
        else
            scale = 1.0;

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap);

        return result.Lines
            .Select(line => new OcrLineBox(line.Words
                .Select(w => new OcrWordBox(w.Text, new Rect(
                    w.BoundingRect.X / scale,
                    w.BoundingRect.Y / scale,
                    w.BoundingRect.Width / scale,
                    w.BoundingRect.Height / scale)))
                .ToList()))
            .Where(line => line.Words.Count > 0)
            .ToList();
    }
}
