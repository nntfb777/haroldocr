using System.Runtime.InteropServices;

namespace TrayOcrSnipper;

/// <summary>
/// Ghi Clipboard an toàn: Clipboard của Windows là tài nguyên dùng chung,
/// ứng dụng khác (Remote Desktop, Clipboard manager, Office...) có thể đang khóa nó
/// và khiến Clipboard.SetText ném ExternalException (CLIPBRD_E_CANT_OPEN).
/// Cách xử lý: thử lại nhiều lần, giữa các lần chờ bằng Task.Delay (KHÔNG chặn UI thread).
/// </summary>
internal static class ClipboardHelper
{
    /// <remarks>Phải gọi từ luồng UI (STA). Trong app này luôn thỏa vì await quay lại UI thread.</remarks>
    public static async Task SetTextAsync(string text, int maxAttempts = 12, int delayMs = 100)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Văn bản rỗng.", nameof(text));

        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, true, text);

                // copy = true: dữ liệu vẫn còn trong Clipboard kể cả khi app thoát.
                // retryTimes = 0: tự xử lý retry bên dưới (không dùng Thread.Sleep chặn UI).
                Clipboard.SetDataObject(data, true, 0, 0);
                return; // thành công
            }
            catch (ExternalException ex)  // Clipboard đang bị khóa
            {
                last = ex;
            }
            catch (ThreadStateException ex) // không phải STA (không nên xảy ra)
            {
                throw new InvalidOperationException("Clipboard yêu cầu luồng STA.", ex);
            }

            await Task.Delay(delayMs);
        }

        throw new IOException(
            "Không thể ghi vào Clipboard (đang bị ứng dụng khác chiếm giữ). Hãy thử lại.", last);
    }
}
