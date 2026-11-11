namespace TrayOcrSnipper;

internal static class Program
{
    private static TrayApplicationContext? _context;

    [STAThread] // Bắt buộc: WinForms + Clipboard cần luồng STA
    private static void Main()
    {
        // Chỉ cho phép 1 phiên bản chạy (tránh đăng ký hotkey trùng)
        using var mutex = new Mutex(initiallyOwned: true, @"Local\TrayOcrSnipper.SingleInstance", out bool isNew);
        if (!isNew) return;

        // Bắt lỗi chưa xử lý của luồng UI → không crash app. Phải gọi TRƯỚC khi tạo bất kỳ control nào.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Logger.Log(e.Exception, "Application.ThreadException");
            _context?.ShowError($"Lỗi không mong muốn: {e.Exception.Message}");
        };

        // Lỗi ở luồng khác / Task không được await
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Logger.Log(ex, "AppDomain.UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Log(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        // DPI đã được khai báo trong app.manifest (PerMonitorV2),
        // nên KHÔNG gọi ApplicationConfiguration.Initialize()/SetHighDpiMode để tránh xung đột.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Cài SynchronizationContext của WinForms để await luôn quay lại luồng UI
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        try
        {
            _context = new TrayApplicationContext();
            Application.Run(_context);   // ApplicationContext thay cho Form chính → không có cửa sổ nào khi khởi động
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Main");
            MessageBox.Show($"Không thể khởi động ứng dụng:\n{ex.Message}", "Tray OCR Snipper",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _context?.Dispose();
        }
    }
}
