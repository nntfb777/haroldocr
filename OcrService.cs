using System.Drawing.Imaging;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TrayOcrSnipper;

/// <summary>Kết quả OCR thô (chưa làm sạch).</summary>
internal sealed record OcrOutput(string Text, string LanguageTag, bool IsVietnamese);

/// <summary>
/// Điều phối OCR:
///  1. Tesseract (vie+eng): bộ OCR chính, nhận dạng tiếng Việt có dấu. Cần dữ liệu .traineddata (tự tải lần đầu).
///  2. Windows OCR (Windows.Media.Ocr): dự phòng khi Tesseract chưa sẵn sàng / bị lỗi.
///     Lưu ý: Windows OCR không có tiếng Việt, chỉ dùng được tiếng Anh/ngôn ngữ khác.
/// </summary>
internal sealed class OcrService : IDisposable
{
    private readonly TesseractService _tesseract = new();
    private Task? _downloadTask;
    private bool _tesseractBroken;
    private string? _tesseractError;

    private OcrEngine? _windowsEngine;

    /// <summary>Ngôn ngữ/engine của lần OCR gần nhất.</summary>
    public string? LanguageTag { get; private set; }

    public bool IsVietnamese => LanguageTag?.StartsWith("vi", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsTesseractDataInstalled => _tesseract.IsDataInstalled;

    public bool IsDownloading => _downloadTask is { IsCompleted: false };

    // ------------------------------------------------------------------
    //  Dữ liệu tiếng Việt
    // ------------------------------------------------------------------

    /// <summary>Tải dữ liệu tiếng Việt nếu chưa có (gọi nhiều lần vẫn chỉ tải 1 lần).</summary>
    public Task EnsureVietnameseDataAsync()
    {
        if (_tesseract.IsDataInstalled) return Task.CompletedTask;

        if (_downloadTask == null || _downloadTask.IsFaulted || _downloadTask.IsCanceled)
            _downloadTask = _tesseract.EnsureDataAsync();

        return _downloadTask;
    }

    /// <summary>Mô tả trạng thái các bộ OCR (hiển thị ở "Thông tin phím tắt").</summary>
    public string GetStatusText()
    {
        var sb = new StringBuilder();

        sb.AppendLine(_tesseract.IsDataInstalled
            ? "Tesseract (tiếng Việt + Anh): ĐÃ sẵn sàng"
            : IsDownloading
                ? "Tesseract (tiếng Việt + Anh): đang tải dữ liệu…"
                : "Tesseract (tiếng Việt + Anh): CHƯA có dữ liệu (chọn 'Tải dữ liệu OCR tiếng Việt')");

        if (_tesseractBroken)
            sb.AppendLine($"Tesseract bị lỗi: {_tesseractError}");

        try
        {
            var tags = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            sb.AppendLine("Windows OCR (dự phòng, không có tiếng Việt): " +
                          (tags.Count == 0 ? "chưa có gói nào" : string.Join(", ", tags)));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Windows OCR: không đọc được ({ex.Message})");
        }

        if (LanguageTag != null)
            sb.Append($"Lần OCR gần nhất dùng: {LanguageTag}");

        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    //  OCR
    // ------------------------------------------------------------------

    public async Task<OcrOutput> RecognizeAsync(Bitmap source)
    {
        // --- Ưu tiên Tesseract (tiếng Việt) ---
        if (!_tesseractBroken && _tesseract.IsDataInstalled)
        {
            try
            {
                string text = await _tesseract.RecognizeAsync(source);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LanguageTag = TesseractService.LanguageLabel;
                    return new OcrOutput(text, LanguageTag, IsVietnamese: true);
                }
                // Rỗng → thử Windows OCR bên dưới
            }
            catch (Exception ex)
            {
                // Thường do thiếu DLL gốc (VC++ Redistributable) hoặc dữ liệu hỏng
                Logger.Log(ex, "Tesseract");
                _tesseractBroken = true;
                _tesseractError = ex.Message;
            }
        }

        // --- Dự phòng: Windows OCR ---
        var engine = GetWindowsEngine();

        string result = await RecognizeWindowsAsync(engine, source, binarize: false);
        if (string.IsNullOrWhiteSpace(result))
            result = await RecognizeWindowsAsync(engine, source, binarize: true);

        LanguageTag = engine.RecognizerLanguage.LanguageTag;
        return new OcrOutput(result, LanguageTag, IsVietnamese: false);
    }

    // ------------------------------------------------------------------
    //  Windows OCR (dự phòng)
    // ------------------------------------------------------------------

    private OcrEngine GetWindowsEngine()
    {
        if (_windowsEngine != null) return _windowsEngine;

        var available = OcrEngine.AvailableRecognizerLanguages;

        Language? pick =
            available.FirstOrDefault(l => l.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        OcrEngine? engine = pick != null ? OcrEngine.TryCreateFromLanguage(pick) : null;
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine == null)
            throw new InvalidOperationException(
                "Chưa có bộ OCR nào sẵn sàng. Hãy chọn 'Tải dữ liệu OCR tiếng Việt' ở menu khay " +
                "(cần Internet) hoặc cài gói OCR tiếng Anh của Windows.");

        _windowsEngine = engine;
        return engine;
    }

    private static async Task<string> RecognizeWindowsAsync(OcrEngine engine, Bitmap source, bool binarize)
    {
        int maxDim = (int)OcrEngine.MaxImageDimension;

        byte[] pngBytes = await Task.Run(() =>
        {
            using var processed = ImageProcessor.Prepare(source, maxDim, binarize);
            using var ms = new MemoryStream();
            processed.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var softwareBitmap = await ToSoftwareBitmapAsync(pngBytes);
        OcrResult result = await engine.RecognizeAsync(softwareBitmap);

        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        using IRandomAccessStream ras = ms.AsRandomAccessStream();

        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public void Dispose() => _tesseract.Dispose();
}