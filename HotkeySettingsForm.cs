namespace TrayOcrSnipper;

/// <summary>
/// Hộp thoại đổi phím tắt: người dùng chỉ việc bấm tổ hợp phím mong muốn vào ô nhập.
/// </summary>
internal sealed class HotkeySettingsForm : Form
{
    private readonly HotkeyBox _box = new();
    private readonly Label _status = new();
    private readonly Button _saveButton = new();
    private AppSettings _candidate;

    /// <summary>Phím tắt người dùng đã chọn (hợp lệ khi DialogResult = OK).</summary>
    public AppSettings Result => _candidate;

    public HotkeySettingsForm(AppSettings current, string? initialError = null)
    {
        _candidate = current.Clone();

        // --- Cấu hình cửa sổ ---
        Text = "Đổi phím tắt";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;                       // luôn nổi lên trên vì app chạy ngầm
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // --- Nội dung ---
        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Text = "Bấm vào ô bên dưới rồi nhấn phím hoặc tổ hợp phím bạn muốn dùng " +
                   "(ví dụ F8, Pause hoặc Ctrl + Shift + O).\n" +
                   "Có thể dùng 1 phím đơn. Phím Windows chưa được hỗ trợ."
        };

        _box.Width = 340;
        _box.Margin = new Padding(0, 10, 0, 0);
        _box.Text = current.DisplayText;
        _box.ComboPressed += OnComboPressed;

        _status.AutoSize = true;
        _status.MaximumSize = new Size(340, 0);
        _status.Margin = new Padding(0, 6, 0, 0);
        SetStatus(initialError, isError: true);

        _saveButton.Text = "Lưu";
        _saveButton.AutoSize = true;
        _saveButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _saveButton.Padding = new Padding(10, 2, 10, 2);
        _saveButton.DialogResult = DialogResult.OK;

        var cancelButton = new Button
        {
            Text = "Hủy",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 2, 10, 2),
            DialogResult = DialogResult.Cancel
        };

        var resetButton = new Button
        {
            Text = $"Mặc định ({AppSettings.Default.DisplayText})",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 2, 10, 2)
        };
        resetButton.Click += (_, _) => ApplyCandidate(AppSettings.Default);

        // RightToLeft: control thêm đầu tiên nằm sát phải → thứ tự hiển thị: [Mặc định] [Lưu] [Hủy]
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0),
            Anchor = AnchorStyles.Right
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(resetButton);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(16),
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(intro);
        layout.Controls.Add(_box);
        layout.Controls.Add(_status);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _saveButton;
        CancelButton = cancelButton;
        _saveButton.Enabled = _candidate.IsValid;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _box.Focus();
    }

    // ------------------------------------------------------------------

    private void OnComboPressed(bool ctrl, bool alt, bool shift, Keys key)
    {
        // Mới chỉ giữ phím modifier: hiện trạng thái đang chờ phím chính
        if (AppSettings.IsModifierKey(key))
        {
            _box.Text = AppSettings.BuildText(ctrl, alt, shift, Keys.None);
            _saveButton.Enabled = false;
            SetStatus("Hãy nhấn thêm phím chính…", isError: false);
            return;
        }

        var cand = new AppSettings { Ctrl = ctrl, Alt = alt, Shift = shift, Key = key };
        _box.Text = cand.DisplayText;

        if (!cand.IsValid)
        {
            _saveButton.Enabled = false;
            SetStatus("Hãy nhấn một phím chính (không chỉ riêng Ctrl / Alt / Shift).", isError: true);
            return;
        }

        _candidate = cand;
        _saveButton.Enabled = true;

        if (cand.IsRiskyWithoutModifier)
            SetStatus($"Lưu ý: phím '{cand.DisplayText}' đứng một mình sẽ không còn gõ được ở ứng dụng khác. " +
                      "Nên chọn F1–F12, Pause, PrintScreen hoặc thêm Ctrl/Alt/Shift.",
                      isError: false, isWarning: true);
        else
            SetStatus(null, isError: false);
    }

    private void ApplyCandidate(AppSettings settings)
    {
        _candidate = settings.Clone();
        _box.Text = _candidate.DisplayText;
        _saveButton.Enabled = _candidate.IsValid;
        SetStatus(null, isError: false);
        _box.Focus();
    }

    private void SetStatus(string? text, bool isError, bool isWarning = false)
    {
        _status.Text = text ?? string.Empty;
        _status.ForeColor = isError ? Color.Firebrick
                          : isWarning ? Color.DarkOrange
                          : SystemColors.GrayText;
        _status.Visible = !string.IsNullOrEmpty(text);
    }

    // ------------------------------------------------------------------
    //  Ô nhập "bắt phím"
    // ------------------------------------------------------------------

    private sealed class HotkeyBox : TextBox
    {
        public event Action<bool, bool, bool, Keys>? ComboPressed;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            BackColor = SystemColors.Window;
        }

        // ProcessCmdKey chạy trước mọi xử lý mặc định nên bắt được cả Ctrl+Alt+..., Alt+..., Ctrl+Tab...
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Esc / Enter / Tab đơn lẻ vẫn dùng để đóng hộp thoại / chuyển ô như bình thường
            if (keyData is Keys.Escape or Keys.Enter or Keys.Tab or (Keys.Shift | Keys.Tab))
                return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;

            ComboPressed?.Invoke(
                (mods & Keys.Control) != 0,
                (mods & Keys.Alt) != 0,
                (mods & Keys.Shift) != 0,
                key);

            return true; // đã xử lý, không cho TextBox làm gì thêm
        }
    }
}