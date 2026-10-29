using System.Text;
using IntraDrop.Core;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ClipboardEchoTrackerTests
{
    private static ClipboardContent Text(string value) => new() { Format = "text", Data = Encoding.UTF8.GetBytes(value) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegroundRecopySurvivesDeferredCaptureAndFocusChanges(bool previouslyReceived)
    {
        var tracker = new ClipboardEchoTracker();
        var content = Text("Excel cells");
        if (previouslyReceived) tracker.Applied(content); else tracker.Sent(content);
        tracker.Observe(12, 42, foreground: true); // Ctrl+C in Excel.
        tracker.Observe(12, 42, foreground: false); // Read retries after switching to VNC/Explorer.
        Assert.Equal(ClipboardEcho.None, tracker.Classify(content, 12, 42, background: true));
    }

    [Fact]
    public void BridgeEchoIsSuppressedButADifferentCopyIsSent()
    {
        var tracker = new ClipboardEchoTracker();
        tracker.Sent(Text("sent"));
        tracker.Observe(12, 42, foreground: false);
        Assert.Equal(ClipboardEcho.Sent, tracker.Classify(Text("sent"), 12, 42, background: true));
        Assert.Equal(ClipboardEcho.None, tracker.Classify(Text("different"), 12, 42, background: true));
        tracker.Applied(Text("received"));
        tracker.Observe(13, 42, foreground: false);
        Assert.Equal(ClipboardEcho.Applied, tracker.Classify(Text("received"), 13, 42, background: true));
    }

    [Theory]
    [InlineData(13u, 42u)]
    [InlineData(12u, 43u)]
    public void NewSequenceOrOwnerCannotInheritForegroundEvidence(uint sequence, uint owner)
    {
        var tracker = new ClipboardEchoTracker();
        var content = Text("same");
        tracker.Sent(content);
        tracker.Observe(12, 42, foreground: true);
        tracker.Observe(sequence, owner, foreground: false);
        Assert.Equal(ClipboardEcho.Sent, tracker.Classify(content, sequence, owner, background: true));
    }

    [Fact]
    public void ForegroundEvidenceCanBeAddedAfterFirstNotification()
    {
        var tracker = new ClipboardEchoTracker();
        var content = Text("same");
        tracker.Sent(content);
        tracker.Observe(12, 42, foreground: false);
        tracker.Observe(12, 42, foreground: true);
        Assert.Equal(ClipboardEcho.None, tracker.Classify(content, 12, 42, background: true));
    }

    [Fact]
    public void InterveningCopyAndClearEndOldEchoSuppression()
    {
        var tracker = new ClipboardEchoTracker();
        tracker.Applied(Text("A"));
        tracker.Sent(Text("B"));
        tracker.Observe(14, 42, foreground: false);
        Assert.Equal(ClipboardEcho.None, tracker.Classify(Text("A"), 14, 42, background: true));
        tracker.Clear();
        Assert.Equal(ClipboardEcho.None, tracker.Classify(Text("B"), 14, 42, background: true));
    }

    [Fact]
    public void OurOwnScreenCaptureIsNotAnEchoOfReceivedContent()
    {
        var tracker = new ClipboardEchoTracker();
        var content = Text("capture");
        tracker.Applied(content);
        tracker.Observe(14, 42, foreground: false);
        Assert.Equal(ClipboardEcho.None, tracker.Classify(content, 14, 42, background: false));
    }
}
