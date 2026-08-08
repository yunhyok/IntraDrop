// .NET 8과 .NET Framework 4.8(Windows 7) 양쪽에서 동작하기 위한 호환 계층.

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // C# 9 record/init 접근자 컴파일용
    internal static class IsExternalInit { }
}
#endif

namespace IntraDrop.Core
{
    internal static class PathCompat
    {
        /// <summary>net48에는 Path.GetRelativePath 가 없다.
        /// 이 프로그램에서는 path 가 항상 relativeTo 하위이므로 접두사 제거로 충분하다.</summary>
        public static string GetRelativePath(string relativeTo, string path)
        {
#if NETFRAMEWORK
            string baseDir = Path.GetFullPath(relativeTo)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(baseDir.Length)
                : full;
#else
            return Path.GetRelativePath(relativeTo, path);
#endif
        }
    }
}

#if NETFRAMEWORK
namespace IntraDrop.Core
{
    using System.Net.Sockets;
    using System.Runtime.InteropServices;

    /// <summary>WhenAny 패턴에서 버려지는 태스크의 뒤늦은 예외를 소비해
    /// UnobservedTaskException 오염을 막는다.</summary>
    internal static class TaskObserver
    {
        public static void Observe(this Task task) =>
            task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>.NET 8 전용 API를 net48에서 동일한 호출 형태로 제공하는 확장 메서드.</summary>
    internal static class NetFrameworkExtensions
    {
        // Stream.ReadAsync(Memory<byte>, CancellationToken)
        public static Task<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken ct)
        {
            if (MemoryMarshal.TryGetArray<byte>(buffer, out ArraySegment<byte> seg))
                return stream.ReadAsync(seg.Array!, seg.Offset, seg.Count, ct);
            return ReadViaCopyAsync(stream, buffer, ct);
        }

        private static async Task<int> ReadViaCopyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
        {
            byte[] tmp = new byte[buffer.Length];
            int n = await stream.ReadAsync(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }

        // Stream.WriteAsync(ReadOnlyMemory<byte>, CancellationToken)
        public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg))
                return stream.WriteAsync(seg.Array!, seg.Offset, seg.Count, ct);
            byte[] tmp = buffer.ToArray();
            return stream.WriteAsync(tmp, 0, tmp.Length, ct);
        }

        // TcpClient.ConnectAsync(host, port, CancellationToken)
        public static async Task ConnectAsync(this TcpClient client, string host, int port, CancellationToken ct)
        {
            Task connect = client.ConnectAsync(host, port);
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task done = await Task.WhenAny(connect, Task.Delay(Timeout.Infinite, delayCts.Token)).ConfigureAwait(false);
                if (done != connect)
                {
                    connect.Observe();   // Close 이후 fault 하는 connect 예외 소비
                    try { client.Close(); } catch { }
                    throw new OperationCanceledException(ct);
                }
                delayCts.Cancel();   // 연결 완료 시 Delay 타이머·토큰 등록 해제
            }
            await connect.ConfigureAwait(false);   // 연결 실패 예외 전파
        }

        // TcpListener.AcceptTcpClientAsync(CancellationToken)
        public static async Task<TcpClient> AcceptTcpClientAsync(this TcpListener listener, CancellationToken ct)
        {
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                try
                {
                    return await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (InvalidOperationException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }
    }
}
#endif
