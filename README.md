# Soundforge

Soundforge is a lightweight Windows desktop soundboard and scene-based audio controller for livestreams, tabletop sessions, and live production.

The application is currently alpha software. Its working model is:

`Project → Scenes → Layers → Playlist Pools → Tracks`

The three default layers are Music, Ambience, and Effects. Soundforge supports layered playback, random music progression, ambience automation, crossfades, output-device selection, portable project files, crash-recovery autosaves, source trimming, and Stream Deck+ control.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK for source builds

## Build

```powershell
dotnet restore Soundforge.sln
dotnet build Soundforge.sln -c Release
```

The desktop application uses WPF and NAudio. The Stream Deck companion project is stored in `StreamDeckPlugin`.

## Repository history

The earliest development predated Git. The first commits in this repository were reconstructed from preserved, date-stamped source snapshots. They are authentic snapshots, but they are not the original minute-by-minute development history. See [HISTORY.md](HISTORY.md) for the exact boundary.

Compiled releases, audio files, imported-source caches, user projects, and local autosaves are intentionally excluded from Git.

