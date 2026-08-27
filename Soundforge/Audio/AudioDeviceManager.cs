using NAudio.CoreAudioApi;

namespace Soundforge.Audio;

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }
}

public sealed record AudioDeviceInfo(string Id, string Name);
