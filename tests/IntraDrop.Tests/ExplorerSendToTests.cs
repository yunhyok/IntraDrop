using System.Diagnostics;
using System.Runtime.InteropServices;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ExplorerSendToTests
{
    [Fact]
    public void Shortcuts_HandleDuplicateNamesAndKeepUnownedFiles()
    {
        string root = Directory.CreateTempSubdirectory("IntraDrop-shortcuts-").FullName;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            string executable = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            string personal = Path.Combine(root, "IntraDrop - Workstation.lnk");
            dynamic other = shell.CreateShortcut(personal);
            try { other.TargetPath = executable; other.Description = "personal shortcut"; other.Save(); }
            finally { Marshal.FinalReleaseComObject(other); }
            byte[] original = File.ReadAllBytes(personal);
            var settings = new AppSettings();
            settings.Peers.Add(new PeerInfo { Host = "peer-a", Nickname = "Workstation" });
            settings.Peers.Add(new PeerInfo { Host = "peer-b", Nickname = "Workstation" });
            settings.Peers.Add(new PeerInfo { Host = "peer-c", Nickname = "bad:/\\?*\"name" });
            var snapshot = ExplorerContextMenu.BuildSnapshot(settings);
            ExplorerContextMenu.SyncShortcuts(snapshot, executable, root);
            Assert.Equal(4, Directory.GetFiles(root, "*.lnk").Length);
            var pathsByArguments = new Dictionary<string, string>();
            foreach (string path in Directory.GetFiles(root, "*.lnk").Where(p => p != personal))
            {
                dynamic link = shell.CreateShortcut(path);
                try
                {
                    Assert.Equal(executable, (string)link.TargetPath, ignoreCase: true);
                    Assert.Contains(snapshot.Entries, e => e.Arguments == (string)link.Arguments);
                    Assert.DoesNotContain("%1", (string)link.Arguments);
                    pathsByArguments.Add((string)link.Arguments, path);
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            settings.Peers.RemoveAt(0);
            ExplorerContextMenu.SyncShortcuts(ExplorerContextMenu.BuildSnapshot(settings), executable, root);
            string remainingArguments = "--send-token " + ExplorerContextMenu.TokenFor(settings.Peers[0]);
            dynamic remaining = shell.CreateShortcut(pathsByArguments[remainingArguments]);
            try { Assert.Equal(remainingArguments, (string)remaining.Arguments); }
            finally { Marshal.FinalReleaseComObject(remaining); }
            settings.Peers.Clear();
            ExplorerContextMenu.SyncShortcuts(ExplorerContextMenu.BuildSnapshot(settings), executable, root);
            Assert.Single(Directory.GetFiles(root));
            Assert.Equal(original, File.ReadAllBytes(personal));
        }
        finally { Marshal.FinalReleaseComObject(shell); Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "InteractiveShell")]
    public void WindowsSendTo_PassesDownloadedExeAndMultipleFilesWithoutExecutingSelection()
    {
        string root = Directory.CreateTempSubdirectory("IntraDrop-shell-").FullName;
        string? installedLink = null;
        try
        {
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            string capture = Path.Combine(root, "Capture.exe");
            string probe = Path.Combine(root, "ShellMenuProbe.exe");
            Run(compiler, "/nologo", "/target:winexe", "/out:" + capture, Path.Combine(AppContext.BaseDirectory, "Fixtures", "Capture.cs"));
            Run(compiler, "/nologo", "/out:" + probe, Path.Combine(AppContext.BaseDirectory, "Fixtures", "ShellMenuProbe.cs"));
            string selected = Path.Combine(root, "설치 파일 & 100%.exe");
            File.Copy(capture, selected);
            // Keep the downloaded-file restriction that triggers the old static-verb bug.
            string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
            File.WriteAllText(selected + ":Zone.Identifier", zone);
            string second = Path.Combine(root, "두 번째 파일.txt");
            File.WriteAllText(second, "data");
            var settings = new AppSettings();
            settings.Peers.Add(new PeerInfo { Host = "probe-host", Nickname = "Probe-" + Guid.NewGuid().ToString("N") });
            var snapshot = ExplorerContextMenu.BuildSnapshot(settings);
            string shortcuts = Path.Combine(root, "links");
            ExplorerContextMenu.SyncShortcuts(snapshot, capture, shortcuts);
            string link = Directory.GetFiles(shortcuts, "*.lnk").Single();
            installedLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), Path.GetFileName(link));
            Assert.False(File.Exists(installedLink));
            Directory.CreateDirectory(Path.GetDirectoryName(installedLink)!);
            File.Copy(link, installedLink);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                dynamic testLink = shell.CreateShortcut(installedLink);
                try { testLink.Description = "IntraDrop shell regression test"; testLink.Save(); }
                finally { Marshal.FinalReleaseComObject(testLink); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            string log = Path.Combine(root, "invocations.log");
            foreach (string[] selection in new[] { new[] { selected }, new[] { selected, second } })
            {
                if (File.Exists(log)) File.Delete(log);
                Run(probe, new[] { Path.GetFileNameWithoutExtension(link) }.Concat(selection).ToArray());
                Assert.True(SpinWait.SpinUntil(() => File.Exists(log), TimeSpan.FromSeconds(5)), "Send To did not start its handler.");
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    try { using var file = File.Open(capture, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
                    catch (IOException) { return false; }
                    catch (UnauthorizedAccessException) { return false; }
                }, TimeSpan.FromSeconds(5)), "Capture process did not exit.");
                Assert.Equal(new[] { "Capture.exe", "--send-token", snapshot.Entries[0].Token }.Concat(selection), File.ReadAllLines(log));
                Assert.Equal(zone, File.ReadAllText(selected + ":Zone.Identifier"));
            }
        }
        finally
        {
            if (installedLink != null && File.Exists(installedLink)) File.Delete(installedLink);
            Directory.Delete(root, true);
        }
    }

    private static void Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000))
        {
            process.Kill(); process.WaitForExit();
            throw new TimeoutException("Shell invocation did not complete; the selected file may have entered the execution-warning path.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());
    }
}
