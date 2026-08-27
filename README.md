# Soundforge

A lightweight Windows soundboard/mixer for tabletop games and livestreams.

## Prototype goals
- Multiple simultaneous audio tracks
- Local MP3/WAV/OGG playback
- Per-track volume
- Looping
- Master volume
- Current track/progress display
- Audio output device selection
- Scene/playlist/project architecture ready for expansion

## Build
Requires .NET 8 SDK and Visual Studio 2022/2026 with the .NET desktop workload.

Open `Soundforge.sln`, restore NuGet packages, then run the project.

This first prototype deliberately keeps the UI simple. The audio engine is separated from the UI so future mobile remote-control support can be added without rewriting playback.
