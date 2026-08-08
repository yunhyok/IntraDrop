namespace IntraDrop.UI;

/// <summary>고해상도(DPI) 대응 공통 UI 헬퍼.
/// 모든 폼은 96DPI 기준으로 설계하고 AutoScaleMode.Dpi 로 배율에 맞춰 확대한다.
/// 글자가 커져도 잘리지 않도록 컨트롤은 가급적 AutoSize 로 만든다.</summary>
internal static class UiKit
{
    public static void ApplyDpiScaling(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
    }

    public static Button DialogButton(string text, DialogResult? result = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(88, 30),
            Padding = new Padding(8, 2, 8, 2),
        };
        if (result.HasValue) b.DialogResult = result.Value;
        return b;
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 10, 8),
    };

    public static Label HintLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 0, 3, 6),
    };

    public static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 14, 3, 3),
        };
        panel.Controls.AddRange(buttons);
        return panel;
    }
}
