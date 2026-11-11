using System.Text;

namespace TrayOcrSnipper;

/// <summary>
/// Ghi log lỗi ra file %LOCALAPPDATA%\TrayOcrSnipper\error.log.
/// Tuyệt đối không ném exception ra ngoài (logger không được phép làm sập app).
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayOcrSnipper", "error.log");

    public static void Log(Exception ex, string? context = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);

                // Giới hạn kích thước file log (~1MB) để không phình mãi
                var fi = new FileInfo(LogFilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                    fi.Delete();

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}");
                sb.AppendLine(ex.ToString());
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Bỏ qua: không có gì hợp lý hơn để làm ở đây.
        }
    }
}
