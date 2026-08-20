using System.Net;
using System.Security.Cryptography;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ProtocolAndRegistryTests
{
    private static KeyMaterial Key => KeyMaterial.FromSecret("test-shared-secret")!;

    [Fact]
    public void DiscoveryPacket_RoundTrips_AndRejectsMalformedVariants()
    {
        var packet = new DiscoveryPacket
        {
            DeviceTag = DiscoveryPacket.DeriveTag(Key, Guid.NewGuid().ToString()),
            TcpPort = 45671,
            IssuedUtc = DateTime.UtcNow,
            BootNonce = RandomNumberGenerator.GetBytes(DiscoveryPacket.BootNonceLength),
            Sequence = 4,
        };
        var wire = packet.Serialize(Key);
        Assert.True(DiscoveryPacket.TryParse(wire, Key, DateTime.UtcNow, out _, out _));
        Assert.False(DiscoveryPacket.TryParse(wire[..^1], Key, DateTime.UtcNow, out _, out _));
        Assert.False(DiscoveryPacket.TryParse(wire.Concat(new byte[1]).ToArray(), Key, DateTime.UtcNow, out _, out _));
        var tampered = wire.ToArray(); tampered[5] ^= 1;
        Assert.False(DiscoveryPacket.TryParse(tampered, Key, DateTime.UtcNow, out _, out _));
        tampered = wire.ToArray(); tampered[0] = (byte)'X';
        Assert.False(DiscoveryPacket.TryParse(tampered, Key, DateTime.UtcNow, out _, out _));
        Assert.False(DiscoveryPacket.TryParse(wire, KeyMaterial.FromSecret("wrong")!, DateTime.UtcNow, out _, out _));
        var stale = new DiscoveryPacket { DeviceTag = packet.DeviceTag, TcpPort = packet.TcpPort, IssuedUtc = DateTime.UtcNow.AddMinutes(-3), BootNonce = packet.BootNonce, Sequence = packet.Sequence };
        Assert.False(DiscoveryPacket.TryParse(stale.Serialize(Key), Key, DateTime.UtcNow, out _, out _));
        var future = new DiscoveryPacket { DeviceTag = packet.DeviceTag, TcpPort = packet.TcpPort, IssuedUtc = DateTime.UtcNow.AddMinutes(1), BootNonce = packet.BootNonce, Sequence = packet.Sequence };
        Assert.False(DiscoveryPacket.TryParse(future.Serialize(Key), Key, DateTime.UtcNow, out _, out _));
    }

    [Fact]
    public void ReplayCache_TracksBootsAndBoundsCapacity()
    {
        var cache = new DiscoveryReplayCache(2);
        DiscoveryPacket P(byte boot, ulong seq) => new() { DeviceTag = new byte[DiscoveryPacket.TagLength], BootNonce = Enumerable.Repeat(boot, DiscoveryPacket.BootNonceLength).ToArray(), Sequence = seq, TcpPort = 1, IssuedUtc = DateTime.UtcNow };
        Assert.True(cache.Accept(P(1, 1))); Assert.False(cache.Accept(P(1, 1))); Assert.False(cache.Accept(P(1, 0)));
        Assert.True(cache.Accept(P(2, 1))); Assert.False(cache.Accept(P(1, 1)));
        Assert.True(cache.Accept(P(3, 1))); Assert.True(cache.Accept(P(1, 1)));
    }

    [Fact]
    public void PeerRegistry_UsesExactIdentityAndRefusesCollisions()
    {
        var settings = new AppSettings { DeviceId = Guid.NewGuid().ToString() };
        var registry = new PeerRegistry(settings);
        var id = Guid.NewGuid().ToString();
        Assert.True(registry.TryPair(id, "192.168.1.10", "Alice"));
        Assert.False(registry.TryPair(id, "192.168.1.11", "Other"));
        Assert.False(registry.TryPair(settings.DeviceId, "192.168.1.12", "Self"));
        Assert.False(registry.TryPair("not-a-guid", "192.168.1.12", "Bad"));
        Assert.False(registry.TryPair(Guid.NewGuid().ToString(), "192.168.1.10", "Collision"));
        Assert.True(registry.TryUpdateHost(id, "192.168.1.11"));
        Assert.False(registry.TryUpdateHost(id, "192.168.1.11"));
        Assert.Equal("Alice", registry.Snapshot().Single().Nickname);
    }

    [Fact]
    public void PeerRegistry_BindsOnlyOneLegacyEndpoint()
    {
        var settings = new AppSettings { DeviceId = Guid.NewGuid().ToString() };
        settings.Peers.Add(new PeerInfo { Host = "192.168.1.20", Nickname = "legacy-a" });
        var registry = new PeerRegistry(settings);
        var id = Guid.NewGuid().ToString();
        Assert.True(registry.TryPair(id, "192.168.1.20", "ignored"));
        Assert.Equal(id, registry.Snapshot().Single().DeviceId);

        var ambiguous = new AppSettings { DeviceId = Guid.NewGuid().ToString() };
        ambiguous.Peers.Add(new PeerInfo { Host = "192.168.1.21" });
        ambiguous.Peers.Add(new PeerInfo { Host = "192.168.1.21" });
        Assert.False(new PeerRegistry(ambiguous).TryPair(Guid.NewGuid().ToString(), "192.168.1.21", "ambiguous"));
    }

    [Fact]
    public async Task WrongRecipient_IsRejectedBeforeReceiverFileCreation()
    {
        string root = Path.Combine(Path.GetTempPath(), "IntraDrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "test");
        var settings = new AppSettings { DeviceId = Guid.NewGuid().ToString(), DownloadFolder = Path.Combine(root, "received") };
        SettingsStore.SetSecret(settings, "test-shared-secret");
        var server = new TransferServer { GetSettings = () => settings, ConfirmRequest = _ => true };
        int port = GetFreePort();
        server.Start(port);
        try
        {
            var ex = await Assert.ThrowsAsync<TransferStatusException>(() => TransferClient.SendAsync(
                "127.0.0.1", port, "sender", new[] { source }, "test-shared-secret", null, CancellationToken.None,
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
            Assert.Equal(Protocol.StatusWrongDevice, ex.Status);
            Assert.False(Directory.Exists(settings.DownloadFolder));
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SecretlessRegisteredOnly_UsesLegacyIpAndNeverPersistsSenderId()
    {
        string root = Path.Combine(Path.GetTempPath(), "IntraDrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "test");
        var settings = new AppSettings { DeviceId = Guid.NewGuid().ToString(), DownloadFolder = Path.Combine(root, "received"), AcceptFromRegisteredOnly = true };
        settings.Peers.Add(new PeerInfo { Host = "127.0.0.1", Nickname = "legacy" });
        int saves = 0;
        var server = new TransferServer { GetSettings = () => settings, SavePeers = () => saves++, ConfirmRequest = _ => true };
        int port = GetFreePort(); server.Start(port);
        try
        {
            await TransferClient.SendAsync("127.0.0.1", port, "sender", new[] { source }, null, null, CancellationToken.None, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            Assert.True(File.Exists(Path.Combine(settings.DownloadFolder, "source.txt")));
            var registerResult = await TransferClient.RegisterAsync("127.0.0.1", port, "sender", null, senderDeviceId: Guid.NewGuid().ToString());
            Assert.Null(registerResult);
            Assert.All(settings.Peers, p => Assert.True(string.IsNullOrWhiteSpace(p.DeviceId)));
        }
        finally { server.Stop(); try { Directory.Delete(root, true); } catch { } }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void UnreadableProtectedSecret_IsUnavailable()
    {
        var settings = new AppSettings { SecretProtected = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) };
        var result = SettingsStore.ReadSecret(settings);
        Assert.Equal(SettingsStore.SecretAvailability.Unavailable, result.Availability);
        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void AuthenticatedRegistryUpdatesRejectHostCollisionsAtomically()
    {
        var settings = new AppSettings { DeviceId = Guid.NewGuid().ToString() };
        string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
        settings.Peers.Add(new PeerInfo { DeviceId = a, Host = "127.0.0.10", Nickname = "A" });
        settings.Peers.Add(new PeerInfo { DeviceId = b, Host = "127.0.0.11", Nickname = "B" });
        var registry = new PeerRegistry(settings);
        Assert.False(registry.TryVerifiedHostUpdate(a, "127.0.0.10", "127.0.0.11", "changed"));
        Assert.False(registry.TryRegisterAuthenticated(a, "127.0.0.11", "changed", out _));
        var snapshot = registry.Snapshot();
        Assert.Equal("127.0.0.10", snapshot.Single(p => p.DeviceId == a).Host);
        Assert.Equal("A", snapshot.Single(p => p.DeviceId == a).Nickname);
        Assert.True(registry.TryVerifiedHostUpdate(a, "127.0.0.10", "peer-hostname", "A2"));
        Assert.Equal("peer-hostname", registry.Snapshot().Single(p => p.DeviceId == a).Host);
    }

    [Fact]
    public async Task SecuredRegisterAndRediscoverValidateMutualIdentityAndRecipient()
    {
        string localId = Guid.NewGuid().ToString(), remoteId = Guid.NewGuid().ToString();
        var settings = new AppSettings { DeviceId = remoteId, DownloadFolder = Path.Combine(Path.GetTempPath(), "idr-" + Guid.NewGuid().ToString("N")) };
        settings.Peers.Add(new PeerInfo { DeviceId = localId, Host = "127.0.0.1", Nickname = "local" });
        SettingsStore.SetSecret(settings, "test-shared-secret");
        int saves = 0;
        var server = new TransferServer { GetSettings = () => settings, SavePeers = () => saves++ };
        int port = GetFreePort(); server.Start(port);
        try
        {
            var reply = await TransferClient.RegisterAsync("127.0.0.1", port, "local", "test-shared-secret", senderDeviceId: localId, recipientDeviceId: remoteId);
            Assert.NotNull(reply); Assert.Equal(remoteId, reply!.SenderDeviceId);
            var identity = await TransferClient.RediscoverAsync("127.0.0.1", port, "local", localId, remoteId, "test-shared-secret");
            Assert.Equal(remoteId, identity.SenderDeviceId);
            var ex = await Assert.ThrowsAsync<TransferStatusException>(() => TransferClient.RegisterAsync("127.0.0.1", port, "local", "test-shared-secret", senderDeviceId: localId, recipientDeviceId: Guid.NewGuid().ToString()));
            Assert.Equal(Protocol.StatusRefused, ex.Status);
            Assert.Equal(localId, settings.Peers.Single().DeviceId);
        }
        finally { server.Stop(); try { Directory.Delete(settings.DownloadFolder, true); } catch { } }
    }
}
