using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Mantelview.Services;

public enum SlideshowCommand
{
    Pause,
    Resume,
    Stop,
    Next,
}

public sealed class CommandListener : IDisposable
{
    private const string SocketFileName = "mantelview-control.sock";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Action<SlideshowCommand> _commandHandler;
    private readonly byte[]? _ipcSecretBytes;
    private readonly CancellationTokenSource _disposeCancellationSource = new();
    private Socket? _listenerSocket;
    private Task? _acceptLoopTask;
    private string? _socketPath;
    private int _activeConnection;
    private bool _isDisposed;

    public CommandListener(string? ipcSecret, Action<SlideshowCommand> commandHandler)
    {
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _ipcSecretBytes = string.IsNullOrWhiteSpace(ipcSecret)
            ? null
            : Encoding.UTF8.GetBytes(ipcSecret);
    }

    public string? SocketPath => _socketPath;

    public bool Start()
    {
        if (_isDisposed || !OperatingSystem.IsLinux())
        {
            return false;
        }

        var socketPath = ResolveSocketPath();
        var socketDirectory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(socketDirectory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(socketDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWarning($"CommandListener could not create socket directory '{socketDirectory}': {ex.Message}");
            return false;
        }

        if (File.Exists(socketPath))
        {
            try
            {
                File.Delete(socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWarning($"CommandListener could not delete existing socket '{socketPath}': {ex.Message}");
                return false;
            }
        }

        try
        {
            _listenerSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listenerSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listenerSocket.Listen(backlog: 1);
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _socketPath = socketPath;
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_disposeCancellationSource.Token));
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogWarning($"CommandListener failed to start at '{socketPath}': {ex.Message}");
            _listenerSocket?.Dispose();
            _listenerSocket = null;

            if (File.Exists(socketPath))
            {
                try
                {
                    File.Delete(socketPath);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    LogWarning($"CommandListener could not remove failed socket '{socketPath}': {cleanupEx.Message}");
                }
            }

            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeCancellationSource.Cancel();
        _listenerSocket?.Dispose();
        _listenerSocket = null;
        _disposeCancellationSource.Dispose();

        if (!string.IsNullOrWhiteSpace(_socketPath) && File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWarning($"CommandListener could not remove socket '{_socketPath}': {ex.Message}");
            }
        }
    }

    public static string ResolveSocketPath()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            runtimeDirectory = "/tmp";
        }

        return Path.Combine(runtimeDirectory, SocketFileName);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listenerSocket is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;

            try
            {
                client = await _listenerSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                LogWarning($"CommandListener accept failed: {ex.Message}");
                continue;
            }

            if (Interlocked.CompareExchange(ref _activeConnection, 1, 0) != 0)
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => ProcessClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ProcessClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var payload = await ReadPayloadAsync(client, cancellationToken).ConfigureAwait(false);
                if (payload.Length == 0 || !TryParseCommand(payload, out var command))
                {
                    return;
                }

                Dispatcher.UIThread.Post(() => _commandHandler(command));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _activeConnection, 0);
            }
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadPayloadAsync(Socket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[GetBufferLength()];
        using var readTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeoutCancellation.CancelAfter(ReadTimeout);

        int bytesRead;
        try
        {
            bytesRead = await client.ReceiveAsync(buffer, SocketFlags.None, readTimeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (bytesRead <= 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (HasBufferedOverflow(client))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return new ReadOnlyMemory<byte>(buffer, 0, bytesRead);
    }

    private int GetBufferLength()
    {
        return _ipcSecretBytes is null
            ? 2
            : _ipcSecretBytes.Length + 3;
    }

    private bool TryParseCommand(ReadOnlyMemory<byte> payloadMemory, out SlideshowCommand command)
    {
        command = default;
        var payload = TrimLineTerminators(payloadMemory.Span);
        if (payload.Length == 0)
        {
            return false;
        }

        if (_ipcSecretBytes is null)
        {
            return payload.Length == 1 && TryMapCommand(payload[0], out command);
        }

        if (payload.Length != _ipcSecretBytes.Length + 2)
        {
            command = default;
            return false;
        }

        if (!payload[.._ipcSecretBytes.Length].SequenceEqual(_ipcSecretBytes))
        {
            command = default;
            return false;
        }

        if (payload[_ipcSecretBytes.Length] != (byte)':')
        {
            command = default;
            return false;
        }

        return TryMapCommand(payload[^1], out command);
    }

    private static ReadOnlySpan<byte> TrimLineTerminators(ReadOnlySpan<byte> payload)
    {
        while (payload.Length > 0 && (payload[^1] == (byte)'\n' || payload[^1] == (byte)'\r'))
        {
            payload = payload[..^1];
        }

        return payload;
    }

    private static bool TryMapCommand(byte value, out SlideshowCommand command)
    {
        command = value switch
        {
            (byte)'P' => SlideshowCommand.Pause,
            (byte)'R' => SlideshowCommand.Resume,
            (byte)'S' => SlideshowCommand.Stop,
            (byte)'N' => SlideshowCommand.Next,
            _ => default,
        };

        return value is (byte)'P' or (byte)'R' or (byte)'S' or (byte)'N';
    }

    private static bool HasBufferedOverflow(Socket client)
    {
        try
        {
            return client.Poll(10_000, SelectMode.SelectRead) && client.Available > 0;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void LogWarning(string message)
    {
        Trace.TraceWarning(message);
        Console.Error.WriteLine(message);
    }
}
