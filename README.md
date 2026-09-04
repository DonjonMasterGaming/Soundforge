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

## Verified project cache (0.9a.2)

Repeat loads reuse previously verified audio when the ZIP entry CRC/length and cached file size/write time match the local cache index. Existing caches without an index, or files whose metadata changed, are read and checked against the ZIP CRC before reuse. Missing or mismatched files are extracted to a unique temporary file, checked for length/CRC, flushed and replaced only after success. A corrupt index is rebuilt. This is an accidental-corruption check, not authentication or a defence against deliberate same-size, same-timestamp cache tampering.

Saving captures a detached settings snapshot on the UI thread before packaging audio in the background. Project format and default compression are unchanged; saving still rebuilds the complete portable file. The first load may need to verify an older cache or extract missing files. Cache directories remain per application build on F:.

Regression tests cover cache reuse/migration, changed archive audio, missing/truncated/modified cache files, invalid cache metadata, interrupted extraction, invalid ZIP checksums, snapshot isolation and failed-save preservation. To benchmark a sample of existing local WAV/MP3 files without modifying them:

```powershell
dotnet run --project tests/Soundforge.ProjectStoreRegression -c Release -- --benchmark-compression "<cached-audio-directory>"
dotnet run --project tests/Soundforge.ProjectStoreRegression -c Release -- --benchmark-cache "<cached-audio-directory>"
```

Benchmark output is temporary and removed after the run. Compression round trips are checked with SHA-256. Timings describe the sampled audio and current disk conditions, not a guarantee for every project.

## Repository history

The earliest development predated Git. The first commits in this repository were reconstructed from preserved, date-stamped source snapshots. They are authentic snapshots, but they are not the original minute-by-minute development history. See [HISTORY.md](HISTORY.md) for the exact boundary.

Compiled releases, audio files, imported-source caches, user projects, and local autosaves are intentionally excluded from Git.
