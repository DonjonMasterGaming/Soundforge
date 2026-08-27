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

var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundforge");
var settingsPath = Path.Combine(settingsDirectory, "streamdeck-actions.json");
var storedSettings = LoadStoredSettings();

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
            await HandlePropertyInspectorMessageAsync(root);
            continue;
        }

        if (eventName == "propertyInspectorDidAppear")
        {
            var inspectorAction = root.GetProperty("action").GetString() ?? string.Empty;
            var inspectorContext = root.GetProperty("context").GetString() ?? string.Empty;
            await SendChoicesForActionAsync(inspectorAction, inspectorContext);
            await SendStoredSettingsToPropertyInspectorAsync(inspectorContext);
            await SetActionTitleAsync(inspectorAction, inspectorContext);
            continue;
        }

        if (eventName == "didReceiveSettings")
        {
            RememberReceivedSettings(root);
            await UpdateActionTitleAsync(root);
            continue;
        }

        if (eventName == "willAppear")
        {
            var appearingAction = root.GetProperty("action").GetString() ?? string.Empty;
            var appearingContext = root.GetProperty("context").GetString() ?? string.Empty;
            await SetActionTitleAsync(appearingAction, appearingContext);
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
                @event = "showAlert",
                context
            });
        }
        else if (action == AmbienceAction && eventName == "keyDown")
        {
            var isOn = result.Message.StartsWith("Started", StringComparison.OrdinalIgnoreCase);
            await SendSocketAsync(new
            {
                @event = "setState",
                context,
                payload = new { state = isOn ? 0 : 1 }
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

async Task UpdateActionTitleAsync(JsonElement root)
{
    var action = root.GetProperty("action").GetString() ?? string.Empty;
    var context = root.GetProperty("context").GetString() ?? string.Empty;
    var configuredName = action switch
    {
        SceneAction => GetSetting(root, "sceneName") ?? GetStoredSetting(context)?.SceneName,
        EffectAction => GetSetting(root, "trackName") ?? GetStoredSetting(context)?.TrackName,
        _ => null
    };

    if (string.IsNullOrWhiteSpace(configuredName))
        return;

    await SendSocketAsync(new
    {
        @event = "setTitle",
        context,
        payload = new { title = configuredName, target = 0 }
    });
}

async Task SetActionTitleAsync(string action, string context)
{
    if (action == AmbienceAction)
    {
        await SendSocketAsync(new
        {
            @event = "setState",
            context,
            payload = new { state = 1 }
        });
    }

    var saved = GetStoredSetting(context);
    var title = action switch
    {
        SceneAction => string.IsNullOrWhiteSpace(saved?.SceneName) ? "SELECT\nSCENE" : saved.SceneName,
        AmbienceAction => string.Empty,
        EffectAction => string.IsNullOrWhiteSpace(saved?.TrackName) ? "SELECT\nEFFECT" : saved.TrackName,
        StopAllAction => "STOP\nALL",
        _ => null
    };
    if (title is null)
        return;

    await SendSocketAsync(new
    {
        @event = "setTitle",
        context,
        payload = new { title, target = 0 }
    });
}

async Task SendChoicesToPropertyInspectorAsync(JsonElement root)
{
    if (!root.TryGetProperty("context", out var contextElement)
        || !root.TryGetProperty("payload", out var payload)
        || !payload.TryGetProperty("command", out var commandElement))
        return;

    await SendChoiceListToPropertyInspectorAsync(commandElement.GetString(), contextElement.GetString());
}

async Task HandlePropertyInspectorMessageAsync(JsonElement root)
{
    if (!root.TryGetProperty("context", out var contextElement)
        || !root.TryGetProperty("payload", out var payload)
        || !payload.TryGetProperty("command", out var commandElement))
        return;

    var command = commandElement.GetString();
    var context = contextElement.GetString() ?? string.Empty;
    if (command != "saveSettings")
    {
        await SendChoicesToPropertyInspectorAsync(root);
        return;
    }

    var sceneName = GetPayloadValue(payload, "sceneName");
    var trackName = GetPayloadValue(payload, "trackName");
    storedSettings[context] = new StoredActionSettings(sceneName, trackName);
    SaveStoredSettings();
    await SendSocketAsync(new
    {
        @event = "setSettings",
        context,
        payload = new { sceneName, trackName }
    });

    var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
    var title = action == SceneAction ? sceneName : action == EffectAction ? trackName : null;
    if (!string.IsNullOrWhiteSpace(title))
    {
        await SendSocketAsync(new
        {
            @event = "setTitle",
            context,
            payload = new { title, target = 0 }
        });
    }
}

async Task SendStoredSettingsToPropertyInspectorAsync(string context)
{
    var saved = GetStoredSetting(context) ?? new StoredActionSettings(null, null);
    await SendSocketAsync(new
    {
        @event = "sendToPropertyInspector",
        context,
        payload = new { type = "savedSettings", sceneName = saved.SceneName, trackName = saved.TrackName }
    });
}

async Task SendChoiceListToPropertyInspectorAsync(string? requestedChoices, string? context)
{
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
        context,
        payload = new
        {
            type = requestedChoices,
            values = response.Values ?? Array.Empty<string>(),
            message = response.Message
        }
    });
}

async Task SendChoicesForActionAsync(string action, string context)
{
    var requestedChoices = action switch
    {
        SceneAction => "getScenes",
        EffectAction => "getEffects",
        _ => null
    };

    if (requestedChoices is not null)
        await SendChoiceListToPropertyInspectorAsync(requestedChoices, context);
}

SoundforgeCommand? BuildCommand(string action, JsonElement root, string eventName)
{
    var ticks = 0;
    if (eventName == "dialRotate" && root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("ticks", out var ticksElement))
        ticks = ticksElement.GetInt32();

    var context = root.TryGetProperty("context", out var contextElement) ? contextElement.GetString() ?? string.Empty : string.Empty;
    var saved = GetStoredSetting(context);
    var sceneName = GetSetting(root, "sceneName") ?? saved?.SceneName;
    var trackName = GetSetting(root, "trackName") ?? saved?.TrackName;
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

string? GetPayloadValue(JsonElement payload, string name)
    => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

StoredActionSettings? GetStoredSetting(string context)
    => storedSettings.TryGetValue(context, out var saved) ? saved : null;

void RememberReceivedSettings(JsonElement root)
{
    if (!root.TryGetProperty("context", out var contextElement))
        return;

    var context = contextElement.GetString() ?? string.Empty;
    var sceneName = GetSetting(root, "sceneName");
    var trackName = GetSetting(root, "trackName");
    if (string.IsNullOrWhiteSpace(sceneName) && string.IsNullOrWhiteSpace(trackName))
        return;

    storedSettings[context] = new StoredActionSettings(sceneName, trackName);
    SaveStoredSettings();
}

Dictionary<string, StoredActionSettings> LoadStoredSettings()
{
    try
    {
        if (!File.Exists(settingsPath))
            return new Dictionary<string, StoredActionSettings>();

        return JsonSerializer.Deserialize<Dictionary<string, StoredActionSettings>>(File.ReadAllText(settingsPath))
            ?? new Dictionary<string, StoredActionSettings>();
    }
    catch
    {
        return new Dictionary<string, StoredActionSettings>();
    }
}

void SaveStoredSettings()
{
    Directory.CreateDirectory(settingsDirectory);
    File.WriteAllText(settingsPath, JsonSerializer.Serialize(storedSettings));
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
sealed record StoredActionSettings(string? SceneName, string? TrackName);
