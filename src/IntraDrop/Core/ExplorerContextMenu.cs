using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using IntraDrop.Models;

namespace IntraDrop.Core;

/// <summary>Explorer Send To shortcuts. Selected files are dropped as data, never opened as shell verbs.</summary>
public static class ExplorerContextMenu
{
    internal const string ShortcutPrefix = "IntraDrop - ";
    internal const string ShortcutDescription = "IntraDrop file transfer";
    private static readonly object SyncRoot = new();

    public sealed class Entry
    {
        public string Token { get; init; } = "";
        public string Label { get; init; } = "";
        public string Host { get; init; } = "";
        public string Arguments { get; init; } = "";
        public string DeviceId { get; init; } = "";
    }

    public sealed class Snapshot
    {
        public IReadOnlyList<Entry> Entries { get; init; } = Array.Empty<Entry>();
    }

    /// <summary>Builds safe deterministic command entries without touching the registry.</summary>
    public static Snapshot BuildSnapshot(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        List<PeerInfo> peers;
        lock (settings.SyncRoot)
            peers = (settings.Peers ?? new List<PeerInfo>()).Select(p => new PeerInfo { DeviceId = p.DeviceId, Host = p.Host, Nickname = p.Nickname, LastVerifiedUtc = p.LastVerifiedUtc }).ToList();
        var candidates = peers
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Host))
            .Select(p => (Peer: p, Token: TokenFor(p)))
            .Where(x => x.Token != null)
            .GroupBy(x => x.Token!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1) // duplicate IDs/legacy endpoints fail closed
            .Select(g => g.Single())
            .OrderBy(x => x.Peer.Nickname ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Peer.Host, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Entry
            {
                Token = x.Token!,
                Label = DisplayLabel(x.Peer),
                Host = x.Peer.Host.Trim(),
                DeviceId = NormalizeDeviceId(x.Peer.DeviceId),
                Arguments = "--send-token " + x.Token!,
            })
            .ToList();
        return new Snapshot { Entries = candidates };
    }

    /// <summary>Resolves a token against the latest settings snapshot; ambiguity and malformed tokens refuse.</summary>
    public static bool TryResolveToken(AppSettings settings, string token, out PeerInfo peer)
    {
        peer = null!;
        if (settings == null || !IsSafeToken(token)) return false;
        PeerInfo[] matches;
        lock (settings.SyncRoot)
            matches = (settings.Peers ?? new List<PeerInfo>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Host) && string.Equals(TokenFor(p), token, StringComparison.Ordinal))
                .Select(p => new PeerInfo { DeviceId = p.DeviceId, Host = p.Host, Nickname = p.Nickname, LastVerifiedUtc = p.LastVerifiedUtc })
                .ToArray();
        if (matches.Length != 1) return false;
        peer = matches[0];
        return true;
    }

    public static string? TokenFor(PeerInfo peer)
    {
        if (peer == null || string.IsNullOrWhiteSpace(peer.Host)) return null;
        if (Guid.TryParse(peer.DeviceId?.Trim(), out var id)) return "id-" + id.ToString("N").ToLowerInvariant();
        string host = peer.Host.Trim().ToLowerInvariant();
        if (host.Length == 0 || host.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return null;
        using var sha = SHA256.Create();
        return "legacy-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(host))).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Use Windows Send To instead of static verbs, which can execute downloaded EXEs.</summary>
    public static void Sync(AppSettings settings, string? executablePath = null)
    {
        lock (SyncRoot)
        try
        {
            // Remove both old cascade layouts, including on upgrades from 1.6.x.
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AllFilesystemObjects\shell\IntraDrop", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\IntraDrop.ContextMenu", false);
            SyncShortcuts(BuildSnapshot(settings), executablePath ?? System.Windows.Forms.Application.ExecutablePath,
                Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        }
        catch { /* Explorer integration must never prevent startup or settings changes. */ }
        NotifyExplorer();
    }

    internal static void SyncShortcuts(Snapshot snapshot, string executablePath, string directory)
    {
        Directory.CreateDirectory(directory);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(directory, ShortcutPrefix + "*.lnk"))
            {
                dynamic link = shell.CreateShortcut(path);
                try { if ((string)link.Description == ShortcutDescription) owned.Add(path, (string)link.Arguments); }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Entries)
            {
                string label = new string(entry.Label.Take(80).Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                string stem = ShortcutPrefix + label;
                string path = owned.FirstOrDefault(p => p.Value == entry.Arguments &&
                    Regex.IsMatch(Path.GetFileNameWithoutExtension(p.Key), "^" + Regex.Escape(stem) + @"(?: \(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Key
                    ?? Path.Combine(directory, stem + ".lnk");
                int suffix = 2;
                while (current.Contains(path) || (File.Exists(path) &&
                    (!owned.TryGetValue(path, out var arguments) || arguments != entry.Arguments)))
                    path = Path.Combine(directory, stem + " (" + suffix++ + ").lnk");
                dynamic link = shell.CreateShortcut(path);
                try
                {
                    link.TargetPath = executablePath;
                    // Windows appends the complete selection; do not put %1 in a shortcut.
                    link.Arguments = entry.Arguments;
                    link.WorkingDirectory = Path.GetDirectoryName(executablePath);
                    link.IconLocation = executablePath + ",0";
                    link.Description = ShortcutDescription;
                    link.Save();
                    current.Add(path);
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            foreach (string stale in owned.Keys.Where(path => !current.Contains(path))) File.Delete(stale);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static string DisplayLabel(PeerInfo peer)
    {
        string value = string.IsNullOrWhiteSpace(peer.Nickname) ? peer.Host.Trim() : peer.Nickname.Trim();
        return new string(value.Select(c => c is '\r' or '\n' or '\0' ? ' ' : c).ToArray());
    }

    private static string NormalizeDeviceId(string? value) => Guid.TryParse(value?.Trim(), out var id) ? id.ToString("N").ToLowerInvariant() : "";

    private static bool IsSafeToken(string token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 80 && token.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static void NotifyExplorer()
    {
        try { NativeMethods.SHChangeNotify(0x08000000u, 0u, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        internal static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
