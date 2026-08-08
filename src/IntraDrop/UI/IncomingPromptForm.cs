using IntraDrop.Core;

namespace IntraDrop.UI;

/// <summary>임계 크기 이상 수신 시 수락/거절을 묻는 대화상자. 60초 후 자동 거절.</summary>
public class IncomingPromptForm : Form
{
    private const int TimeoutSeconds = 60;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly Button _reject;
    private int _remaining = TimeoutSeconds;

    public IncomingPromptForm(string senderName, int fileCount, long totalSize)
    {
        Text = "IntraDrop - 파일 수신 요청";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UiKit.ApplyDpiScaling(this);

        string sender = string.IsNullOrWhiteSpace(senderName) ? "알 수 없는 컴퓨터" : senderName;

        var message = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),   // 이 폭에서 자동 줄바꿈
            Text = $"{sender} 님이 큰 파일을 보내려고 합니다.\n\n" +
                   $"파일 {fileCount}개, 총 {Protocol.FormatSize(totalSize)}",
            Font = new Font(Font.FontFamily, 9.5f),
            Margin = new Padding(6, 6, 6, 6),
        };

        var accept = UiKit.DialogButton("수락", DialogResult.Yes);
        _reject = UiKit.DialogButton($"거절 ({_remaining})", DialogResult.No);
        AcceptButton = accept;
        CancelButton = _reject;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
        };
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(UiKit.ButtonRow(accept, _reject), 0, 1);
        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.No;
                Close();
            }
            else
            {
                _reject.Text = $"거절 ({_remaining})";
            }
        };
        _timer.Start();

        FormClosed += (_, _) => _timer.Dispose();
    }
}
