using System.Net;
using IntraDrop.Models;

namespace IntraDrop.Core;

/// <summary>Bounded authenticated refresh coordinator. Broker data is candidate-only.</summary>
public sealed class PeerRefreshCoordinator
{
    private readonly AppSettings _settings;
    private readonly PeerRegistry _registry;
    private readonly Action _save;
    public PeerRefreshCoordinator(AppSettings settings, PeerRegistry registry, Action save)
    { _settings = settings; _registry = registry; _save = save; }

    public async Task<string?> RefreshAsync(PeerInfo target, CancellationToken ct = default)
    {
        var secret = SettingsStore.ReadSecret(_settings);
        if (!secret.IsAvailable || string.IsNullOrWhiteSpace(target.DeviceId)) return null;
        string expectedHost = target.Host;
        try
        {
            var direct = await TransferClient.RediscoverAsync(expectedHost, _settings.Port, _settings.DeviceName, _settings.DeviceId, target.DeviceId, secret.Secret!, 5000, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var current = SettingsStore.ReadSecret(_settings);
            if (!current.IsAvailable || !string.Equals(current.Secret, secret.Secret, StringComparison.Ordinal)) return null;
            if (string.Equals(direct.SenderDeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase) && _registry.TryConfirmVerified(target.DeviceId, expectedHost, expectedHost, direct.ComputerName, out _)) { _save(); return direct.SenderName; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        var helpers = _registry.Snapshot().Where(p => !string.Equals(p.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase) && !string.Equals(p.DeviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.DeviceId)).OrderByDescending(p => p.LastVerifiedUtc ?? DateTime.MinValue).Take(3).ToList();
        foreach (var helper in helpers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hints = await TransferClient.RequestPeerSnapshotAsync(helper.Host, _settings.Port, _settings.DeviceName, _settings.DeviceId, helper.DeviceId, secret.Secret!, 5000, new[] { target.DeviceId }, ct).ConfigureAwait(false);
                foreach (var hint in hints)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.Equals(hint.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase) || !IPAddress.TryParse(hint.Host, out _) || string.Equals(hint.Host, expectedHost, StringComparison.OrdinalIgnoreCase)) continue;
                    var identity = await TransferClient.RediscoverAsync(hint.Host, _settings.Port, _settings.DeviceName, _settings.DeviceId, target.DeviceId, secret.Secret!, 5000, ct).ConfigureAwait(false);
                    if (!string.Equals(identity.Type, "identity", StringComparison.OrdinalIgnoreCase) || !string.Equals(identity.SenderDeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                    ct.ThrowIfCancellationRequested();
                    var currentSecret = SettingsStore.ReadSecret(_settings);
                    if (!currentSecret.IsAvailable || !string.Equals(currentSecret.Secret, secret.Secret, StringComparison.Ordinal)) return null;
                    if (_registry.TryConfirmVerified(target.DeviceId, expectedHost, hint.Host, identity.ComputerName, out _)) { _save(); return identity.SenderName; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }
}
