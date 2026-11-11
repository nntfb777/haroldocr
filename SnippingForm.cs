using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TrayOcrSnipper;

/// <summary>
/// Form overlay phủ toàn bộ Virtual Screen (tất cả màn hình) để người dùng kéo chọn vùng.
///
/// Thiết kế để KHÔNG LỖI:
///  1. Chụp "đóng băng" toàn bộ màn hình TRƯỚC khi hiện overlay. Vùng được cắt từ ảnh này nên
///     ảnh OCR không bao giờ dính overlay/viền chọn, không phụ thuộc hiệu ứng mờ dần của DWM
///     khi Hide(), và nội dung màn hình không thay đổi giữa lúc chọn.
///  2. Dùng tọa độ pixel vật lý (app khai báo PerMonitorV2 trong manifest) và đặt vị trí cửa sổ
///     bằng SetWindowPos, chặn WM_DPICHANGED để WinForms không tự co giãn form khi trải nhiều màn hình DPI khác nhau.
///  3. Double-buffer + chỉ vẽ lại vùng "bẩn" (dirty rectangle) => không giật lắc.
///  4. Giải phóng toàn bộ bitmap/brush/pen khi đóng form => không rò rỉ GDI+.
/// </summary>
internal sealed class SnippingForm : Form
{
    private const int MinSelectionSize = 5; // vùng nhỏ hơn 5x5 px coi như bấm nhầm => hủy

    private readonly Rectangle _virtualScreen;
    private readonly Bitmap _screenShot;                       // ảnh đóng băng toàn bộ màn hình
    private readonly TaskCompletionSource<Bitmap?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Tài nguyên vẽ tạo 1 lần, dùng lại cho mọi lần Paint (tránh cấp phát liên tục)
    private readonly SolidBrush _dimBrush = new(Color.FromArgb(120, 0, 0, 0));
    private readonly SolidBrush _labelBackBrush = new(Color.FromArgb(200, 20, 20, 20));
    private readonly SolidBrush _labelTextBrush = new(Color.White);
    private readonly Pen _borderPen = new(Color.FromArgb(255, 0, 170, 255), 1f);
    private readonly Font _labelFont = new("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _hintFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Point);

    private bool _dragging;
    private Point _startPoint;
    private Rectangle _selection = Rectangle.Empty;
    private bool _hasBeenActive;
    private bool _finished;

    // ------------------------------------------------------------------
    //  API công khai
    // ------------------------------------------------------------------

    /// <summary>
    /// Hiện overlay và trả về ảnh vùng được chọn (Bitmap 32bpp ARGB, người gọi phải Dispose).
    /// Trả về null nếu người dùng hủy (Esc / chuột phải / vùng quá nhỏ).
    /// </summary>
    public static Task<Bitmap?> CaptureAsync()
    {
        var form = new SnippingForm(); // constructor đã chụp màn hình; nếu lỗi sẽ ném exception ra ngoài
        form.Show();
        return form._tcs.Task;
    }

    // ------------------------------------------------------------------
    //  Khởi tạo
    // ------------------------------------------------------------------

    private SnippingForm()
    {
        // --- Cấu hình form theo yêu cầu ---
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;   // không để WinForms tự scale theo DPI
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
        KeyPreview = true;

        // --- Double-buffer: triệt tiêu flicker ---
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Opaque, true);
        UpdateStyles();

        // Virtual screen = hình chữ nhật bao quanh MỌI màn hình (có thể có tọa độ âm)
        _virtualScreen = SystemInformation.VirtualScreen;

        try
        {
            // Format32bppPArgb là định dạng GDI+ vẽ nhanh nhất
            _screenShot = new Bitmap(_virtualScreen.Width, _virtualScreen.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_screenShot);
            // Lưu ý: KHÔNG dùng SourceCopy | CaptureBlt vì .NET ném ArgumentException với tổ hợp này.
            // Trên Windows 10/11 (DWM luôn bật), SourceCopy đã chụp được cả cửa sổ layered.
            g.CopyFromScreen(_virtualScreen.Location, Point.Empty, _virtualScreen.Size,
                             CopyPixelOperation.SourceCopy);
        }
        catch
        {
            // Nếu chụp thất bại, dọn dẹp mọi thứ đã tạo rồi ném lỗi cho tầng trên hiển thị bong bóng.
            _screenShot?.Dispose();
            DisposeDrawingResources();
            throw;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW; // ẩn khỏi Alt+Tab
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Đặt vị trí/kích thước bằng SetWindowPos (pixel vật lý) thay vì thuộc tính Bounds,
        // để tránh WinForms scale lại khi tọa độ rơi vào màn hình có DPI khác.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
            _virtualScreen.X, _virtualScreen.Y, _virtualScreen.Width, _virtualScreen.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Đảm bảo overlay nhận được bàn phím (Esc) ngay cả khi app chạy nền.
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hasBeenActive = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Người dùng Alt+Tab / bấm sang nơi khác => hủy để overlay không kẹt trên màn hình.
        if (_hasBeenActive && !_finished)
            Cancel();
    }

    // ------------------------------------------------------------------
    //  Chặn WM_DPICHANGED: không cho WinForms resize overlay
    // ------------------------------------------------------------------
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_DPICHANGED)
            return;
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------
    //  Vẽ
    // ------------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Cố ý để trống: toàn bộ việc vẽ nằm trong OnPaint (chống flicker)
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        if (clip.IsEmpty) return;

        // 1) Vẽ lại phần ảnh gốc trong vùng cần cập nhật (sao chép 1:1, không nội suy)
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_screenShot, clip, clip, GraphicsUnit.Pixel);

        // 2) Phủ lớp mờ tối lên toàn bộ vùng
        g.CompositingMode = CompositingMode.SourceOver;
        g.FillRectangle(_dimBrush, clip);

        if (_dragging && _selection.Width > 0 && _selection.Height > 0)
        {
            // 3) Vùng chọn: vẽ lại ảnh gốc SÁNG (không bị phủ mờ)
            var inner = Rectangle.Intersect(_selection, clip);
            if (!inner.IsEmpty)
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(_screenShot, inner, inner, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
            }

            // 4) Viền + nhãn kích thước
            g.DrawRectangle(_borderPen, _selection.X, _selection.Y,
                            _selection.Width - 1, _selection.Height - 1);
            DrawSizeLabel(g);
        }
        else if (!_dragging)
        {
            DrawHint(g);
        }
    }

    private void DrawSizeLabel(Graphics g)
    {
        string text = $"{_selection.Width} × {_selection.Height}";
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var size = g.MeasureString(text, _labelFont);
        int w = (int)size.Width + 8, h = (int)size.Height + 2;

        int x = _selection.X;
        int y = _selection.Y - h - 4;
        if (y < 0) y = _selection.Y + 4;                     // sát mép trên => đưa nhãn vào trong
        if (x + w > ClientSize.Width) x = ClientSize.Width - w;

        g.FillRectangle(_labelBackBrush, x, y, w, h);
        g.DrawString(text, _labelFont, _labelTextBrush, x + 4, y + 1);
    }

    private void DrawHint(Graphics g)
    {
        const string text = "Kéo chuột để chọn vùng cần OCR   •   Esc / chuột phải: hủy";
        var primary = Screen.PrimaryScreen?.Bounds ?? _virtualScreen;
        var size = g.MeasureString(text, _hintFont);
        float x = primary.X - _virtualScreen.X + (primary.Width - size.Width) / 2f;
        float y = primary.Y - _virtualScreen.Y + 40;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.FillRectangle(_labelBackBrush, x - 12, y - 6, size.Width + 24, size.Height + 12);
        g.DrawString(text, _hintFont, _labelTextBrush, x, y);
    }

    // ------------------------------------------------------------------
    //  Chuột & bàn phím
    // ------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;

        _dragging = true;
        _startPoint = ClampToClient(e.Location);
        _selection = new Rectangle(_startPoint, Size.Empty);
        Capture = true;   // nhận MouseMove/MouseUp kể cả khi con trỏ ra ngoài form
        Invalidate();     // xóa dòng gợi ý
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var old = _selection;
        _selection = MakeRectangle(_startPoint, ClampToClient(e.Location));

        // Chỉ vẽ lại vùng bị thay đổi (hợp của vùng cũ + mới + chỗ đặt nhãn) => mượt, không giật
        var dirty = Rectangle.Union(old, _selection);
        dirty.Inflate(120, 34);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;

        _dragging = false;
        Capture = false;

        var rect = MakeRectangle(_startPoint, ClampToClient(e.Location));
        Bitmap? result = null;

        try
        {
            if (rect.Width >= MinSelectionSize && rect.Height >= MinSelectionSize)
            {
                // Cắt vùng từ ảnh đóng băng (tọa độ client == tọa độ pixel của ảnh vì form phủ đúng virtual screen)
                result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(result);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(_screenShot, new Rectangle(0, 0, rect.Width, rect.Height),
                            rect, GraphicsUnit.Pixel);
            }
        }
        catch
        {
            result?.Dispose();
            result = null;
            Finish(null);
            throw; // tầng trên sẽ báo lỗi bằng bong bóng
        }

        Finish(result);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------------
    //  Kết thúc & dọn dẹp
    // ------------------------------------------------------------------

    private void Cancel() => Finish(null);

    /// <summary>Ẩn form NGAY, trả kết quả cho người gọi, rồi đóng (Close ⇒ Dispose) để giải phóng GDI+.</summary>
    private void Finish(Bitmap? result)
    {
        if (_finished)
        {
            result?.Dispose();
            return;
        }
        _finished = true;

        Hide();                    // biến mất tức thì
        _tcs.TrySetResult(result); // trả ảnh (hoặc null nếu hủy)
        Close();                   // form được Show() không-modal ⇒ Close() tự gọi Dispose()
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tcs.TrySetResult(null); // phòng hờ: đảm bảo Task luôn hoàn tất
            _screenShot?.Dispose();
            DisposeDrawingResources();
        }
        base.Dispose(disposing);
    }

    private void DisposeDrawingResources()
    {
        _dimBrush.Dispose();
        _labelBackBrush.Dispose();
        _labelTextBrush.Dispose();
        _borderPen.Dispose();
        _labelFont.Dispose();
        _hintFont.Dispose();
    }

    // ------------------------------------------------------------------
    //  Tiện ích
    // ------------------------------------------------------------------

    private Point ClampToClient(Point p) => new(
        Math.Max(0, Math.Min(ClientSize.Width - 1, p.X)),
        Math.Max(0, Math.Min(ClientSize.Height - 1, p.Y)));

    private static Rectangle MakeRectangle(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X) + 1;
        int h = Math.Abs(a.Y - b.Y) + 1;
        return new Rectangle(x, y, w, h);
    }
}