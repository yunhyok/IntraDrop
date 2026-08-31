using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using IntraDrop.Models;

namespace IntraDrop.Core;

/// <summary>Static Explorer cascading-menu registration. Menu construction is pure; registry I/O is best effort.</summary>
public static class ExplorerContextMenu
{
    public const string ParentSubKey = @"Software\Classes\AllFilesystemObjects\shell\IntraDrop";
    public const string ExtendedSubCommandsKey = @"AllFilesystemObjects\shell\IntraDrop";
    public const string ChildShellSubKey = ParentSubKey + @"\Shell";
    public const string MultiSelectModel = "Player";

    public sealed class Entry
    {
        public string Token { get; init; } = "";
        public string Label { get; init; } = "";
        public string Host { get; init; } = "";
        public string Command { get; init; } = "";
        public string DeviceId { get; init; } = "";
    }

    public sealed class Snapshot
    {
        public IReadOnlyList<Entry> Entries { get; init; } = Array.Empty<Entry>();
    }

    /// <summary>Builds safe deterministic command entries without touching the registry.</summary>
    public static Snapshot BuildSnapshot(AppSettings settings, string executablePath)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("Executable path is required.", nameof(executablePath));
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
                Command = BuildCommand(executablePath, x.Token!),
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

    public static string BuildCommand(string executablePath, string token) =>
        QuoteWindowsArg(executablePath) + " --send-token " + token + " %*";

    public static string? TokenFor(PeerInfo peer)
    {
        if (peer == null || string.IsNullOrWhiteSpace(peer.Host)) return null;
        if (Guid.TryParse(peer.DeviceId?.Trim(), out var id)) return "id-" + id.ToString("N").ToLowerInvariant();
        string host = peer.Host.Trim().ToLowerInvariant();
        if (host.Length == 0 || host.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return null;
        using var sha = SHA256.Create();
        return "legacy-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(host))).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Best-effort HKCU registration. Explorer is notified even when a registry operation fails.</summary>
    public static void Sync(AppSettings settings, string? executablePath = null)
    {
        try
        {
            executablePath ??= System.Windows.Forms.Application.ExecutablePath;
            var snapshot = BuildSnapshot(settings, executablePath);
            using var parent = Registry.CurrentUser.CreateSubKey(ParentSubKey);
            if (parent != null)
            {
                parent.SetValue("MUIVerb", "IntraDrop", RegistryValueKind.String);
                parent.SetValue("ExtendedSubCommandsKey", ExtendedSubCommandsKey, RegistryValueKind.String);
                parent.SetValue("MultiSelectModel", MultiSelectModel, RegistryValueKind.String);
            }
            using var commands = Registry.CurrentUser.CreateSubKey(ChildShellSubKey);
            if (commands != null)
            {
                foreach (var stale in commands.GetSubKeyNames())
                    try { commands.DeleteSubKeyTree(stale, false); } catch { }
                foreach (var entry in snapshot.Entries)
                {
                    using var key = commands.CreateSubKey(entry.Token);
                    key?.SetValue("MUIVerb", entry.Label, RegistryValueKind.String);
                    key?.SetValue("MultiSelectModel", MultiSelectModel, RegistryValueKind.String);
                    using var command = key?.CreateSubKey("command");
                    command?.SetValue(null, entry.Command, RegistryValueKind.String);
                }
            }
        }
        catch { /* Explorer integration must never prevent startup or settings changes. */ }
        NotifyExplorer();
    }

    private static string DisplayLabel(PeerInfo peer)
    {
        string value = string.IsNullOrWhiteSpace(peer.Nickname) ? peer.Host.Trim() : peer.Nickname.Trim();
        return new string(value.Select(c => c is '\r' or '\n' or '\0' ? ' ' : c).ToArray());
    }

    private static string NormalizeDeviceId(string? value) => Guid.TryParse(value?.Trim(), out var id) ? id.ToString("N").ToLowerInvariant() : "";

    private static bool IsSafeToken(string token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 80 && token.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string QuoteWindowsArg(string value)
    {
        var b = new StringBuilder(value.Length + 2); b.Append('"');
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { b.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            if (slashes > 0) { b.Append('\\', slashes); slashes = 0; }
            b.Append(c);
        }
        b.Append('\\', slashes * 2).Append('"');
        return b.ToString();
    }

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
