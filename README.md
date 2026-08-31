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

## 0.9a.2 playback hotfix

- Playback and the trim editor decode uncompressed WAV Extensible PCM/float directly, without requiring a legacy ACM codec. Original files are not converted or modified.
- Pool/scene transitions start their replacements before retiring existing playback. Failed starts release new sessions and leave previous playback running; failed pool activation restores the previous pool selection.
- Manual playback and automatic progression report source errors instead of terminating the application. Stream Deck receives a failed command result rather than a false success.
- Selecting the current music pool can retry playback when it has no remaining session.

Run the regression harness (Windows with an audio output available):

```powershell
dotnet run --project tests/Soundforge.ProjectStoreRegression -c Release
```

The harness covers PCM24/float WAV Extensible sample values, seeking, gain, unknown-subtype rejection, failed-transition cleanup, successful transitions, trims, fades, completion, persistence and scene ordering. An optional `-- --verify-sources <recovery.autosave.json>` checks full-file decoding of the local sources referenced by a recovery manifest without playing or changing those files. It does not extract portable projects.

## Repository history

The earliest development predated Git. The first commits in this repository were reconstructed from preserved, date-stamped source snapshots. They are authentic snapshots, but they are not the original minute-by-minute development history. See [HISTORY.md](HISTORY.md) for the exact boundary.

Compiled releases, audio files, imported-source caches, user projects, and local autosaves are intentionally excluded from Git.
