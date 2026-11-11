using System.Drawing.Imaging;
using Tesseract;

namespace TrayOcrSnipper;

/// <summary>
/// OCR bằng Tesseract (offline) với dữ liệu tiếng Việt + tiếng Anh ("vie+eng").
/// - Windows OCR không hỗ trợ tiếng Việt nên đây là bộ OCR chính cho tiếng Việt.
/// - File dữ liệu .traineddata (bản "tessdata_best", chính xác nhất) được tự tải về lần đầu
///   vào %LOCALAPPDATA%\TrayOcrSnipper\tessdata.
/// </summary>
internal sealed class TesseractService : IDisposable
{
    public const string Languages = "vie+eng";
    public const string LanguageLabel = "vie+eng (Tesseract)";

    private const string BaseUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";
    private const long MinValidFileSize = 1_000_000; // file hợp lệ phải > ~1MB (tránh file tải dở / trang lỗi HTML)
    private static readonly string[] RequiredFiles = { "vie.traineddata", "eng.traineddata" };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayOcrSnipper", "tessdata");

    private readonly SemaphoreSlim _lock = new(1, 1);   // TesseractEngine KHÔNG thread-safe
    private TesseractEngine? _engine;
    private bool _disposed;

    /// <summary>True nếu đủ file dữ liệu vie + eng.</summary>
    public bool IsDataInstalled => RequiredFiles.All(IsFileValid);

    private static bool IsFileValid(string fileName)
    {
        var fi = new FileInfo(Path.Combine(DataDirectory, fileName));
        return fi.Exists && fi.Length > MinValidFileSize;
    }

    // ------------------------------------------------------------------
    //  Tải dữ liệu
    // ------------------------------------------------------------------

    public async Task EnsureDataAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDirectory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TrayOcrSnipper/1.0");

        foreach (string file in RequiredFiles)
        {
            if (IsFileValid(file)) continue;

            string finalPath = Path.Combine(DataDirectory, file);
            string tempPath = finalPath + ".download";

            using (var response = await http.GetAsync(BaseUrl + file, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(tempPath);
                await source.CopyToAsync(target, ct);
            }

            if (new FileInfo(tempPath).Length <= MinValidFileSize)
            {
                File.Delete(tempPath);
                throw new IOException($"File {file} tải về không hợp lệ (quá nhỏ).");
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
    }

    // ------------------------------------------------------------------
    //  OCR
    // ------------------------------------------------------------------

    public async Task<string> RecognizeAsync(Bitmap source)
    {
        // 1) Tiền xử lý ảnh (phóng to + thang xám + tăng tương phản) ở thread pool
        byte[] png = await Task.Run(() =>
        {
            using var processed = ImageProcessor.Prepare(source, maxDimension: 8000, binarize: false);
            using var ms = new MemoryStream();
            processed.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        });

        // 2) Chạy Tesseract (tuần tự hóa bằng semaphore vì engine không thread-safe)
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(() => RunOcr(png));
        }
        finally
        {
            _lock.Release();
        }
    }

    private string RunOcr(byte[] png)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TesseractService));

        if (_engine == null)
        {
            // LstmOnly: dùng mạng nơ-ron LSTM (chính xác nhất, khớp với dữ liệu tessdata_best)
            _engine = new TesseractEngine(DataDirectory, Languages, EngineMode.LstmOnly);
            _engine.SetVariable("user_defined_dpi", "300"); // tránh cảnh báo ước lượng DPI
        }

        using var pix = Pix.LoadFromMemory(png);

        // Lượt 1: tự phân tích bố cục (đoạn văn, nhiều dòng)
        using (var page = _engine.Process(pix, PageSegMode.Auto))
        {
            string text = page.GetText();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        // Lượt 2 (dự phòng): tìm chữ rải rác không theo bố cục
        using (var page = _engine.Process(pix, PageSegMode.SparseText))
        {
            return page.GetText();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine?.Dispose();
        _engine = null;
        _lock.Dispose();
    }
}