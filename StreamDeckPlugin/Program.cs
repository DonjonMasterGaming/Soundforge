using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

const string SceneAction = "com.soundforge.control.scene";
const string AmbienceAction = "com.soundforge.control.ambience";
const string EffectAction = "com.soundforge.control.effect";
const string StopAllAction = "com.soundforge.control.stop-all";
const string MasterAction = "com.soundforge.control.master-volume";
const string MusicAction = "com.soundforge.control.music-volume";
const string AmbienceVolumeAction = "com.soundforge.control.ambience-volume";
const string EffectsAction = "com.soundforge.control.effects-volume";

var port = GetArgument("-port");
var pluginUuid = GetArgument("-pluginUUID");
var registerEvent = GetArgument("-registerEvent");
if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(pluginUuid) || string.IsNullOrWhiteSpace(registerEvent))
    return;

using var socket = new ClientWebSocket();
await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
await SendSocketAsync(new { @event = registerEvent, uuid = pluginUuid });

while (socket.State == WebSocketState.Open)
{
    var message = await ReceiveSocketMessageAsync();
    if (message is null)
        break;

    try
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (!root.TryGetProperty("event", out var eventElement))
            continue;

        var eventName = eventElement.GetString();
        if (eventName == "sendToPlugin")
        {
            await SendChoicesToPropertyInspectorAsync(root);
            continue;
        }

        if (eventName is not "keyDown" and not "dialRotate")
            continue;

        var action = root.GetProperty("action").GetString() ?? string.Empty;
        var context = root.GetProperty("context").GetString() ?? string.Empty;
        var command = BuildCommand(action, root, eventName);
        if (command is null)
            continue;

        var result = await SendToSoundforgeAsync(command);
        if (eventName == "dialRotate" && result.Success && result.Level is double level)
        {
            await SendSocketAsync(new
            {
                @event = "setFeedback",
                context,
                payload = new
                {
                    title = GetDialTitle(action),
                    value = $"{level:P0}",
                    indicator = new { value = Math.Round(level * 100) }
                }
            });
        }
        if (!result.Success)
        {
            await SendSocketAsync(new
            {
                @event = "setTitle",
                context,
                payload = new { title = "SOUNDFORGE\nOFFLINE", target = 0 }
            });
        }
    }
    catch
    {
        // The Stream Deck application continues running even if one action payload is malformed.
    }
}

string GetDialTitle(string action) => action switch
{
    MasterAction => "MASTER",
    MusicAction => "MUSIC",
    AmbienceVolumeAction => "AMBIENCE",
    EffectsAction => "EFFECTS",
    _ => "SOUNDFORGE"
};

async Task SendChoicesToPropertyInspectorAsync(JsonElement root)
{
    if (!root.TryGetProperty("context", out var contextElement)
        || !root.TryGetProperty("payload", out var payload)
        || !payload.TryGetProperty("command", out var commandElement))
        return;

    var requestedChoices = commandElement.GetString();
    var command = requestedChoices switch
    {
        "getScenes" => new SoundforgeCommand("listScenes", null, null, 0),
        "getEffects" => new SoundforgeCommand("listEffects", null, null, 0),
        _ => null
    };
    if (command is null)
        return;

    var response = await SendToSoundforgeAsync(command);
    await SendSocketAsync(new
    {
        @event = "sendToPropertyInspector",
        context = contextElement.GetString(),
        payload = new
        {
            type = requestedChoices,
            values = response.Values ?? Array.Empty<string>(),
            message = response.Message
        }
    });
}

SoundforgeCommand? BuildCommand(string action, JsonElement root, string eventName)
{
    var ticks = 0;
    if (eventName == "dialRotate" && root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("ticks", out var ticksElement))
        ticks = ticksElement.GetInt32();

    var sceneName = GetSetting(root, "sceneName");
    var trackName = GetSetting(root, "trackName");
    return action switch
    {
        SceneAction => new SoundforgeCommand("activateScene", sceneName, null, 0),
        AmbienceAction => new SoundforgeCommand("toggleAmbience", null, null, 0),
        EffectAction => new SoundforgeCommand("triggerEffect", null, trackName, 0),
        StopAllAction => new SoundforgeCommand("stopAll", null, null, 0),
        MasterAction => new SoundforgeCommand("adjustMaster", null, null, ticks),
        MusicAction => new SoundforgeCommand("adjustMusic", null, null, ticks),
        AmbienceVolumeAction => new SoundforgeCommand("adjustAmbience", null, null, ticks),
        EffectsAction => new SoundforgeCommand("adjustEffects", null, null, ticks),
        _ => null
    };
}

string? GetSetting(JsonElement root, string name)
{
    if (!root.TryGetProperty("payload", out var payload)
        || !payload.TryGetProperty("settings", out var settings)
        || !settings.TryGetProperty(name, out var value))
        return null;

    return value.GetString();
}

async Task<SoundforgeResponse> SendToSoundforgeAsync(SoundforgeCommand command)
{
    try
    {
        using var pipe = new NamedPipeClientStream(".", "Soundforge.StreamDeck", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(750);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(command));
        var response = await reader.ReadLineAsync();
        return response is null
            ? new SoundforgeResponse(false, "Soundforge did not respond.")
            : JsonSerializer.Deserialize<SoundforgeResponse>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new SoundforgeResponse(false, "Soundforge returned an invalid response.");
    }
    catch
    {
        return new SoundforgeResponse(false, "Soundforge is not running.");
    }
}

async Task SendSocketAsync(object value)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

async Task<string?> ReceiveSocketMessageAsync()
{
    var buffer = new byte[32 * 1024];
    var builder = new StringBuilder();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;
        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
    } while (!result.EndOfMessage);

    return builder.ToString();
}

string? GetArgument(string name)
{
    var index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index < args.Length - 1 ? args[index + 1] : null;
}

sealed record SoundforgeCommand(string Action, string? SceneName, string? TrackName, int Ticks);
sealed record SoundforgeResponse(bool Success, string Message, IReadOnlyList<string>? Values = null, double? Level = null);
