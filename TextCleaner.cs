using System.Text;
using System.Text.RegularExpressions;

namespace TrayOcrSnipper;

/// <summary>
/// Làm sạch văn bản sau OCR.
/// </summary>
internal static class TextCleaner
{
    private static readonly Regex MultiSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex MultiNewLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // 1) Chuẩn hóa Unicode về dạng NFC: rất quan trọng với tiếng Việt,
        //    tránh chuyện "ế" bị tách thành "ê" + dấu sắc rời, gây lỗi khi tìm kiếm/dán.
        string text = raw.Normalize(NormalizationForm.FormC);

        // 2) Loại ký tự "vô hình" và đổi khoảng trắng đặc biệt thành khoảng trắng thường
        text = text.Replace("\u200B", "")   // zero-width space
                   .Replace("\uFEFF", "")   // BOM
                   .Replace('\u00A0', ' ')  // non-breaking space
                   .Replace('\t', ' ');

        // 3) Chuẩn hóa xuống dòng về '\n' (tạm thời)
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 4) Cắt khoảng trắng đầu/cuối từng dòng, gộp khoảng trắng liên tiếp
        var lines = text.Split('\n')
                        .Select(l => MultiSpaces.Replace(l.Trim(), " "));
        text = string.Join("\n", lines);

        // 5) Tối đa 1 dòng trống liên tiếp, bỏ khoảng trắng thừa đầu/cuối toàn bộ
        text = MultiNewLines.Replace(text, "\n\n").Trim();

        // 6) Đổi sang kiểu xuống dòng của Windows (CRLF) để dán đúng ở mọi ứng dụng
        return text.Replace("\n", Environment.NewLine);
    }
}
