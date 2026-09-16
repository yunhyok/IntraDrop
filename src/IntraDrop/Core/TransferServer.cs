using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using IntraDrop.Models;

namespace IntraDrop.Core;

public class TransferServer
{
    private const int HeaderIdleMs = 30_000;
    private const int DataIdleMs = 60_000;
    private const int DnsTimeoutMs = 2_000;
    private const int DrainIdleMs = 3_000;
    private const long DrainMaxBytes = 4L << 20;

    private static readonly string AppVersion =
        typeof(TransferServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private PeerRegistry? _peerRegistry;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public TransferServer()
    {
        // 기본값은 저장된 설정에서 직접 읽는다 (UI 가 배선하면 그쪽이 우선).
        GetSecret = () => SettingsStore.GetSecret(GetSettings());
    }

    public Func<AppSettings> GetSettings { get; set; } = () => new AppSettings();
    public PeerRegistry? PeerRegistry { get => _peerRegistry; set => _peerRegistry = value; }

    /// <summary>이 컴퓨터의 공유 암호(평문). 빈 문자열이면 인증·암호화 없음.</summary>
    public Func<string> GetSecret { get; set; }

    /// <summary>임계 크기 이상 수신 요청. UI 스레드에서 수락 여부를 결정해 돌려준다.</summary>
    public Func<TransferHeader, bool>? ConfirmRequest { get; set; }

    /// <summary>검증과 파일 저장이 끝난 클립보드 데이터를 UI STA 스레드에 적용한다.</summary>
    public Func<string, ClipboardContent, Task>? ApplyClipboardAsync { get; set; }

    /// <summary>자동 등록으로 peers 목록이 바뀐 뒤 저장을 맡길 콜백.</summary>
    public Action? SavePeers { get; set; }

    public event Action<string, int, string>? TransferCompleted;   // 보낸이, 파일 수, 저장 폴더
    public event Action<string, string>? TransferFailed;           // 보낸이, 사유
    public event Action<string, long>? TransferRejected;           // 보낸이, 크기
    public event Action<string, string>? PeerAutoRegistered;       // 별명, IP
    public event Action? PeerAddressChanged;

    private void PersistPeers()
    {
        try
        {
            // UI callback owns persistence so registry snapshots stay synchronized.
            if (SavePeers != null) SavePeers();
            else _peerRegistry?.Save();
        }
        catch { }
    }

    public bool IsRunning => _listener != null;

    public void Start(int port, IPAddress? bindAddress = null)
    {
        Stop();
        var listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        listener.Start();
        _cts = new CancellationTokenSource();
        _listener = listener;
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string sender = "알 수 없음";
        bool headerReceived = false;
        try
        {
            using var _ = client;

            IPAddress? remote = null;
            try { remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address; } catch { }

            using var net = client.GetStream();
            using var stream = new TimeoutStream(net, HeaderIdleMs, HeaderIdleMs, ct);

            // 1. 매직 + 플래그. IDRP1(구버전)이나 불일치는 예외 → 조용히 종료.
            byte flags = await Protocol.ReadMagicAsync(stream, ct);
            if (flags != Protocol.FlagPlain && flags != Protocol.FlagSecured)
                return;

            // 2. nonce 는 플래그 수신 직후 항상 보낸다 (재전송 공격 방지용).
            byte[] nonce = Crypto.NewNonce();
            await stream.WriteAsync(nonce, 0, nonce.Length, ct);
            await stream.FlushAsync(ct);

            var settings = GetSettings();
            var secretState = SettingsStore.ReadSecret(settings);
            // A protected value that cannot be decrypted is not "no secret". Fail closed.
            if (secretState.Availability == SettingsStore.SecretAvailability.Unavailable)
            {
                await RejectAsync(stream, Protocol.StatusAuthFailed, ct);
                return;
            }
            string? effectiveSecret = secretState.Availability == SettingsStore.SecretAvailability.Available
                ? secretState.Secret : SafeGetSecret();
            var serverKey = KeyMaterial.FromSecret(effectiveSecret);

            // 서버에 암호가 없으면 암호화 연결을 복호할 수 없다 → 즉시 거부.
            if (flags == Protocol.FlagSecured && serverKey == null)
            {
                await RejectAsync(stream, Protocol.StatusNoServerSecret, ct);
                return;
            }

            var segmentKey = flags == Protocol.FlagSecured ? serverKey : null;

            // 3. 세그먼트 A = 헤더 JSON. 복호·MAC 검증 실패는 인증 실패로 응답.
            byte[]? json = await TryReadHeaderSegmentAsync(stream, segmentKey, nonce, ct);
            if (json == null)
            {
                await RejectAsync(stream, Protocol.StatusAuthFailed, ct);
                return;
            }

            var header = Protocol.FromJsonBytes<TransferHeader>(json);
            headerReceived = true;
            sender = string.IsNullOrWhiteSpace(header.SenderName) ? sender : header.SenderName;

            // ping 은 언제나 평문이며 서버 암호 설정과 무관하게 응답한다 (온라인 표시용).
            if (header.Type == "ping")
            {
                await Protocol.WriteJsonAsync(stream,
                    new PongMessage { Name = settings.DeviceName, Version = AppVersion }, ct);
                return;
            }

            // 서버에 암호가 있는데 평문으로 붙었으면 거부.
            if (flags == Protocol.FlagPlain && serverKey != null)
            {
                await RejectAsync(stream, Protocol.StatusSecretRequired, ct);
                return;
            }

        if (header.Type == "register")
            {
                await HandleRegisterAsync(stream, header, segmentKey, nonce, remote, settings, ct);
                return;
            }
            if (header.Type == "rediscover")
            {
                await HandleRediscoverAsync(stream, header, segmentKey, nonce, remote, settings, ct);
                return;
            }
            if (header.Type == "peer_snapshot")
            {
                await HandlePeerSnapshotAsync(stream, header, segmentKey, nonce, settings, ct);
                return;
            }

            if (header.Type != "transfer" && header.Type != "clipboard")
                return;

            if (!string.IsNullOrWhiteSpace(header.RecipientDeviceId) &&
                !string.Equals(header.RecipientDeviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(stream, Protocol.StatusWrongDevice, ct);
                return;
            }

            if (header.Type == "clipboard")
                await ReceiveClipboardAsync(stream, header, segmentKey, nonce, remote, settings, ct);
            else
                await ReceiveTransferAsync(stream, header, segmentKey, nonce, remote, settings, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 헤더도 받기 전에 끊긴 연결(포트 스캔, 상태 확인 등)은 조용히 무시
            if (headerReceived)
                TransferFailed?.Invoke(sender, ex.Message);
        }
    }

    private string SafeGetSecret()
    {
        try { return GetSecret() ?? ""; }
        catch { return ""; }
    }

    /// <summary>거부 상태 코드를 보내고 연결을 정상 종료한다.
    /// 클라이언트가 아직 세그먼트를 보내는 중일 수 있으므로 남은 데이터를 잠깐 흘려보낸다 —
    /// 받지 않은 데이터를 남긴 채 닫으면 TCP RST 가 나가 클라이언트가 상태 코드조차 읽지 못한다.</summary>
    private static async Task RejectAsync(TimeoutStream stream, byte status, CancellationToken ct)
    {
        await Protocol.WriteByteAsync(stream, status, ct);
        try
        {
            stream.ReadIdleMs = DrainIdleMs;
            byte[] sink = new byte[8192];
            long budget = DrainMaxBytes;
            while (budget > 0)
            {
                int n = await stream.ReadAsync(sink, 0, sink.Length, ct);
                if (n == 0) break;   // 상대가 연결을 닫음
                budget -= n;
            }
        }
        catch { /* 이미 끊긴 연결이면 그대로 종료 */ }
    }

    private static async Task<byte[]?> TryReadHeaderSegmentAsync(
        Stream stream, KeyMaterial? key, byte[] nonce, CancellationToken ct)
    {
        try
        {
            return await Segment.ReadSegmentAsync(
                stream, key, nonce, Segment.IndexA, Protocol.MaxJsonLength, ct);
        }
        catch (SegmentAuthException) { return null; }
        catch (CryptographicException) { return null; }   // 패딩 오류 등 = 잘못된 키
    }

    // ── register: 상호 자동 등록 ─────────────────────────────────────────

    private async Task HandleRegisterAsync(
        TimeoutStream stream, TransferHeader header, KeyMaterial? segmentKeyForRegister, byte[] registerNonce,
        IPAddress? remote, AppSettings settings, CancellationToken ct)
    {
        if (remote == null)
        {
            await RejectAsync(stream, Protocol.StatusRefused, ct);
            return;
        }

        string ip = Normalize(remote).ToString();
        string nickname = string.IsNullOrWhiteSpace(header.SenderName) ? ip : header.SenderName.Trim();

        bool added = false;
        bool changed = false;
        bool rejected = false;
        var registry = _peerRegistry ?? new PeerRegistry(settings);
        if (segmentKeyForRegister != null && !string.IsNullOrWhiteSpace(header.RecipientDeviceId) && !string.Equals(header.RecipientDeviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase))
            rejected = true;
        else if (segmentKeyForRegister != null && !string.IsNullOrWhiteSpace(header.SenderDeviceId))
        {
            changed = registry.TryRegisterAuthenticated(header.SenderDeviceId, ip, nickname, out added, header.ComputerName);
            if (!changed) rejected = true;
        }
        else if (!registry.Snapshot().Any(p => string.Equals((p.Host ?? "").Trim(), ip, StringComparison.OrdinalIgnoreCase)))
            changed = registry.Add(new PeerInfo { Nickname = nickname, Host = ip });

        if (rejected) { await RejectAsync(stream, Protocol.StatusRefused, ct); return; }

        if (changed)
        {
            PersistPeers();
            if (added || segmentKeyForRegister == null) PeerAutoRegistered?.Invoke(nickname, ip);
            else PeerAddressChanged?.Invoke();
        }

        await Protocol.WriteByteAsync(stream, Protocol.StatusAccepted, ct);
        if (!string.IsNullOrWhiteSpace(header.SenderDeviceId) && segmentKeyForRegister != null)
        {
            try { await Segment.WriteSegmentAsync(stream, segmentKeyForRegister, registerNonce, Segment.IndexC,
                Protocol.ToJsonBytes(new TransferHeader { Type = "identity", SenderName = settings.DeviceName, ComputerName = Environment.MachineName, SenderDeviceId = settings.DeviceId }), ct); } catch { }
        }
    }

    private async Task HandleRediscoverAsync(TimeoutStream stream, TransferHeader header, KeyMaterial? key,
        byte[] nonce, IPAddress? remote, AppSettings settings, CancellationToken ct)
    {
        if (key == null || remote == null || string.IsNullOrWhiteSpace(header.SenderDeviceId) ||
            (!string.IsNullOrWhiteSpace(header.RecipientDeviceId) && !string.Equals(header.RecipientDeviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase)))
        { await RejectAsync(stream, Protocol.StatusAuthFailed, ct); return; }
        var registry = _peerRegistry ?? new PeerRegistry(settings);
        var peers = registry.Snapshot().Where(p => string.Equals(p.DeviceId, header.SenderDeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (peers.Count != 1) { await RejectAsync(stream, Protocol.StatusNotRegistered, ct); return; }
        string host = Normalize(remote).ToString();
        bool confirmed = registry.TryConfirmVerified(header.SenderDeviceId, peers[0].Host, host, header.ComputerName, out bool hostChanged);
        if (!confirmed)
        { await RejectAsync(stream, Protocol.StatusRefused, ct); return; }
        PersistPeers();
        await Protocol.WriteByteAsync(stream, Protocol.StatusAccepted, ct);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexC,
            Protocol.ToJsonBytes(new TransferHeader { Type = "identity", SenderName = settings.DeviceName, ComputerName = Environment.MachineName, SenderDeviceId = settings.DeviceId }), ct);
        if (hostChanged) PeerAddressChanged?.Invoke();
    }

    private async Task HandlePeerSnapshotAsync(TimeoutStream stream, TransferHeader header, KeyMaterial? key,
        byte[] nonce, AppSettings settings, CancellationToken ct)
    {
        if (key == null || string.IsNullOrWhiteSpace(header.SenderDeviceId) ||
            !string.Equals(header.RecipientDeviceId, settings.DeviceId, StringComparison.OrdinalIgnoreCase))
        { await RejectAsync(stream, Protocol.StatusAuthFailed, ct); return; }
        if (header.RequestedDeviceIds == null || header.RequestedDeviceIds.Count == 0 || header.RequestedDeviceIds.Count > 64) { await RejectAsync(stream, Protocol.StatusRefused, ct); return; }
        if (header.RequestedDeviceIds.Any(id => !Guid.TryParse(id, out _)) || header.RequestedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.RequestedDeviceIds.Count)
        { await RejectAsync(stream, Protocol.StatusRefused, ct); return; }
        var registry = _peerRegistry ?? new PeerRegistry(settings);
        var sender = registry.Snapshot().Where(p => string.Equals(p.DeviceId, header.SenderDeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sender.Count != 1) { await RejectAsync(stream, Protocol.StatusNotRegistered, ct); return; }
        var requested = header.RequestedDeviceIds.Where(id => Guid.TryParse(id, out _)).Take(64).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hints = registry.Snapshot().Where(p => !string.IsNullOrWhiteSpace(p.DeviceId) && requested.Contains(p.DeviceId) && Guid.TryParse(p.DeviceId, out _) && IPAddress.TryParse(p.Host, out _) && p.LastVerifiedUtc.HasValue && p.LastVerifiedUtc.Value <= DateTime.UtcNow.AddSeconds(30) && DateTime.UtcNow - p.LastVerifiedUtc.Value <= TimeSpan.FromMinutes(15))
            .Take(64).Select(p => new PeerHint { DeviceId = p.DeviceId, Host = p.Host }).ToList();
        await Protocol.WriteByteAsync(stream, Protocol.StatusAccepted, ct);
        await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexC,
            Protocol.ToJsonBytes(new TransferHeader { Type = "peer_snapshot", SenderDeviceId = settings.DeviceId, RecipientDeviceId = header.SenderDeviceId, PeerHints = hints }), ct);
    }

    // ── transfer ─────────────────────────────────────────────────────────

    private async Task ReceiveClipboardAsync(
        TimeoutStream stream, TransferHeader header, KeyMaterial? key, byte[] nonce,
        IPAddress? remote, AppSettings settings, CancellationToken ct)
    {
        string format = header.ClipboardFormat ?? "";
        List<ClipboardManifestItem>? manifest = null;
        long expected;

        if (key != null && !IsValidClipboardRequestId(header.ClipboardRequestId))
        {
            await RejectAsync(stream, Protocol.StatusRefused, ct);
            return;
        }

        if (format == "text" || format == "png")
        {
            long limit = format == "text" ? ClipboardContent.MaxTextBytes : ClipboardContent.MaxImageBytes;
            if (header.Items == null || header.Items.Count != 0 || header.TotalSize <= 0 || header.TotalSize > limit)
            {
                await RejectAsync(stream, Protocol.StatusRefused, ct);
                return;
            }
            expected = header.TotalSize;
        }
        else if (format == "files")
        {
            if (!TryBuildClipboardManifest(header, out manifest, out expected))
            {
                await RejectAsync(stream, Protocol.StatusRefused, ct);
                return;
            }
        }
        else
        {
            await RejectAsync(stream, Protocol.StatusRefused, ct);
            return;
        }

        if (!await AcceptTransferAsync(stream, header, key, remote, settings, expected,
                requireDiskSpace: format == "files", ct))
            return;

        stream.ReadIdleMs = DataIdleMs;
        if (format == "files")
        {
            await ReceiveClipboardFilesAsync(stream, header, key, nonce, settings, manifest!, expected, ct);
            return;
        }

        byte[] data;
        try
        {
            int limit = format == "text" ? ClipboardContent.MaxTextBytes : ClipboardContent.MaxImageBytes;
            data = await Segment.ReadSegmentAsync(stream, key, nonce, Segment.IndexB, limit, ct);
            if (data.LongLength != expected) throw new InvalidDataException("클립보드 데이터 크기가 헤더와 다릅니다.");
            ClipboardContent.ValidateData(format, data);
        }
        catch
        {
            await TryWriteClipboardFailureAsync(stream, ct);
            throw;
        }

        await ApplyClipboardAndAcknowledgeAsync(stream, header,
            new ClipboardContent { Format = format, Data = data }, key, nonce, settings, ct);
    }

    private async Task ReceiveClipboardFilesAsync(
        TimeoutStream stream, TransferHeader header, KeyMaterial? key, byte[] nonce,
        AppSettings settings, List<ClipboardManifestItem> manifest, long expected, CancellationToken ct)
    {
        string folder = Path.GetFullPath(settings.DownloadFolder);
        Directory.CreateDirectory(folder);
        string batchId = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(folder, ".intradrop-" + batchId + ".part");
        string batch = Path.Combine(folder, "Clipboard-" + batchId);
        Directory.CreateDirectory(stage);
        IReadOnlyList<string>? receivedRoots = null;
        try
        {
            using var reader = await Segment.ReadSegmentHeaderAsync(
                stream, key, nonce, Segment.IndexB, expected, ct);
            if (reader.PlainLength != expected)
                throw new InvalidDataException("클립보드 파일 크기가 헤더와 다릅니다.");

            byte[] buffer = new byte[81920];
            foreach (var item in manifest)
            {
                string destination = Path.Combine(stage, item.RelPath);
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long remaining = item.Size;
                while (remaining > 0)
                {
                    int read = await reader.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), ct);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }
            await reader.CompleteAsync(ct);

            Directory.Move(stage, batch);
            stage = "";
            receivedRoots = manifest.Where(item => item.IsRoot)
                .Select(item => Path.Combine(batch, item.RelPath)).ToArray();
        }
        catch
        {
            try { if (stage.Length != 0 && Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            await TryWriteClipboardFailureAsync(stream, ct);
            throw;
        }

        await ApplyClipboardAndAcknowledgeAsync(stream, header,
            new ClipboardContent { Format = "files", Paths = receivedRoots }, key, nonce, settings, ct);
    }

    private async Task ApplyClipboardAndAcknowledgeAsync(
        TimeoutStream stream, TransferHeader header, ClipboardContent content,
        KeyMaterial? key, byte[] nonce, AppSettings settings, CancellationToken ct)
    {
        try
        {
            var apply = ApplyClipboardAsync ?? throw new InvalidOperationException("클립보드를 적용할 수 없습니다.");
            await apply(string.IsNullOrWhiteSpace(header.SenderName) ? "알 수 없음" : header.SenderName, content);
        }
        catch
        {
            await TryWriteClipboardFailureAsync(stream, ct);
            throw;
        }
        await Protocol.WriteByteAsync(stream, 1, ct);
        if (key != null)
            await Segment.WriteSegmentAsync(stream, key, nonce, Segment.IndexC, Protocol.ToJsonBytes(new TransferHeader
            {
                Type = "clipboard_applied", ClipboardFormat = header.ClipboardFormat,
                ClipboardRequestId = header.ClipboardRequestId,
                SenderDeviceId = settings.DeviceId, RecipientDeviceId = header.SenderDeviceId,
            }), ct);
    }

    private static bool IsValidClipboardRequestId(string? value)
    {
        if (value == null || value.Length != 24) return false;
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            return bytes.Length == KeyMaterial.NonceLength &&
                   string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task TryWriteClipboardFailureAsync(TimeoutStream stream, CancellationToken ct)
    {
        try { await Protocol.WriteByteAsync(stream, 0, ct); } catch { }
    }

    private async Task<bool> AcceptTransferAsync(
        TimeoutStream stream, TransferHeader header, KeyMaterial? key, IPAddress? remote,
        AppSettings settings, long expected, bool requireDiskSpace, CancellationToken ct)
    {
        string sender = string.IsNullOrWhiteSpace(header.SenderName) ? "알 수 없음" : header.SenderName;
        if (settings.AcceptFromRegisteredOnly)
        {
            bool known = key != null && !string.IsNullOrWhiteSpace(header.SenderDeviceId)
                ? remote != null && IsRegisteredDevice(settings, header.SenderDeviceId, remote)
                : remote != null && await IsRegisteredAsync(settings, remote);
            if (!known)
            {
                await RejectAsync(stream, Protocol.StatusNotRegistered, ct);
                return false;
            }
        }

        if (requireDiskSpace && !HasEnoughDiskSpace(settings.DownloadFolder, expected))
        {
            await RejectAsync(stream, Protocol.StatusRefused, ct);
            TransferFailed?.Invoke(sender, "저장 공간이 부족합니다.");
            return false;
        }

        if (expected >= settings.ConfirmThresholdBytes && !(ConfirmRequest?.Invoke(header) ?? false))
        {
            await RejectAsync(stream, Protocol.StatusRejected, ct);
            TransferRejected?.Invoke(sender, expected);
            return false;
        }

        await Protocol.WriteByteAsync(stream, Protocol.StatusAccepted, ct);
        return true;
    }

    private static bool HasEnoughDiskSpace(string folder, long expected)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "C:\\";
            long available = new DriveInfo(root).AvailableFreeSpace;
            return expected <= available && available - expected >= (64L << 20);
        }
        catch { return true; }
    }

    private static bool TryBuildClipboardManifest(
        TransferHeader header, out List<ClipboardManifestItem>? manifest, out long expected)
    {
        manifest = null;
        expected = 0;
        if (header.Items == null || header.Items.Count == 0 || header.Items.Count > Protocol.MaxItemCount)
            return false;

        var result = new List<ClipboardManifestItem>(header.Items.Count);
        var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in header.Items)
            {
                if (item == null || !TryValidateClipboardRelativePath(item.Path, out string? relativePath))
                    return false;
                if (kinds.ContainsKey(relativePath)) return false;
                kinds.Add(relativePath, item.IsDirectory);
                if (item.IsDirectory)
                {
                    if (item.Size != 0) return false;
                }
                else
                {
                    if (item.Size < 0) return false;
                    expected = checked(expected + item.Size);
                }
                result.Add(new ClipboardManifestItem(relativePath, item.Size, item.IsDirectory));
            }
        }
        catch (OverflowException) { return false; }

        if (expected != header.TotalSize) return false;
        foreach (var item in result)
        {
            string[] parts = item.RelPath.Split(Path.DirectorySeparatorChar);
            string parent = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (!kinds.TryGetValue(parent, out bool isDirectory) || !isDirectory) return false;
                parent = Path.Combine(parent, parts[i]);
            }
        }
        if (!result.Any(item => item.IsRoot)) return false;
        manifest = result;
        return true;
    }

    private static bool TryValidateClipboardRelativePath(string? raw, out string relativePath)
    {
        relativePath = "";
        if (raw == null || string.IsNullOrWhiteSpace(raw) || raw.Length > 32767 || raw.IndexOf('\0') >= 0 ||
            raw.IndexOf('\\') >= 0 || raw.IndexOf(':') >= 0 || raw[0] == '/' || Path.IsPathRooted(raw))
            return false;
        string[] parts = raw.Split(new[] { '/' }, StringSplitOptions.None);
        if (parts.Length == 0) return false;
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." ||
                !string.Equals(part, part.TrimEnd(' ', '.'), StringComparison.Ordinal) ||
                part.IndexOfAny(invalid) >= 0 || IsReservedWindowsName(part))
                return false;
        }
        relativePath = Path.Combine(parts);
        return true;
    }

    private static bool IsReservedWindowsName(string part)
    {
        string name = part.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$") return true;
        return name.Length == 4 && name[3] >= '1' && name[3] <= '9' &&
               (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal));
    }

    private async Task ReceiveTransferAsync(
        TimeoutStream stream, TransferHeader header, KeyMaterial? key, byte[] nonce,
        IPAddress? remote, AppSettings settings, CancellationToken ct)
    {
        string sender = string.IsNullOrWhiteSpace(header.SenderName) ? "알 수 없음" : header.SenderName;

        // 경로 검증 (경로 탈출 차단)
        if (header.Items.Count > Protocol.MaxItemCount)
            throw new InvalidDataException("파일 개수가 너무 많습니다.");

        var items = new List<(string RelPath, long Size)>();
        long expected = 0;
        foreach (var item in header.Items)
        {
            string? safe = SanitizeRelativePath(item.Path);
            if (safe == null || item.Size < 0)
                throw new InvalidDataException("잘못된 파일 경로가 포함되어 있습니다.");
            expected += item.Size;
            if (expected < 0)
                throw new InvalidDataException("전송 크기가 잘못되었습니다.");
            items.Add((safe, item.Size));
        }
        if (items.Count == 0)
            throw new InvalidDataException("받을 파일이 없습니다.");
        if (expected != header.TotalSize)
            throw new InvalidDataException("전송 크기가 헤더와 다릅니다.");

        if (!await AcceptTransferAsync(stream, header, key, remote, settings, expected,
                requireDiskSpace: true, ct))
            return;

        string folder = settings.DownloadFolder;
        Directory.CreateDirectory(folder);

        stream.ReadIdleMs = DataIdleMs;

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string PartPath, string DestPath)>();
        try
        {
            // 세그먼트 B = 모든 파일 바이트 연속
            using var reader = await Segment.ReadSegmentHeaderAsync(
                stream, key, nonce, Segment.IndexB, expected, ct);
            if (reader.PlainLength != expected)
                throw new InvalidDataException("전송 크기가 헤더와 다릅니다.");

            byte[] buffer = new byte[81920];
            foreach (var (relPath, size) in items)
            {
                string dest = Path.Combine(folder, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                string partPath = Path.Combine(Path.GetDirectoryName(dest)!, Guid.NewGuid().ToString("N") + ".part");
                using var fs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                pending.Add((partPath, dest));

                long remaining = size;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int n = await reader.ReadAsync(buffer, 0, toRead, ct);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }

            // MAC 검증 — 실패하면 여기서 예외가 나고 .part 는 모두 지워진다 (ack 없음).
            await reader.CompleteAsync(ct);
        }
        catch
        {
            DeleteParts(pending);
            throw;
        }

        try
        {
            foreach (var (partPath, dest) in pending)
            {
                while (true)
                {
                    string unique = MakeUniquePath(dest, reserved);
                    try { File.Move(partPath, unique); break; }
                    // Another transfer can claim the name between the check and the move.
                    catch (IOException) when (File.Exists(unique) || Directory.Exists(unique)) { }
                }
            }
        }
        catch
        {
            DeleteParts(pending);
            throw;
        }

        await Protocol.WriteByteAsync(stream, 1, ct);   // 저장 완료 응답

        TransferCompleted?.Invoke(sender, items.Count, folder);
    }

    private bool IsRegisteredDevice(AppSettings settings, string deviceId, IPAddress remote)
    {
        var matches = (_peerRegistry ?? new PeerRegistry(settings)).Snapshot().Where(p => string.Equals(p.DeviceId?.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 && IPAddress.TryParse(matches[0].Host?.Trim(), out var parsed) && SameAddress(parsed, remote);
    }

    private static void DeleteParts(List<(string PartPath, string DestPath)> pending)
    {
        foreach (var (partPath, _) in pending)
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    // ── 허용 목록 확인 ───────────────────────────────────────────────────

    /// <summary>원격 IP 가 등록된 peer 인지 확인한다.
    /// IP 문자열은 즉시 비교하고, 이름으로 등록된 peer 만 DNS 로 조회한다(전체 2초 제한).</summary>
    private async Task<bool> IsRegisteredAsync(AppSettings settings, IPAddress remote)
    {
        List<PeerInfo> peers = (_peerRegistry ?? new PeerRegistry(settings)).Snapshot().ToList();

        var names = new List<string>();
        foreach (var peer in peers)
        {
            string host = (peer.Host ?? "").Trim();
            if (host.Length == 0) continue;

            if (IPAddress.TryParse(host, out var parsed))
            {
                if (SameAddress(parsed, remote)) return true;
            }
            else
            {
                names.Add(host);
            }
        }
        if (names.Count == 0) return false;

        var lookups = names.Select(SafeResolveAsync).ToList();
        using (var delayCts = new CancellationTokenSource())
        {
            Task all = Task.WhenAll(lookups);
            Task done = await Task.WhenAny(all, Task.Delay(DnsTimeoutMs, delayCts.Token)).ConfigureAwait(false);
            if (done == all) delayCts.Cancel();   // 조회 완료 시 Delay 타이머 즉시 해제
        }

        foreach (var lookup in lookups)
        {
            if (lookup.Status != TaskStatus.RanToCompletion) continue;   // 실패·미완료 peer 는 무시
            if (lookup.Result.Any(a => SameAddress(a, remote))) return true;
        }
        return false;
    }

    private static async Task<IPAddress[]> SafeResolveAsync(string host)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool SameAddress(IPAddress a, IPAddress b) =>
        Normalize(a).Equals(Normalize(b));

    // ── 경로 처리 ────────────────────────────────────────────────────────

    private static string? SanitizeRelativePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string normalized = raw.Replace('\\', '/');
        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var safeParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part[part.Length - 1] == ':') return null;
            var chars = part.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string cleaned = new string(chars).TrimEnd(' ', '.');
            if (cleaned.Length == 0) return null;
            safeParts.Add(cleaned);
        }
        return Path.Combine(safeParts.ToArray());
    }

    /// <summary>고유한 저장 경로를 만든다. 같은 전송 안에서 이미 잡아둔 이름(reserved)도 피한다.</summary>
    private static string MakeUniquePath(string path, HashSet<string> reserved)
    {
        if (IsFree(path, reserved))
        {
            reserved.Add(path);
            return path;
        }

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (IsFree(candidate, reserved))
            {
                reserved.Add(candidate);
                return candidate;
            }
        }
    }

    private static bool IsFree(string path, HashSet<string> reserved) =>
        !reserved.Contains(path) && !File.Exists(path) && !Directory.Exists(path);

    private sealed class ClipboardManifestItem
    {
        public ClipboardManifestItem(string relPath, long size, bool isDirectory)
        { RelPath = relPath; Size = size; IsDirectory = isDirectory; }
        public string RelPath { get; }
        public long Size { get; }
        public bool IsDirectory { get; }
        public bool IsRoot => RelPath.IndexOf(Path.DirectorySeparatorChar) < 0;
    }
}
