using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WindowsChildSession;

internal static class LocalPipe
{
    private const int MaxMessage = 128 * 1024;
    public static string HostName(uint id) => $"WindowsChildSession.v1.{WindowsSessions.UserSid}.host.{id}";
    public static string ExecutorName(uint id) => $"WindowsChildSession.v1.{WindowsSessions.UserSid}.executor.{id}";

    public static async Task<PipeReply> Call(string name, uint sessionId, PipeRequest request, CancellationToken token = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
        VerifyPeer(pipe.SafePipeHandle, sessionId, client: false);
        await Send(pipe, request, limit.Token).ConfigureAwait(false);
        var reply = await Receive<PipeReply>(pipe, limit.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[1], limit.Token).ConfigureAwait(false);
        return reply;
    }

    public static async Task Serve(NamedPipeServerStream pipe, uint peerSession,
        Func<PipeRequest, Task<PipeReply>> handle, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 109 or 232 or 233)
            {
                // The client abandoned a pending connection before the handshake started.
                DisconnectNamedPipe(pipe.SafePipeHandle);
                continue;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                VerifyPeer(pipe.SafePipeHandle, peerSession, client: true);
                var request = await Receive<PipeRequest>(pipe, limit.Token).ConfigureAwait(false);
                var result = await handle(request).ConfigureAwait(false);
                await SendReply(pipe, result, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A peer may close after submitting. Keep serving the next connection.
                try { await SendReply(pipe, new PipeReply("error", Error: ex.Message), limit.Token).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or OperationCanceledException) { }
            }
            finally
            {
                pipe.Disconnect();
            }
        }
    }

    private static async Task SendReply(Stream stream, PipeReply reply, CancellationToken token)
    {
        await Send(stream, reply, token).ConfigureAwait(false);
        // DisconnectNamedPipe discards unread bytes. Wait for the reader before recycling the instance.
        await stream.ReadExactlyAsync(new byte[1], token).ConfigureAwait(false);
    }

    public static NamedPipeServerStream OpenServer(string name)
    {
        // The single instance rejects network clients before the application handshake.
        const uint duplexOverlappedFirst = 0x00000003 | 0x40000000 | 0x00080000;
        const uint rejectRemoteClients = 0x00000008;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{WindowsSessions.UserSid})",
                1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe($@"\\.\pipe\{name}", duplexOverlappedFirst, rejectRemoteClients,
                1, 65536, 65536, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = new Win32Exception();
                handle.Dispose();
                throw error;
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void VerifyPeer(SafePipeHandle pipe, uint session, bool client)
    {
        var ok = client ? GetNamedPipeClientProcessId(pipe, out var pid) : GetNamedPipeServerProcessId(pipe, out pid);
        if (!ok)
            throw new Win32Exception();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x000A, out var token))
            throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (WindowsSessions.ForProcess(pid) != session || identity.User?.Value != WindowsSessions.UserSid ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException($"The pipe peer is not the ordinary user in expected session {session}.");
        }
    }

    private static async Task Send<T>(Stream stream, T message, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Cli.Json);
        if (body.Length > MaxMessage)
            throw new InvalidDataException("The IPC message is larger than 128 KiB.");
        var size = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        await stream.WriteAsync(size, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static async Task<T> Receive<T>(Stream stream, CancellationToken token)
    {
        var size = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxMessage)
            throw new InvalidDataException("Invalid IPC message length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Cli.Json) ?? throw new InvalidDataException("Empty IPC message.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DisconnectNamedPipe(SafePipeHandle pipe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
