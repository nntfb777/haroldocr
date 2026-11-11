using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayOcrSnipper;

/// <summary>
/// Cài đặt người dùng, lưu tại %LOCALAPPDATA%\TrayOcrSnipper\settings.json.
/// Hiện tại gồm phím tắt toàn cục.
/// </summary>
internal sealed class AppSettings
{
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Keys Key { get; set; } = Keys.X;

    // ------------------------------------------------------------------
    //  Thuộc tính suy ra (không lưu vào file)
    // ------------------------------------------------------------------

    /// <summary>Giá trị modifier dùng cho RegisterHotKey.</summary>
    [JsonIgnore]
    public uint Modifiers =>
        (Ctrl ? NativeMethods.MOD_CONTROL : 0u) |
        (Alt ? NativeMethods.MOD_ALT : 0u) |
        (Shift ? NativeMethods.MOD_SHIFT : 0u);

    /// <summary>Chuỗi hiển thị, ví dụ "Ctrl + Alt + X".</summary>
    [JsonIgnore]
    public string DisplayText => BuildText(Ctrl, Alt, Shift, Key);

    /// <summary>Hợp lệ khi có phím chính (không phải chỉ riêng phím Ctrl/Alt/Shift/Windows). Cho phép dùng 1 phím đơn.</summary>
    [JsonIgnore]
    public bool IsValid => !IsModifierKey(Key);

    /// <summary>
    /// True nếu là phím đơn (không modifier) mà bình thường dùng để gõ chữ/số/điều hướng:
    /// khi đăng ký làm hotkey toàn cục, phím đó sẽ không còn dùng được ở ứng dụng khác.
    /// </summary>
    [JsonIgnore]
    public bool IsRiskyWithoutModifier => !(Ctrl || Alt || Shift) && !IsSafeSingleKey(Key);

    /// <summary>Các phím đơn ít khi dùng để gõ: F1–F24, PrintScreen, Pause, ScrollLock.</summary>
    public static bool IsSafeSingleKey(Keys key) =>
        IsFunctionKey(key) || key is Keys.PrintScreen or Keys.Pause or Keys.Scroll;

    // ------------------------------------------------------------------
    //  Tiện ích tĩnh
    // ------------------------------------------------------------------

    public static AppSettings Default => new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static bool IsFunctionKey(Keys key) => key >= Keys.F1 && key <= Keys.F24;

    public static bool IsModifierKey(Keys key) => key is
        Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or
        Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    /// <summary>Tạo chuỗi hiển thị; nếu key là phím modifier (hoặc None) thì chỉ hiện phần modifier + "…".</summary>
    public static string BuildText(bool ctrl, bool alt, bool shift, Keys key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(IsModifierKey(key) ? "…" : KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys key)
    {
        string s = key.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString(); // D1 → 1
        return s switch
        {
            "Oemtilde" => "`",
            "OemMinus" => "-",
            "Oemplus" => "=",
            "OemOpenBrackets" => "[",
            "Oem6" => "]",
            "Oem5" => "\\",
            "Oem1" => ";",
            "Oem7" => "'",
            "Oemcomma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            _ => s
        };
    }

    // ------------------------------------------------------------------
    //  Đọc / ghi file
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayOcrSnipper", "settings.json");

    /// <summary>Đọc cài đặt; nếu file không có / hỏng / không hợp lệ thì trả về mặc định (Ctrl + Alt + X).</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null && loaded.IsValid)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "AppSettings.Load");
        }
        return Default;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}