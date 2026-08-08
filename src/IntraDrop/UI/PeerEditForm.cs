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
        ClientSize = new Size(340, 150);
        MaximizeBox = false;
        MinimizeBox = false;

        var lblNick = new Label { Text = "별명:", Location = new Point(16, 20), AutoSize = true };
        _nickname = new TextBox { Location = new Point(110, 16), Width = 210 };

        var lblHost = new Label { Text = "주소(IP):", Location = new Point(16, 56), AutoSize = true };
        _host = new TextBox { Location = new Point(110, 52), Width = 210 };

        var ok = new Button
        {
            Text = "확인",
            DialogResult = DialogResult.OK,
            Location = new Point(156, 104),
            Size = new Size(80, 28),
        };
        ok.Click += (_, e) => Validate(e);

        var cancel = new Button
        {
            Text = "취소",
            DialogResult = DialogResult.Cancel,
            Location = new Point(242, 104),
            Size = new Size(80, 28),
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { lblNick, _nickname, lblHost, _host, ok, cancel });

        if (existing != null)
        {
            _nickname.Text = existing.Nickname;
            _host.Text = existing.Host;
        }
    }

    private void Validate(EventArgs e)
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

        Result = new PeerInfo { Nickname = nickname, Host = host };
    }
}
