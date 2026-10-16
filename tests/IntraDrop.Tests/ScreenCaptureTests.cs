using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ScreenCaptureTests
{
    [Theory]
    [InlineData(PlatformID.Win32NT, 6, 1, true)]
    [InlineData(PlatformID.Win32NT, 6, 0, false)]
    [InlineData(PlatformID.Win32NT, 6, 2, false)]
    [InlineData(PlatformID.Win32NT, 6, 3, false)]
    [InlineData(PlatformID.Win32NT, 10, 0, false)]
    [InlineData(PlatformID.Unix, 6, 1, false)]
    public void OnlyWindows7EnablesCapture(PlatformID platform, int major, int minor, bool expected) =>
        Assert.Equal(expected, ScreenCaptureService.Supports(new OperatingSystem(platform, new Version(major, minor))));

    [Fact]
    public void ModernApplicationDoesNotContainCaptureImplementation()
    {
        var app = typeof(TrayApplicationContext).Assembly;
        Assert.Null(app.GetType("IntraDrop.UI.ScreenCaptureService"));
        Assert.Null(app.GetType("IntraDrop.UI.ScreenCaptureForm"));
        Assert.Null(typeof(TrayApplicationContext).GetField("_screenCapture", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Theory]
    [InlineData(10, 20, 80, 70, 10, 20, 70, 50)]
    [InlineData(80, 70, 10, 20, 10, 20, 70, 50)]
    [InlineData(10, 70, 80, 20, 10, 20, 70, 50)]
    [InlineData(80, 20, 10, 70, 10, 20, 70, 50)]
    [InlineData(-10, -20, 150, 130, 0, 0, 100, 80)]
    [InlineData(10, 20, 10, 20, 10, 20, 0, 0)]
    [InlineData(10, 20, 10, 70, 10, 20, 0, 50)]
    [InlineData(-20, 20, -10, 70, 0, 20, 0, 50)]
    public void DragBoundsAreNormalizedAndClamped(int x1, int y1, int x2, int y2,
        int x, int y, int width, int height) =>
        Assert.Equal(new Rectangle(x, y, width, height),
            ScreenCaptureForm.GetSelection(new Point(x1, y1), new Point(x2, y2), new Size(100, 80)));

    [Fact]
    public Task SelectionUsesOriginalPixelsAcrossNegativeMonitorCoordinates() => OnSta(() =>
    {
        using var desktop = new Bitmap(400, 200);
        using (var graphics = Graphics.FromImage(desktop)) graphics.Clear(Color.CornflowerBlue);
        desktop.SetPixel(150, 60, Color.Red);
        var bounds = new Rectangle(-200, -100, 400, 200);
        using var form = new ScreenCaptureForm(desktop, bounds);
        Assert.Equal(bounds, form.Bounds);
        Assert.Equal(AutoScaleMode.None, form.AutoScaleMode);

        // Simulate an in-progress drag without taking mouse capture from the user's desktop.
        SetField(form, "_start", new Point(300, 150));
        SetField(form, "_dragging", true);
        Invoke(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 100, 50, 0));
        Assert.Equal(new Rectangle(100, 50, 200, 100), form.Selection);
        using var painted = new Bitmap(400, 200);
        using (var graphics = Graphics.FromImage(painted))
            Invoke(form, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, painted.Size)));
        Assert.Equal(Color.Red.ToArgb(), painted.GetPixel(150, 60).ToArgb());
        Assert.NotEqual(desktop.GetPixel(10, 10).ToArgb(), painted.GetPixel(10, 10).ToArgb());

        Invoke(form, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 0, 100, 50, 0));
        Assert.Equal(DialogResult.OK, form.DialogResult);
        using var cropped = desktop.Clone(form.Selection, desktop.PixelFormat);
        Assert.Equal(new Size(200, 100), cropped.Size);
        Assert.Equal(Color.Red.ToArgb(), cropped.GetPixel(50, 10).ToArgb());
        var data = new DataObject();
        data.SetImage(cropped);
        Assert.Equal("png", ClipboardService.CaptureAutomatic(data)?.Format);
    });

    [Fact]
    public Task EmptySelectionAndCancellationNeverAcceptCapture() => OnSta(() =>
    {
        using var desktop = new Bitmap(100, 80);
        var bounds = new Rectangle(Point.Empty, desktop.Size);
        using var empty = new ScreenCaptureForm(desktop, bounds);
        SetField(empty, "_start", new Point(20, 20));
        SetField(empty, "_dragging", true);
        Invoke(empty, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 0, 20, 60, 0));
        Assert.Equal(DialogResult.Cancel, empty.DialogResult);

        using var rightClick = new ScreenCaptureForm(desktop, bounds);
        Invoke(rightClick, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 10, 10, 0));
        Assert.Equal(DialogResult.Cancel, rightClick.DialogResult);

        using var escape = new ScreenCaptureForm(desktop, bounds);
        var key = Message.Create(IntPtr.Zero, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
        Assert.Equal(true, Invoke(escape, "ProcessCmdKey", key, Keys.Escape));
        Assert.Equal(DialogResult.Cancel, escape.DialogResult);

        using var deactivated = new ScreenCaptureForm(desktop, bounds);
        Invoke(deactivated, "OnDeactivate", EventArgs.Empty);
        Assert.Equal(DialogResult.Cancel, deactivated.DialogResult);

        using var lostCapture = new ScreenCaptureForm(desktop, bounds);
        SetField(lostCapture, "_dragging", true);
        Invoke(lostCapture, "OnMouseCaptureChanged", EventArgs.Empty);
        Assert.Equal(DialogResult.Cancel, lostCapture.DialogResult);
    });

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public Task DesktopCaptureReadsPixelsWithoutChangingClipboard() => OnSta(() =>
    {
        using var image = ScreenCaptureService.CaptureDesktop(
            new Rectangle(SystemInformation.VirtualScreen.Location, new Size(2, 2)));
        Assert.Equal(new Size(2, 2), image.Size);
        Assert.Equal(255, image.GetPixel(0, 0).A);
    });

    private static object? Invoke(ScreenCaptureForm form, string method, params object[] args) =>
        typeof(ScreenCaptureForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);

    private static void SetField(ScreenCaptureForm form, string name, object value) =>
        typeof(ScreenCaptureForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);

    private static async Task OnSta(Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(true); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
