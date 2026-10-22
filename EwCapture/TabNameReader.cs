using EwCapture.Core.Recognition;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace EwCapture;

/// <summary>
/// Reads the open tab's name off the tab strip with Windows' built-in OCR (no
/// extra download). Copies of a tab type are told apart by their names.
/// </summary>
static class TabNameReader
{
    const int Scale = 3; // OCR reads small text much better enlarged
    static readonly OcrEngine? Engine =
        OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();

    public static bool Available => Engine is not null;

    /// <summary>The open tab's name, or null if there's no strip or nothing legible.</summary>
    public static async Task<string?> ReadAsync(Frame frame1440)
    {
        if (Engine is null) return null;
        var tab = TabStrip.FindOpenTab(frame1440);
        if (tab is null) return null;
        int x0 = tab.Value.X0, w = tab.Value.X1 - x0 + 1, h = TabStrip.LabelBottom - TabStrip.LabelTop;
        int W = w * Scale, H = h * Scale;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int s = ((TabStrip.LabelTop + y / Scale) * frame1440.Width + x0 + x / Scale) * 4, d = (y * W + x) * 4;
                px[d] = frame1440.Bgra[s]; px[d + 1] = frame1440.Bgra[s + 1]; px[d + 2] = frame1440.Bgra[s + 2]; px[d + 3] = 255;
            }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(px), BitmapPixelFormat.Bgra8, W, H, BitmapAlphaMode.Premultiplied);
        var result = await Engine.RecognizeAsync(bitmap);
        var name = TabStrip.CleanName(result.Text ?? "");
        return name.Length == 0 ? null : name;
    }
}
