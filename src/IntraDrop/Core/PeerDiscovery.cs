using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using IntraDrop.Models;

namespace IntraDrop.Core;

/// <summary>Fixed-format authenticated UDP candidate hint. A hint never mutates settings.</summary>
public sealed class DiscoveryPacket
{
    public const byte Version = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("IDPD");
    public const int TagLength = 16;
    public const int BootNonceLength = 16;
    public const int HmacLength = 32;
    public const int Length = 4 + 1 + TagLength + 2 + 8 + BootNonceLength + 8 + HmacLength;

    public byte[] DeviceTag { get; init; } = new byte[TagLength];
    public int TcpPort { get; init; }
    public DateTime IssuedUtc { get; init; }
    public byte[] BootNonce { get; init; } = new byte[BootNonceLength];
    public ulong Sequence { get; init; }

    public static byte[] DeriveTag(KeyMaterial key, string deviceId)
    {
        using var h = new HMACSHA256(key.MacKey);
        byte[] input = Encoding.UTF8.GetBytes("IntraDrop.v1.3.discovery.tag\0" + deviceId.Trim());
        return h.ComputeHash(input).Take(TagLength).ToArray();
    }

    private static byte[] DerivePacketKey(KeyMaterial key)
    {
        using var h = new HMACSHA256(key.MacKey);
        return h.ComputeHash(Encoding.UTF8.GetBytes("IntraDrop.v1.3.discovery.packet\0"));
    }

    public byte[] Serialize(KeyMaterial key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (DeviceTag.Length != TagLength || BootNonce.Length != BootNonceLength)
            throw new InvalidDataException("잘못된 디바이스 태그/부트 논스");
        if (TcpPort is < 1 or > 65535) throw new InvalidDataException("잘못된 포트");
        byte[] data = new byte[Length];
        Buffer.BlockCopy(Magic, 0, data, 0, Magic.Length);
        data[4] = Version;
        Buffer.BlockCopy(DeviceTag, 0, data, 5, TagLength);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(21), (ushort)TcpPort);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(23), IssuedUtc.ToUniversalTime().Ticks);
        Buffer.BlockCopy(BootNonce, 0, data, 31, BootNonceLength);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(47), Sequence);
        using var h = new HMACSHA256(DerivePacketKey(key));
        Buffer.BlockCopy(h.ComputeHash(data, 0, Length - HmacLength), 0, data, Length - HmacLength, HmacLength);
        return data;
    }

    public static bool TryParse(byte[] data, KeyMaterial key, DateTime utcNow, out DiscoveryPacket packet, out string reason)
    {
        packet = null!; reason = "invalid";
        if (key == null || data == null || data.Length != Length) { reason = "length"; return false; }
        for (int i = 0; i < Magic.Length; i++) if (data[i] != Magic[i]) { reason = "magic"; return false; }
        if (data[4] != Version) { reason = "version"; return false; }
        using (var h = new HMACSHA256(DerivePacketKey(key)))
        {
            byte[] expected = h.ComputeHash(data, 0, Length - HmacLength);
            byte[] actual = data.Skip(Length - HmacLength).ToArray();
            if (!Crypto.FixedTimeEquals(expected, actual)) { reason = "mac"; return false; }
        }
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(21));
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(23));
        DateTime issued;
        try { issued = new DateTime(ticks, DateTimeKind.Utc); } catch { reason = "time"; return false; }
        TimeSpan age = utcNow.ToUniversalTime() - issued;
        if (port == 0 || age > TimeSpan.FromMinutes(2)) { reason = "stale"; return false; }
        if (age < TimeSpan.FromSeconds(-30)) { reason = "future"; return false; }
        var tag = data.Skip(5).Take(TagLength).ToArray();
        var boot = data.Skip(31).Take(BootNonceLength).ToArray();
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(47));
        packet = new DiscoveryPacket { DeviceTag = tag, TcpPort = port, IssuedUtc = issued, BootNonce = boot, Sequence = seq };
        reason = "ok"; return true;
    }
}

/// <summary>Bounded replay protection keyed by authenticated tag and process boot nonce.</summary>
public sealed class DiscoveryReplayCache
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Dictionary<string, (byte[] Boot, ulong Seq, DateTime Seen)> _entries = new();
    public DiscoveryReplayCache(int capacity = 256) { _capacity = Math.Max(1, capacity); }
    public bool Accept(DiscoveryPacket packet)
    {
        string tag = Convert.ToBase64String(packet.DeviceTag);
        string key = tag + ":" + Convert.ToBase64String(packet.BootNonce);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var prior) && packet.Sequence <= prior.Seq)
                return false;
            _entries[key] = (packet.BootNonce.ToArray(), packet.Sequence, DateTime.UtcNow);
            if (_entries.Count > _capacity)
            {
                string oldest = _entries.OrderBy(k => k.Value.Seen).First().Key;
                _entries.Remove(oldest);
            }
            return true;
        }
    }
}

/// <summary>Thread-safe registry. DeviceId is authoritative once paired; IP is never an identity.</summary>
public sealed class PeerRegistry
{
    private readonly AppSettings _settings;
    private object Sync => _settings.SyncRoot;
    public PeerRegistry(AppSettings settings) { _settings = settings ?? throw new ArgumentNullException(nameof(settings)); }
    public IReadOnlyList<PeerInfo> Snapshot()
    {
        lock (Sync) return _settings.Peers.Select(Clone).ToList();
    }
    /// <summary>Returns the live peer references while the registry lock is held.
    /// Callers must not retain the returned list for mutation; this preserves UI identity.</summary>
    public IReadOnlyList<PeerInfo> SnapshotReferences()
    {
        lock (Sync) return _settings.Peers.ToList();
    }
    public bool Add(PeerInfo peer)
    {
        if (peer == null || string.IsNullOrWhiteSpace(peer.Host)) return false;
        lock (Sync)
        {
            if (_settings.Peers.Any(p => string.Equals(p.Host?.Trim(), peer.Host.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            _settings.Peers.Add(peer);
            return true;
        }
    }
    public bool Update(PeerInfo peer, string nickname, string host)
    {
        if (peer == null || string.IsNullOrWhiteSpace(host)) return false;
        lock (Sync)
        {
            if (!_settings.Peers.Contains(peer)) return false;
            if (_settings.Peers.Any(p => !ReferenceEquals(p, peer) && string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            bool hostChanged = !string.Equals(peer.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase);
            peer.Nickname = string.IsNullOrWhiteSpace(nickname) ? host.Trim() : nickname.Trim();
            peer.Host = host.Trim();
            if (hostChanged) { peer.DeviceId = ""; peer.LastVerifiedUtc = null; }
            return true;
        }
    }
    public bool Remove(PeerInfo peer)
    {
        if (peer == null) return false;
        lock (Sync) return _settings.Peers.Remove(peer);
    }
    public void Save() { lock (Sync) SettingsStore.Save(_settings); }
    public bool TryUpdateHost(string deviceId, string host)
    {
        return TryVerifiedHostUpdate(deviceId, null, host, null);
    }

    /// <summary>Atomically commits an authenticated host edit. expectedHost, when supplied,
    /// prevents a stale UI edit from overwriting a concurrent rediscovery.</summary>
    public bool TryVerifiedHostUpdate(string deviceId, string? expectedHost, string host, string? nickname)
    {
        if (!ValidId(deviceId) || string.Equals(deviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(host)) return false;
        lock (Sync)
        {
            var matches = _settings.Peers.Where(p => string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return false;
            var peer = matches[0];
            if (expectedHost != null && !string.Equals(peer.Host?.Trim(), expectedHost.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(peer.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (_settings.Peers.Any(p => !ReferenceEquals(p, peer) && string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            peer.Host = host.Trim();
            if (nickname != null && !string.IsNullOrWhiteSpace(nickname)) peer.Nickname = nickname.Trim();
            peer.LastVerifiedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public bool TryConfirmVerified(string deviceId, string expectedHost, string authenticatedHost, string? nickname, out bool hostChanged)
    {
        hostChanged = false;
        if (!ValidId(deviceId) || string.IsNullOrWhiteSpace(expectedHost) || string.IsNullOrWhiteSpace(authenticatedHost)) return false;
        lock (Sync)
        {
            var matches = _settings.Peers.Where(p => string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !string.Equals(matches[0].Host?.Trim(), expectedHost.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            var peer = matches[0];
            if (_settings.Peers.Any(p => !ReferenceEquals(p, peer) && string.Equals(p.Host?.Trim(), authenticatedHost.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            hostChanged = !string.Equals(peer.Host?.Trim(), authenticatedHost.Trim(), StringComparison.OrdinalIgnoreCase);
            peer.Host = authenticatedHost.Trim();
            if (!string.IsNullOrWhiteSpace(nickname)) peer.Nickname = nickname!.Trim();
            peer.LastVerifiedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public bool TryPair(string deviceId, string host, string nickname)
    {
        if (!ValidId(deviceId) || string.Equals(deviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase) || !IPAddress.TryParse(host, out _)) return false;
        lock (Sync)
        {
            if (_settings.Peers.Any(p => string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))) return false;
            var legacy = _settings.Peers.Where(p => string.IsNullOrWhiteSpace(p.DeviceId) && string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (legacy.Count == 1) { legacy[0].DeviceId = deviceId.Trim(); legacy[0].LastVerifiedUtc = DateTime.UtcNow; return true; }
            if (legacy.Count > 1) return false;
            if (_settings.Peers.Any(p => string.Equals((p.Host ?? "").Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.DeviceId))) return false;
            _settings.Peers.Add(new PeerInfo { DeviceId = deviceId.Trim(), Host = host.Trim(), Nickname = string.IsNullOrWhiteSpace(nickname) ? host.Trim() : nickname.Trim(), LastVerifiedUtc = DateTime.UtcNow });
            return true;
        }
    }
    public string? FindDeviceIdByTag(KeyMaterial key, byte[] tag)
    {
        lock (Sync)
        {
            var matches = _settings.Peers.Where(p => ValidId(p.DeviceId)).Where(p => Crypto.FixedTimeEquals(DiscoveryPacket.DeriveTag(key, p.DeviceId), tag)).Select(p => p.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && Guid.TryParse(id.Trim(), out _) && id.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
    private static PeerInfo Clone(PeerInfo p) => new() { DeviceId = p.DeviceId, Host = p.Host, Nickname = p.Nickname, LastVerifiedUtc = p.LastVerifiedUtc };

    public bool TryRegisterAuthenticated(string deviceId, string host, string nickname, out bool added)
    {
        added = false;
        if (!ValidId(deviceId) || string.Equals(deviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase) || !IPAddress.TryParse(host, out _)) return false;
        lock (Sync)
        {
            var byId = _settings.Peers.Where(p => string.Equals(p.DeviceId?.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count > 1) return false;
            if (byId.Count == 1)
            {
                if (_settings.Peers.Any(p => !ReferenceEquals(p, byId[0]) && string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
                byId[0].Host = host.Trim(); byId[0].LastVerifiedUtc = DateTime.UtcNow; return true;
            }
            var legacy = _settings.Peers.Where(p => string.IsNullOrWhiteSpace(p.DeviceId) && string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (legacy.Count > 1) return false;
            if (legacy.Count == 1)
            {
                legacy[0].DeviceId = deviceId.Trim(); legacy[0].LastVerifiedUtc = DateTime.UtcNow; return true;
            }
            if (_settings.Peers.Any(p => string.Equals(p.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.DeviceId))) return false;
            _settings.Peers.Add(new PeerInfo { DeviceId = deviceId.Trim(), Host = host.Trim(), Nickname = string.IsNullOrWhiteSpace(nickname) ? host.Trim() : nickname.Trim(), LastVerifiedUtc = DateTime.UtcNow });
            added = true;
            return true;
        }
    }
}

/// <summary>Bounded UDP hint listener/announcer. Candidate hints are surfaced to the caller for TCP authentication.</summary>
public sealed class PeerDiscoveryService : IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<SettingsStore.SecretResult> _getSecret;
    private readonly DiscoveryReplayCache _replay = new();
    private readonly SemaphoreSlim _handshakeSlots = new(4, 4);
    private readonly SemaphoreSlim _outboundHandshakeSlots = new(4, 4);
    private readonly SemaphoreSlim _announceSlots = new(1, 1);
    private readonly Dictionary<string, DateTime> _cooldown = new();
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _addressBurstCts;
    private bool _networkSubscribed;
    private int _generation;
    private readonly byte[] _bootNonce = Crypto.RandomBytes(DiscoveryPacket.BootNonceLength);
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private long _sequence;
    public event Action<IPAddress, DiscoveryPacket>? CandidateReceived;
    public Func<IPAddress, DiscoveryPacket, CancellationToken, Task>? AuthenticatedCandidate { get; set; }

    public PeerDiscoveryService(Func<AppSettings> getSettings, Func<SettingsStore.SecretResult> getSecret)
    { _getSettings = getSettings; _getSecret = getSecret; }

    public void Start()
    {
        Stop();
        var s = _getSettings(); var sec = _getSecret();
        if (!sec.IsAvailable) return;
        _cts = new CancellationTokenSource();
        if (s.EnablePeerDiscovery)
        {
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                _udp.Client.ExclusiveAddressUse = true;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, s.Port));
            }
            catch { _udp?.Close(); _udp = null; } // Direct TCP announcements still work without UDP.
        }
        int generation;
        lock (_lifecycleSync)
        {
            _networkSubscribed = true;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            generation = ++_generation;
        }
        if (_udp != null)
            _ = ReceiveLoopAsync(_udp, _cts.Token, generation);
        _ = AnnounceLoopAsync(_cts.Token);
    }
    public void Stop()
    {
        lock (_lifecycleSync)
        {
            if (_networkSubscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _networkSubscribed = false;
            }
            try { _addressBurstCts?.Cancel(); } catch { }
            _addressBurstCts?.Dispose();
            _addressBurstCts = null;
            _generation++;
        }
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        _udp = null; _cts = null;
    }
    public void Dispose() => Stop();

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        CancellationTokenSource burst;
        lock (_lifecycleSync)
        {
            if (!_networkSubscribed || _cts == null) return;
            try { _addressBurstCts?.Cancel(); } catch { }
            _addressBurstCts?.Dispose();
            burst = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _addressBurstCts = burst;
        }
        _ = AddressBurstAsync(burst, burst.Token);
    }

    private async Task AddressBurstAsync(CancellationTokenSource owner, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                await AnnounceAsync(ct).ConfigureAwait(false);
                if (i < 2) await Task.Delay(TimeSpan.FromSeconds(i == 0 ? 2 : 5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_addressBurstCts, owner)) _addressBurstCts = null;
            }
            owner.Dispose();
        }
    }
    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct, int generation)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync().ConfigureAwait(false); }
            catch { break; }
            var sec = _getSecret(); if (!sec.IsAvailable) continue;
            if (!DiscoveryPacket.TryParse(r.Buffer, KeyMaterial.FromSecret(sec.Secret)!, DateTime.UtcNow, out var packet, out _)) continue;
            if (!_replay.Accept(packet) || IsLocalAddress(r.RemoteEndPoint.Address) || !IsLocalSubnetSource(r.RemoteEndPoint.Address)) continue;
            CandidateReceived?.Invoke(r.RemoteEndPoint.Address, packet);
            if (AuthenticatedCandidate != null)
            {
                string key = Convert.ToBase64String(packet.DeviceTag) + ":" + r.RemoteEndPoint.Address;
                bool allowed;
                lock (_cooldown)
                {
                    var now = DateTime.UtcNow;
                    allowed = !_cooldown.TryGetValue(key, out var t) || now - t > TimeSpan.FromSeconds(20);
                    if (_cooldown.Count > 512)
                    {
                        foreach (var old in _cooldown.OrderBy(k => k.Value).Take(_cooldown.Count - 256).ToList()) _cooldown.Remove(old.Key);
                    }
                }
                if (allowed && await _handshakeSlots.WaitAsync(0).ConfigureAwait(false))
                {
                    lock (_cooldown) _cooldown[key] = DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            lock (_lifecycleSync) if (generation != _generation || ct.IsCancellationRequested) return;
                            await AuthenticatedCandidate(r.RemoteEndPoint.Address, packet, ct).ConfigureAwait(false);
                        }
                        catch { }
                        finally { _handshakeSlots.Release(); }
                    });
                }
            }
        }
    }
    private async Task AnnounceAsync(CancellationToken ct)
    {
        try { await _announceSlots.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            var sec = _getSecret(); var s = _getSettings(); if (!sec.IsAvailable) return;
            var key = KeyMaterial.FromSecret(sec.Secret)!;
            ulong sequence = unchecked((ulong)Interlocked.Increment(ref _sequence));
            var packet = new DiscoveryPacket { DeviceTag = DiscoveryPacket.DeriveTag(key, s.DeviceId), TcpPort = s.Port, IssuedUtc = DateTime.UtcNow, BootNonce = _bootNonce, Sequence = sequence };
            byte[] wire = packet.Serialize(key);
            try
            {
                if (_udp != null)
                    foreach (var broadcast in BroadcastAddresses())
                        await _udp.SendAsync(wire, wire.Length, new IPEndPoint(broadcast, s.Port)).ConfigureAwait(false);
            }
            catch { /* A broadcast failure must not skip the registered peers. */ }

            await Task.WhenAll(new PeerRegistry(s).Snapshot()
                .Where(p => Guid.TryParse(p.DeviceId, out _) && !string.Equals(p.DeviceId, s.DeviceId, StringComparison.OrdinalIgnoreCase))
                .Select(async peer =>
                {
                    await _outboundHandshakeSlots.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var currentSecret = _getSecret();
                        if (!currentSecret.IsAvailable || currentSecret.Secret != sec.Secret) return;
                        // The receiver verifies our identity and learns our current IP from the TCP connection.
                        await TransferClient.RediscoverAsync(peer.Host, s.Port, s.DeviceName, s.DeviceId,
                            peer.DeviceId, sec.Secret!, 5000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* Offline or changed peers are retried by subsequent announcements. */ }
                    finally { _outboundHandshakeSlots.Release(); }
                })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { _announceSlots.Release(); }
    }
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await AnnounceAsync(ct).ConfigureAwait(false);
            if (i < 2)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(i == 0 ? 2 : 5), ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }
    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) return true;
        return address.Equals(GetLocalV4());
    }
    private static IPAddress GetLocalV4()
    {
        try { return Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.None; } catch { return IPAddress.None; }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null || u.Address.GetAddressBytes()[0] == 169) continue;
                byte[] ip = u.Address.GetAddressBytes(), mask = u.IPv4Mask.GetAddressBytes(), b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | (byte)~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }
    private static bool IsLocalSubnetSource(IPAddress source)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork) return false;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                byte[] a = u.Address.GetAddressBytes(), b = source.GetAddressBytes(), m = u.IPv4Mask.GetAddressBytes(); bool same = true;
                for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) { same = false; break; }
                if (same) return true;
            }
        }
        return false;
    }
}
