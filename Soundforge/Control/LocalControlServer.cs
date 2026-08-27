using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Soundforge.Control;

/// <summary>
/// Listens on a Windows named pipe, which is local-machine only. This keeps early
/// control-surface work private while leaving the audio engine independent from UI.
/// </summary>
public sealed class LocalControlServer : IDisposable
{
    public const string PipeName = "Soundforge.StreamDeck";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<SoundforgeControlCommand, Task<SoundforgeControlResult>> _handleCommand;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;

    public LocalControlServer(Func<SoundforgeControlCommand, Task<SoundforgeControlResult>> handleCommand)
    {
        _handleCommand = handleCommand;
    }

    public void Start() => _listener ??= Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cancellation.Token);

                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var request = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(request))
                    continue;

                var command = JsonSerializer.Deserialize<SoundforgeControlCommand>(request, JsonOptions);
                var result = command is null
                    ? new SoundforgeControlResult(false, "The control request was invalid.")
                    : await _handleCommand(command);
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A disconnected control surface should never disturb live audio.
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
