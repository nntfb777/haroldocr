using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TrayOcrSnipper;

/// <summary>
/// Tiền xử lý ảnh để Windows OCR đạt độ chính xác cao nhất. Chuỗi xử lý:
///   1. Phóng to (bicubic) nếu chữ nhỏ  → OCR hoạt động tốt nhất khi chữ cao ~20–40 px.
///   2. Chuyển thang xám (Grayscale).
///   3. Tự phát hiện nền tối (dark mode) → đảo màu để thành "chữ đen trên nền trắng".
///   4. Kéo giãn tương phản tự động (Auto-Levels theo percentile 0.5% – 99.5%).
///   5. (Tùy chọn) Nhị phân hóa bằng Otsu → tách chữ khỏi nền nhòe/nhiễu.
///   6. Thêm viền trắng quanh ảnh (OCR đọc chữ sát mép kém chính xác).
/// Chỉ dùng mảng byte + LockBits (không cần unsafe).
/// </summary>
internal static class ImageProcessor
{
    private const int Padding = 16;

    /// <summary>Trả về Bitmap mới (người gọi phải Dispose). Không thay đổi ảnh nguồn.</summary>
    public static Bitmap Prepare(Bitmap source, int maxDimension, bool binarize)
    {
        int scale = ChooseScale(source.Width, source.Height, maxDimension);

        Bitmap work = Upscale(source, scale);   // luôn trả bản sao 32bppArgb để chỉnh sửa an toàn
        try
        {
            Enhance(work, binarize);
            Bitmap padded = AddWhitePadding(work, Padding);
            work.Dispose();
            return padded;
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------
    //  Phóng to
    // ------------------------------------------------------------------

    private static int ChooseScale(int w, int h, int maxDim)
    {
        int shorter = Math.Min(w, h);
        int scale = shorter < 60 ? 3 : shorter < 200 ? 2 : 1;

        // Không vượt kích thước tối đa mà OcrEngine cho phép (kể cả phần viền thêm vào)
        while (scale > 1 && (w * scale + 2 * Padding > maxDim || h * scale + 2 * Padding > maxDim))
            scale--;

        return scale;
    }

    private static Bitmap Upscale(Bitmap src, int scale)
    {
        int nw = src.Width * scale, nh = src.Height * scale;
        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = scale == 1 ? InterpolationMode.NearestNeighbor
                                         : InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // TileFlipXY: tránh quầng sáng/tối ở mép khi nội suy
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, nw, nh), 0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attrs);
        return dst;
    }

    // ------------------------------------------------------------------
    //  Grayscale + Invert + Auto-contrast (+ Otsu)
    // ------------------------------------------------------------------

    private static void Enhance(Bitmap bmp, bool binarize)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;                 // dương với bitmap top-down của GDI+
            int byteCount = Math.Abs(stride) * h;
            var buf = new byte[byteCount];
            Marshal.Copy(data.Scan0, buf, 0, byteCount);

            // --- Bước 1: thang xám (công thức luminance ITU-R BT.601) ---
            var gray = new byte[w * h];
            long sum = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;                         // BGRA
                    int lum = (buf[i + 2] * 299 + buf[i + 1] * 587 + buf[i] * 114) / 1000;
                    gray[o + x] = (byte)lum;
                    sum += lum;
                }
            }

            // --- Bước 2: nền tối => đảo màu để thành chữ tối trên nền sáng ---
            double mean = (double)sum / gray.Length;
            if (mean < 127.0)
            {
                for (int i = 0; i < gray.Length; i++)
                    gray[i] = (byte)(255 - gray[i]);
            }

            // --- Bước 3: histogram + kéo giãn tương phản (percentile) ---
            var hist = new int[256];
            for (int i = 0; i < gray.Length; i++) hist[gray[i]]++;

            int total = gray.Length;
            int lo = PercentileLevel(hist, total, 0.005);
            int hi = PercentileLevel(hist, total, 0.995);

            if (hi - lo >= 16)
            {
                var lut = new byte[256];
                double range = hi - lo;
                for (int v = 0; v < 256; v++)
                {
                    double n = (v - lo) * 255.0 / range;
                    lut[v] = (byte)Math.Clamp((int)Math.Round(n), 0, 255);
                }

                Array.Clear(hist, 0, 256);
                for (int i = 0; i < gray.Length; i++)
                {
                    byte nv = lut[gray[i]];
                    gray[i] = nv;
                    hist[nv]++;
                }
            }

            // --- Bước 4 (tùy chọn): nhị phân hóa Otsu ---
            if (binarize)
            {
                int t = OtsuThreshold(hist, total);
                for (int i = 0; i < gray.Length; i++)
                    gray[i] = gray[i] > t ? (byte)255 : (byte)0;
            }

            // --- Ghi ngược lại vào bitmap ---
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte v = gray[o + x];
                    buf[i] = v; buf[i + 1] = v; buf[i + 2] = v; buf[i + 3] = 255;
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, byteCount);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Mức xám tại đó tích lũy histogram đạt tỉ lệ <paramref name="fraction"/>.</summary>
    private static int PercentileLevel(int[] hist, int total, double fraction)
    {
        long target = (long)(total * fraction);
        long acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc > target) return v;
        }
        return 255;
    }

    /// <summary>Thuật toán Otsu: chọn ngưỡng làm cực đại phương sai giữa hai lớp (chữ / nền).</summary>
    private static int OtsuThreshold(int[] hist, int total)
    {
        double sumAll = 0;
        for (int v = 0; v < 256; v++) sumAll += v * (double)hist[v];

        double sumB = 0, maxVar = -1;
        long wB = 0;
        int threshold = 127;

        for (int v = 0; v < 256; v++)
        {
            wB += hist[v];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;

            sumB += v * (double)hist[v];
            double mB = sumB / wB;
            double mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);

            if (between > maxVar)
            {
                maxVar = between;
                threshold = v;
            }
        }
        return threshold;
    }

    // ------------------------------------------------------------------
    //  Viền trắng
    // ------------------------------------------------------------------

    private static Bitmap AddWhitePadding(Bitmap src, int pad)
    {
        var dst = new Bitmap(src.Width + 2 * pad, src.Height + 2 * pad, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(src, pad, pad);
        return dst;
    }
}
