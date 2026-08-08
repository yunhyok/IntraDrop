using IntraDrop.Models;

namespace IntraDrop.UI;

public class SettingsForm : Form
{
    private readonly TextBox _deviceName;
    private readonly TextBox _folder;
    private readonly ComboBox _threshold;
    private readonly CheckBox _autoStart;
    private readonly NumericUpDown _port;

    private static readonly (string Label, int MB)[] ThresholdOptions =
    {
        ("256 MB", 256),
        ("512 MB", 512),
        ("1 GB", 1024),
    };

    public SettingsForm(AppSettings settings)
    {
        Text = "IntraDrop 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 258);
        MaximizeBox = false;
        MinimizeBox = false;

        int y = 18;

        var lblName = new Label { Text = "내 장치 이름:", Location = new Point(16, y + 4), AutoSize = true };
        _deviceName = new TextBox { Location = new Point(150, y), Width = 260, Text = settings.DeviceName };
        y += 36;

        var lblFolder = new Label { Text = "다운로드 폴더:", Location = new Point(16, y + 4), AutoSize = true };
        _folder = new TextBox { Location = new Point(150, y), Width = 190, Text = settings.DownloadFolder };
        var browse = new Button { Text = "찾아보기...", Location = new Point(346, y - 1), Size = new Size(64, 25) };
        browse.Click += (_, _) => BrowseFolder();
        y += 36;

        var lblThreshold = new Label { Text = "수락 확인 크기:", Location = new Point(16, y + 4), AutoSize = true };
        _threshold = new ComboBox
        {
            Location = new Point(150, y),
            Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var (label, _) in ThresholdOptions) _threshold.Items.Add(label);
        int idx = Array.FindIndex(ThresholdOptions, o => o.MB == settings.ConfirmThresholdMB);
        _threshold.SelectedIndex = idx >= 0 ? idx : 0;
        var lblThresholdHint = new Label
        {
            Text = "이 크기 이상은 받는 쪽에서 수락해야 합니다.",
            Location = new Point(150, y + 26),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        y += 56;

        var lblPort = new Label { Text = "포트:", Location = new Point(16, y + 4), AutoSize = true };
        _port = new NumericUpDown
        {
            Location = new Point(150, y),
            Width = 90,
            Minimum = 1024,
            Maximum = 65535,
            Value = Math.Clamp(settings.Port, 1024, 65535),
        };
        var lblPortHint = new Label
        {
            Text = "모든 컴퓨터가 같은 포트를 사용해야 합니다.",
            Location = new Point(248, y + 4),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        y += 36;

        _autoStart = new CheckBox
        {
            Text = "Windows 시작 시 자동 실행",
            Location = new Point(150, y),
            AutoSize = true,
            Checked = settings.AutoStart,
        };
        y += 36;

        var ok = new Button
        {
            Text = "저장",
            DialogResult = DialogResult.OK,
            Location = new Point(246, y),
            Size = new Size(80, 28),
        };
        ok.Click += (_, _) => ValidateInput();
        var cancel = new Button
        {
            Text = "취소",
            DialogResult = DialogResult.Cancel,
            Location = new Point(332, y),
            Size = new Size(80, 28),
        };

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange(new Control[]
        {
            lblName, _deviceName,
            lblFolder, _folder, browse,
            lblThreshold, _threshold, lblThresholdHint,
            lblPort, _port, lblPortHint,
            _autoStart, ok, cancel,
        });
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "받은 파일을 저장할 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folder.Text = dlg.SelectedPath;
    }

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_folder.Text))
        {
            MessageBox.Show(this, "다운로드 폴더를 입력하세요.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        try
        {
            Directory.CreateDirectory(_folder.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"다운로드 폴더를 만들 수 없습니다.\n{ex.Message}", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text)
            ? Environment.MachineName
            : _deviceName.Text.Trim();
        settings.DownloadFolder = _folder.Text.Trim();
        settings.ConfirmThresholdMB = ThresholdOptions[_threshold.SelectedIndex].MB;
        settings.AutoStart = _autoStart.Checked;
        settings.Port = (int)_port.Value;
    }
}
