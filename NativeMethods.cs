using System.Runtime.InteropServices;

namespace TrayOcrSnipper;

/// <summary>
/// Tập trung toàn bộ khai báo P/Invoke (Win32 API) vào một chỗ.
/// </summary>
internal static class NativeMethods
{
    // ---- Modifier của RegisterHotKey ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    /// <summary>Giữ phím không bắn WM_HOTKEY liên tục (tránh mở nhiều overlay).</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- Window messages ----
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;

    // ---- Window styles ----
    public const int WS_EX_TOOLWINDOW = 0x00000080; // không hiện trong Alt+Tab

    // ---- SetWindowPos ----
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    /// <summary>Parent đặc biệt để tạo "message-only window" (cửa sổ ẩn hoàn toàn, chỉ nhận message).</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
