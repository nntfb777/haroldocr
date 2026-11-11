using System.Drawing.Drawing2D;

namespace TrayOcrSnipper;

/// <summary>
/// "Bộ não" của ứng dụng: không có Form chính, chỉ có NotifyIcon + cửa sổ ẩn nhận hotkey.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly OcrService _ocr = new();

    private AppSettings _settings;    // phím tắt hiện hành (đọc từ settings.json)
    private bool _busy;               // chặn bấm hotkey chồng khi đang xử lý
    private bool _settingsOpen;       // chỉ cho mở 1 hộp thoại đổi phím tắt
    private bool _fallbackWarned;     // chỉ cảnh báo dùng OCR dự phòng 1 lần
    private bool _cleanedUp;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        // --- Menu chuột phải ---
        var menu = new ContextMenuStrip();
        menu.Items.Add("Thông tin phím tắt", null, (_, _) => ShowHotkeyInfo());
        menu.Items.Add("Đổi phím tắt…", null, (_, _) => ChangeHotkey());
        menu.Items.Add("Tải dữ liệu OCR tiếng Việt", null, async (_, _) => await PrepareVietnameseOcrAsync(userInitiated: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thoát", null, (_, _) => ExitApplication());

        // --- Icon khay hệ thống ---
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        UpdateTrayText();

        // --- Hotkey toàn cục ---
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;

        if (_hotkeyWindow.Register(_settings.Modifiers, _settings.Key, out string? error))
        {
            ShowInfo("Đang chạy ngầm", $"Bấm {_settings.DisplayText} để chọn vùng màn hình và OCR.");
        }
        else
        {
            ShowError($"Không đăng ký được phím tắt {_settings.DisplayText} (có thể đã bị ứng dụng khác dùng). " +
                      $"Chuột phải icon → 'Đổi phím tắt…' để chọn tổ hợp khác. {error}");
        }

        // Tự tải dữ liệu OCR tiếng Việt ở nền nếu chưa có (chỉ lần đầu)
        _ = PrepareVietnameseOcrAsync(userInitiated: false);
    }

    private void UpdateTrayText()
    {
        // NotifyIcon.Text tối đa 63 ký tự
        string text = $"OCR chọn vùng ({_settings.DisplayText})";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    // ------------------------------------------------------------------
    //  Dữ liệu OCR tiếng Việt (Tesseract)
    // ------------------------------------------------------------------

    private async Task PrepareVietnameseOcrAsync(bool userInitiated)
    {
        try
        {
            if (_ocr.IsTesseractDataInstalled)
            {
                if (userInitiated)
                    ShowInfo("OCR tiếng Việt đã sẵn sàng", "Dữ liệu Tesseract (tiếng Việt + Anh) đã có trên máy.");
                return;
            }

            if (_ocr.IsDownloading)
            {
                if (userInitiated)
                    ShowInfo("Đang tải", "Dữ liệu OCR tiếng Việt đang được tải, vui lòng chờ.");
                return;
            }

            ShowInfo("Đang tải dữ liệu OCR tiếng Việt",
                     "Tải lần đầu (~30 MB), vui lòng chờ. Trong lúc này OCR chỉ dùng được tiếng Anh.");

            await _ocr.EnsureVietnameseDataAsync();

            _fallbackWarned = false;
            ShowInfo("Đã tải xong", "OCR tiếng Việt (Tesseract) đã sẵn sàng.");
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "PrepareVietnameseOcrAsync");
            ShowError($"Không tải được dữ liệu OCR tiếng Việt ({ex.Message}). " +
                      "Kiểm tra Internet rồi chọn lại 'Tải dữ liệu OCR tiếng Việt' ở menu.");
        }
    }

    // ------------------------------------------------------------------
    //  Đổi phím tắt
    // ------------------------------------------------------------------

    private void ChangeHotkey()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;

        bool applied = false;

        // Gỡ hotkey cũ khi mở hộp thoại: nếu không, bấm đúng tổ hợp đang dùng sẽ kích hoạt OCR
        // thay vì được ô nhập ghi nhận.
        _hotkeyWindow.Unregister();

        try
        {
            AppSettings working = _settings.Clone();
            string? message = null;

            while (true)
            {
                using var form = new HotkeySettingsForm(working, message);
                if (form.ShowDialog() != DialogResult.OK)
                    break; // người dùng hủy

                AppSettings chosen = form.Result;

                if (_hotkeyWindow.Register(chosen.Modifiers, chosen.Key, out _))
                {
                    _settings = chosen;
                    applied = true;

                    try
                    {
                        _settings.Save();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, "AppSettings.Save");
                        ShowError("Đã đổi phím tắt nhưng không lưu được vào file cài đặt, lần sau sẽ về mặc định.");
                    }

                    UpdateTrayText();
                    ShowInfo("Đã đổi phím tắt", $"Phím tắt mới: {_settings.DisplayText}");
                    return;
                }

                // Tổ hợp bị ứng dụng khác chiếm → mở lại hộp thoại để chọn tổ hợp khác
                working = chosen;
                message = $"Không dùng được {chosen.DisplayText} (đã bị ứng dụng khác hoặc Windows sử dụng). " +
                          "Hãy chọn tổ hợp khác.";
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "ChangeHotkey");
            ShowError($"Không đổi được phím tắt: {ex.Message}");
        }
        finally
        {
            // Hủy hoặc lỗi → khôi phục phím tắt cũ
            if (!applied && !_cleanedUp)
            {
                if (!_hotkeyWindow.Register(_settings.Modifiers, _settings.Key, out string? err))
                    ShowError($"Không đăng ký lại được phím tắt {_settings.DisplayText}. {err}");
            }
            _settingsOpen = false;
        }
    }

    // ------------------------------------------------------------------
    //  Luồng chính: Hotkey → chọn vùng → OCR → Clipboard
    // ------------------------------------------------------------------

    // async void chỉ chấp nhận được ở event handler; mọi exception đều được bắt trong try/catch bên dưới.
    private async void OnHotkeyPressed()
    {
        if (_busy || _settingsOpen) return;
        _busy = true;

        Bitmap? shot = null;
        try
        {
            // 1) Chọn vùng (trả null nếu người dùng hủy)
            shot = await SnippingForm.CaptureAsync();
            if (shot == null) return;

            // 2) OCR (xử lý ảnh chạy nền, không chặn UI)
            OcrOutput result = await _ocr.RecognizeAsync(shot);

            // Cảnh báo 1 lần nếu phải dùng OCR dự phòng (không có tiếng Việt)
            if (!result.IsVietnamese && !_fallbackWarned)
            {
                _fallbackWarned = true;
                ShowInfo("Đang dùng OCR dự phòng (không có tiếng Việt)",
                    $"Đang dùng '{result.LanguageTag}'. Chọn 'Tải dữ liệu OCR tiếng Việt' ở menu khay " +
                    "để nhận dạng tiếng Việt có dấu.");
            }

            // 3) Làm sạch văn bản
            string text = TextCleaner.Clean(result.Text);
            if (text.Length == 0)
            {
                ShowInfo("Không nhận dạng được chữ", "Vùng đã chọn không chứa văn bản đọc được.");
                return;
            }

            // 4) Ghi Clipboard (có retry khi bị khóa)
            await ClipboardHelper.SetTextAsync(text);

            string preview = text.Replace("\r\n", " ").Replace('\n', ' ');
            if (preview.Length > 80) preview = preview[..80] + "…";
            ShowInfo($"Đã sao chép {text.Length} ký tự", preview);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "OnHotkeyPressed");
            ShowError(FriendlyMessage(ex));
        }
        finally
        {
            shot?.Dispose();
            _busy = false;
        }
    }

    // ------------------------------------------------------------------
    //  Thông báo (bong bóng khay hệ thống)
    // ------------------------------------------------------------------

    public void ShowInfo(string title, string message) => Balloon(title, message, ToolTipIcon.Info);

    public void ShowError(string message) => Balloon("Lỗi OCR", message, ToolTipIcon.Error);

    private void Balloon(string title, string message, ToolTipIcon icon)
    {
        try
        {
            // Giới hạn độ dài của Windows: title 63 ký tự, nội dung 255 ký tự
            if (title.Length > 60) title = title[..60];
            if (message.Length > 250) message = message[..250] + "…";

            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Balloon");
        }
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception => "Không chụp được màn hình (có thể thiếu quyền hoặc màn hình đang khóa).",
        InvalidOperationException => ex.Message,
        IOException => ex.Message,
        OutOfMemoryException => "Hết bộ nhớ khi xử lý ảnh. Hãy chọn vùng nhỏ hơn.",
        _ => $"Đã xảy ra lỗi: {ex.Message}"
    };

    private void ShowHotkeyInfo()
    {
        MessageBox.Show(
            $"{_settings.DisplayText}\tChọn vùng màn hình và OCR → chép vào Clipboard\n" +
            "Esc / Chuột phải\tHủy chọn vùng\n\n" +
            "Đổi phím tắt: chuột phải icon khay → 'Đổi phím tắt…'\n\n" +
            _ocr.GetStatusText() + "\n\n" +
            $"Dữ liệu OCR: {TesseractService.DataDirectory}\n" +
            $"File cài đặt: {AppSettings.FilePath}\n" +
            $"File log lỗi: {Logger.LogFilePath}",
            "Thông tin",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------------
    //  Thoát & dọn dẹp
    // ------------------------------------------------------------------

    private void ExitApplication()
    {
        Cleanup();
        ExitThread();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyWindow.Dispose();      // UnregisterHotKey + hủy cửa sổ ẩn

        _ocr.Dispose();               // giải phóng Tesseract engine (bộ nhớ gốc)

        _notifyIcon.Visible = false;  // tránh icon "ma" còn sót trên khay
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------
    //  Icon vẽ bằng code (không cần file .ico)
    // ------------------------------------------------------------------

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var back = new SolidBrush(Color.FromArgb(0, 120, 215));
            g.FillEllipse(back, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("T", font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();       // bản sao độc lập với handle gốc
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon); // tránh rò rỉ GDI handle
        }
    }
}