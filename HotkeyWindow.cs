using System.ComponentModel;

namespace TrayOcrSnipper;

/// <summary>
/// Cửa sổ "message-only" ẩn hoàn toàn, chỉ để nhận WM_HOTKEY từ RegisterHotKey.
/// - Không có giao diện, không hiện trên taskbar / Alt+Tab.
/// - RegisterHotKey do hệ điều hành xử lý nên không "nuốt" phím và không làm chậm hệ thống
///   (khác với low-level keyboard hook phải chạy code cho mọi phím bấm).
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x4F43; // ID tùy ý, duy nhất trong cửa sổ này

    private readonly SynchronizationContext _uiContext;
    private bool _registered;
    private bool _disposed;

    /// <summary>Bắn khi người dùng bấm tổ hợp phím đã đăng ký (luôn chạy trên luồng UI).</summary>
    public event Action? HotkeyPressed;

    public HotkeyWindow()
    {
        // Lấy SynchronizationContext của luồng UI để Post sự kiện, giúp WndProc trả về ngay lập tức.
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // Parent = HWND_MESSAGE => message-only window
        CreateHandle(new CreateParams { Parent = NativeMethods.HWND_MESSAGE });
    }

    /// <summary>Đăng ký hotkey. Trả về false nếu tổ hợp phím đã bị ứng dụng khác chiếm.</summary>
    public bool Register(uint modifiers, Keys key, out string? error)
    {
        error = null;
        if (_registered) Unregister();

        // MOD_NOREPEAT: giữ phím không sinh ra nhiều sự kiện liên tiếp
        _registered = NativeMethods.RegisterHotKey(
            Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, (uint)key);

        if (!_registered)
            error = new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;

        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            // Post thay vì gọi trực tiếp: WndProc thoát ngay, việc nặng chạy ở vòng message kế tiếp.
            _uiContext.Post(_ => HotkeyPressed?.Invoke(), null);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unregister();
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
