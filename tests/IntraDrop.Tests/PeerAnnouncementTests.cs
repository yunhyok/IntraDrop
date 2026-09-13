using System.Net;
using System.Net.Sockets;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class PeerAnnouncementTests
{
    [Fact]
    public void FailedListenerDoesNotReportRunning()
    {
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Server.ExclusiveAddressUse = true;
        busy.Start();
        var server = new TransferServer();
        try
        {
            Assert.Throws<SocketException>(() => server.Start(((IPEndPoint)busy.LocalEndpoint).Port, IPAddress.Loopback));
            Assert.False(server.IsRunning);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAndRestartNotifyRegisteredPeerEvenWithoutUdp(bool enablePeerDiscovery)
    {
        using var busyUdp = new UdpClient(AddressFamily.InterNetwork);
        busyUdp.Client.ExclusiveAddressUse = true;
        int port;
        for (int attempt = 0; ; attempt++)
        {
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            try { busyUdp.Client.Bind(new IPEndPoint(IPAddress.Any, port)); break; }
            // A free TCP port can be reserved or already occupied for UDP.
            catch (SocketException ex) when (attempt < 19 &&
                ex.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { }
        }

        var sender = new AppSettings { Port = port, EnablePeerDiscovery = enablePeerDiscovery };
        var receiver = new AppSettings { Port = port };
        SettingsStore.SetSecret(sender, "restart-test-secret");
        SettingsStore.SetSecret(receiver, "restart-test-secret");
        sender.Peers.Add(new PeerInfo { DeviceId = receiver.DeviceId, Host = "127.0.0.2" });
        var registeredSender = new PeerInfo { DeviceId = sender.DeviceId, Host = "127.0.0.9", Nickname = "My PC" };
        receiver.Peers.Add(registeredSender);
        int saves = 0;
        using var changed = new SemaphoreSlim(0);
        var server = new TransferServer { GetSettings = () => receiver, SavePeers = () => Interlocked.Increment(ref saves) };
        server.PeerAddressChanged += () => changed.Release();
        server.Start(port, IPAddress.Parse("127.0.0.2"));
        using var discovery = new PeerDiscoveryService(() => sender, () => SettingsStore.ReadSecret(sender));
        try
        {
            for (int restart = 0; restart < 2; restart++)
            {
                registeredSender.Host = "127.0.0.9";
                discovery.Start();
                Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)), "Startup did not announce the new IP.");
                discovery.Stop();
                Assert.Equal("127.0.0.1", registeredSender.Host);
                Assert.Equal("My PC", registeredSender.Nickname);
                Assert.Equal(sender.DeviceId, registeredSender.DeviceId);
                Assert.NotNull(registeredSender.LastVerifiedUtc);
            }
            Assert.True(saves >= 2);
            Assert.Single(receiver.Peers);
        }
        finally { discovery.Stop(); server.Stop(); }
    }
}
