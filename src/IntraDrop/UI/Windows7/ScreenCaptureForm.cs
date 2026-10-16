using System.Drawing;
using System.Windows.Forms;

namespace IntraDrop.UI;

internal sealed class ScreenCaptureForm : Form
{
    private const string Instructions = "마우스로 영역을 드래그하세요. Esc 또는 오른쪽 클릭: 취소";
    private readonly Bitmap _desktop;
    private readonly Point _hintLocation;
    private Point _start;
    private bool _dragging;

    internal Rectangle Selection { get; private set; }

    public ScreenCaptureForm(Bitmap desktop, Rectangle bounds)
    {
        _desktop = desktop;
        Text = "IntraDrop 화면 캡처";
        AccessibleName = Text;
        AccessibleDescription = Instructions;
        AutoScaleMode = AutoScaleMode.None; // Snapshot and mouse coordinates are physical pixels on Windows 7.
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        var monitor = Screen.FromPoint(Cursor.Position).Bounds;
        _hintLocation = new Point(monitor.Left - bounds.Left + 24, monitor.Top - bounds.Top + 24);
    }

    internal static Rectangle GetSelection(Point start, Point end, Size size)
    {
        int left = Math.Max(0, Math.Min(size.Width, Math.Min(start.X, end.X)));
        int top = Math.Max(0, Math.Min(size.Height, Math.Min(start.Y, end.Y)));
        int right = Math.Max(0, Math.Min(size.Width, Math.Max(start.X, end.X)));
        int bottom = Math.Max(0, Math.Min(size.Height, Math.Max(start.Y, end.Y)));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_desktop, Point.Empty);
        using var shade = new SolidBrush(Color.FromArgb(110, Color.Black));
        e.Graphics.FillRectangle(shade, ClientRectangle);
        if (Selection.Width > 0 && Selection.Height > 0)
        {
            e.Graphics.DrawImage(_desktop, Selection, Selection, GraphicsUnit.Pixel);
            e.Graphics.DrawRectangle(Pens.White, Selection.X, Selection.Y, Selection.Width - 1, Selection.Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, Instructions, SystemFonts.MessageBoxFont, _hintLocation,
            Color.White, Color.FromArgb(40, 40, 40));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { CancelCapture(); return; }
        if (e.Button != MouseButtons.Left) return;
        _start = e.Location;
        Selection = Rectangle.Empty;
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Selection = GetSelection(_start, e.Location, _desktop.Size);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging) return;
        Selection = GetSelection(_start, e.Location, _desktop.Size);
        _dragging = false;
        Capture = false;
        DialogResult = Selection.Width > 0 && Selection.Height > 0 ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        CancelCapture();
        return true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (DialogResult == DialogResult.None) CancelCapture();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture) CancelCapture();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x007E) CancelCapture(); // Display layout changed; the snapshot no longer matches.
        base.WndProc(ref m);
    }

    private void CancelCapture()
    {
        _dragging = false;
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
