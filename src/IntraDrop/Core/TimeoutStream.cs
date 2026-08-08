namespace IntraDrop.Core;

/// <summary>내부 스트림의 읽기/쓰기에 유휴 시간 제한을 적용하는 래퍼 스트림.
/// NetworkStream 을 이 스트림으로 한 번 감싸두면 위에 얹는 CryptoStream 등
/// 모든 상위 I/O 에 동일한 시간 제한이 적용된다.
/// net48 에서는 소켓 I/O 가 CancellationToken 으로 중단되지 않으므로
/// WhenAny + Delay 즉시취소 + Observe 패턴을 쓴다.
/// (시간 초과 시 호출 측이 연결을 닫으면서 미완료 I/O 가 함께 정리된다.)</summary>
public sealed class TimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _ct;

    public TimeoutStream(Stream inner, int readIdleMs, int writeIdleMs,
                         CancellationToken ct, bool leaveOpen = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ReadIdleMs = readIdleMs;
        WriteIdleMs = writeIdleMs;
        _ct = ct;
        _leaveOpen = leaveOpen;
    }

    /// <summary>한 번의 읽기가 기다릴 수 있는 최대 시간(ms). 단계별로 바꿔 쓸 수 있다.</summary>
    public int ReadIdleMs { get; set; }

    /// <summary>한 번의 쓰기가 기다릴 수 있는 최대 시간(ms).</summary>
    public int WriteIdleMs { get; set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => true;

    public override int ReadTimeout { get => ReadIdleMs; set => ReadIdleMs = value; }
    public override int WriteTimeout { get => WriteIdleMs; set => WriteIdleMs = value; }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    // CryptoStream 의 FlushFinalBlock 등은 동기로 호출되므로 비동기 경로에 위임한다.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (!ct.CanBeCanceled || ct == _ct)
            return ReadCoreAsync(buffer, offset, count, _ct);
        return ReadLinkedAsync(buffer, offset, count, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (!ct.CanBeCanceled || ct == _ct)
            return WriteCoreAsync(buffer, offset, count, _ct);
        return WriteLinkedAsync(buffer, offset, count, ct);
    }

    private async Task<int> ReadLinkedAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
        return await ReadCoreAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
    }

    private async Task WriteLinkedAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
        await WriteCoreAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
    }

    private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
#if NETFRAMEWORK
        Task<int> read = _inner.ReadAsync(buffer, offset, count, ct);
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task done = await Task.WhenAny(read, Task.Delay(ReadIdleMs, delayCts.Token)).ConfigureAwait(false);
            if (done != read)
            {
                read.Observe();   // 버려지는 read 의 뒤늦은 예외를 소비
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("상대방 응답이 없습니다 (시간 초과).");
            }
            delayCts.Cancel();   // I/O 완료 시 Delay 타이머·토큰 등록 즉시 해제
        }
        return await read.ConfigureAwait(false);
#else
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(ReadIdleMs);
        try
        {
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("상대방 응답이 없습니다 (시간 초과).");
        }
#endif
    }

    private async Task WriteCoreAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
#if NETFRAMEWORK
        Task write = _inner.WriteAsync(buffer, offset, count, ct);
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task done = await Task.WhenAny(write, Task.Delay(WriteIdleMs, delayCts.Token)).ConfigureAwait(false);
            if (done != write)
            {
                write.Observe();   // 버려지는 write 의 뒤늦은 예외를 소비
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("상대방이 데이터를 받지 않습니다 (시간 초과).");
            }
            delayCts.Cancel();   // I/O 완료 시 Delay 타이머·토큰 등록 즉시 해제
        }
        await write.ConfigureAwait(false);
#else
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(WriteIdleMs);
        try
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("상대방이 데이터를 받지 않습니다 (시간 초과).");
        }
#endif
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            try { _inner.Dispose(); } catch { /* 이미 끊긴 연결 정리 실패는 무시 */ }
        }
        base.Dispose(disposing);
    }
}
