using IntraDrop.Models;

namespace IntraDrop.UI;

public class PeerEditForm : Form
{
    private readonly TextBox _nickname;
    private readonly TextBox _host;

    public PeerInfo? Result { get; private set; }

    public PeerEditForm(PeerInfo? existing)
    {
        Text = existing == null ? "컴퓨터 등록" : "컴퓨터 수정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UiKit.ApplyDpiScaling(this);

        _nickname = new TextBox { Width = 240, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
        _host = new TextBox { Width = 240, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };

        var ok = UiKit.DialogButton("확인", DialogResult.OK);
        ok.Click += (_, _) => ValidateInput();
        var cancel = UiKit.DialogButton("취소", DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(14),
        };
        layout.Controls.Add(UiKit.FieldLabel("별명:"), 0, 0);
        layout.Controls.Add(_nickname, 1, 0);
        layout.Controls.Add(UiKit.FieldLabel("주소(IP):"), 0, 1);
        layout.Controls.Add(_host, 1, 1);

        var buttons = UiKit.ButtonRow(ok, cancel);
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        if (existing != null)
        {
            ExistingHost = existing.Host;
            ExistingDeviceId = existing.DeviceId;
            _nickname.Text = existing.Nickname;
            _host.Text = existing.Host;
        }
    }

    private void ValidateInput()
    {
        string host = _host.Text.Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "주소(IP 또는 컴퓨터 이름)를 입력하세요.", "IntraDrop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        string nickname = _nickname.Text.Trim();
        if (nickname.Length == 0) nickname = host;

        Result = new PeerInfo { Nickname = nickname, Host = host, DeviceId = ExistingDeviceId ?? "" };
    }

    private string? ExistingHost { get; set; }
    private string? ExistingDeviceId { get; set; }
}
