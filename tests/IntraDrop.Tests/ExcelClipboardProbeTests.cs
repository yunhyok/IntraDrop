using System.Diagnostics;
using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ExcelClipboardProbeTests
{
    [Theory]
    [InlineData(0, 10u, 20, 30u, true)]
    [InlineData(1, 10u, 20, 30u, false)]
    [InlineData(2, 10u, 20, 30u, false)]
    [InlineData(3, 10u, 20, 30u, false)]
    [InlineData(-1, 10u, 20, 30u, false)]
    [InlineData(0, 11u, 20, 30u, false)]
    [InlineData(0, 10u, 21, 30u, false)]
    [InlineData(0, 10u, 20, 31u, false)]
    public void OnlyExactIdleObservationCanReleaseProtection(int mode, uint sequence, int owner, uint processId, bool expected)
    {
        var state = new ExcelClipboardState(10, new IntPtr(20), 30, mode);
        Assert.Equal(expected, state.IsIdleFor(sequence, new IntPtr(owner), processId));
        Assert.False(new ExcelClipboardState(10, IntPtr.Zero, 30, 0).IsIdleFor(10, IntPtr.Zero, 30));
        Assert.False(new ExcelClipboardState(10, new IntPtr(20), 0, 0).IsIdleFor(10, new IntPtr(20), 0));
    }

    [Theory]
    [InlineData("0", "20", "30", true)]
    [InlineData("4294967295", "20", "4294967295", true)]
    [InlineData("4294967296", "20", "30", false)]
    [InlineData("10", "0", "30", false)]
    [InlineData("10", "20", "0", false)]
    [InlineData("10", "20", "4294967296", false)]
    [InlineData("-1", "20", "30", false)]
    [InlineData("+1", "20", "30", false)]
    [InlineData(" 1", "20", "30", false)]
    [InlineData("1", "-20", "30", false)]
    [InlineData("1", "20.0", "30", false)]
    [InlineData("1", "20", "30 ", false)]
    public void HelperAcceptsOnlyCompleteUnsignedNumericArguments(string sequence, string owner, string processId, bool expected)
    {
        Assert.Equal(expected, ExcelClipboardProbe.TryParseArguments(
            new[] { ExcelClipboardProbe.Argument, sequence, owner, processId }, out var state));
        if (expected)
        {
            Assert.NotNull(state);
            Assert.Equal(ExcelClipboardProbe.Unknown, state.Mode);
        }
        else Assert.Null(state);
    }

    [Fact]
    public void MalformedHelperInvocationCannotStartNormalApp()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { ExcelClipboardProbe.Argument },
            new[] { "--other-mode", "1", "20", "30" }, new[] { ExcelClipboardProbe.Argument, "1", "20", "30", "extra" } })
        {
            Assert.False(ExcelClipboardProbe.TryParseArguments(args, out _));
            Assert.Equal(ExcelClipboardProbe.Unknown, ExcelClipboardProbe.Run(args));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutOrCancelledHelperIsTerminated(bool cancel)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(100);
        var elapsed = Stopwatch.StartNew();
        Assert.Null(await ExcelClipboardProbe.WaitForExitAsync(process, cancel ? 2000 : 100, cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ExitedHelperReturnsItsExitCode()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 2")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        Assert.Equal(2, await ExcelClipboardProbe.WaitForExitAsync(process, 2000, CancellationToken.None));
    }
}
