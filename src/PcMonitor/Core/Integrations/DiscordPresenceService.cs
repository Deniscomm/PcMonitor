using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using PcMonitor.Core.Diagnostics;

namespace PcMonitor.Core.Integrations;

/// <summary>Publishes PC Monitor activity through Discord's local IPC endpoint.</summary>
internal sealed class DiscordPresenceService : IDisposable
{
    private const int MaximumFrameLength = 1024 * 1024;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly string _applicationId;
    private readonly string _details;
    private readonly string _state;
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _worker;
    private bool _disposed;

    public DiscordPresenceService(string applicationId, string details, string state)
    {
        _applicationId = applicationId;
        _details = details;
        _state = state;
    }

    public string ApplicationId => _applicationId;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _worker ??= RunAsync(_cancellation.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool failureReported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool connected = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                if (connected) failureReported = false;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!failureReported)
                {
                    StartupDiagnostics.Error("discord.rpc", exception);
                    failureReported = true;
                }

                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        for (int index = 0; index < 10; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pipe = new NamedPipeClientStream(
                ".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            await RunSessionAsync(pipe, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task RunSessionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] handshake = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = _applicationId });
        await WriteFrameAsync(pipe, 0, handshake, cancellationToken).ConfigureAwait(false);

        (int opcode, byte[] payload) = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
        if (opcode != 1 || !IsReady(payload))
            throw new InvalidDataException("Discord RPC handshake was rejected.");

        byte[] activity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid = Environment.ProcessId,
                activity = new
                {
                    details = _details,
                    state = _state,
                    type = 0,
                    timestamps = new { start = _startedAt },
                },
            },
            nonce = Guid.NewGuid().ToString("N"),
        });
        await WriteFrameAsync(pipe, 1, activity, cancellationToken).ConfigureAwait(false);
        StartupDiagnostics.Mark("discord.rpc.connected");

        while (!cancellationToken.IsCancellationRequested)
        {
            (opcode, payload) = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (opcode == 2) return;
            if (opcode == 3)
                await WriteFrameAsync(pipe, 4, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsReady(byte[] payload)
    {
        using JsonDocument response = JsonDocument.Parse(payload);
        return response.RootElement.TryGetProperty("evt", out JsonElement eventName) &&
               eventName.ValueKind == JsonValueKind.String &&
               eventName.GetString() == "READY";
    }

    private static async Task WriteFrameAsync(
        Stream stream, int opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > MaximumFrameLength)
            throw new InvalidDataException("Discord RPC frame length is invalid.");

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (opcode, payload);
    }

    private static async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
