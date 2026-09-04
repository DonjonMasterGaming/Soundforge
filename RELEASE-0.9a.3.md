# Soundforge 0.9a.3 — Cloud Audio foundation

## What is new

- Add a track from a direct HTTPS audio URL.
- Paste and import hundreds of cloud audio links in one batch; successful files remain imported when an individual link fails.
- Recognise common public Google Drive file share links and cache the result locally.
- Download on a background task, show progress, validate that the completed file can be decoded, and only then add it to a scene.
- Play URL/Drive sources exclusively from local cached audio. Once cached, playback makes no network request.
- Preserve source kind, original URL, Drive file ID, ETag, modified time and cached time in the project model. Portable `.soundforge` files continue to bundle the cached audio, so a recipient does not need the original URL.
- Show a cached-offline marker and original URL alongside cloud-backed sources.
- Refuse HTTP, loopback/private-network targets, excessive redirects, HTML/login pages, empty/truncated files, unknown formats, files over 20 GB, and downloads that would leave under 512 MB free.
- Keep partial downloads temporary and remove them on failure.

Public Drive links must permit downloading without sign-in. Google account connection, private Drive browsing, update checks, cache-size policy and automatic eviction are deliberately not in this increment. Google's supported Drive API download flow requires OAuth access; the provider boundary and metadata fields added here leave room for that without coupling network work to playback.

## Installation and updating

Extract the complete ZIP and run **Update Soundforge.exe** beside `update.json` and the `payload` folder.

On this development PC, the first updater recognises the existing 0.9a.2 installation and offers to update it in place, preserving the existing pinned shortcut. Otherwise it asks for one permanent install folder and records that choice. Later update packages reuse that location automatically.

The updater:

- refuses to update while the installed Soundforge is running;
- stages and SHA-256 verifies every new program file;
- replaces only files managed by the updater;
- preserves unrecognised files, `data`, project files, cloud/project caches, autosaves and LocalAppData settings;
- rolls back already-replaced files if a later replacement fails;
- removes its temporary staging/backup directories after success or rollback.

Keep the ZIP until the update has been tested. It is self-contained for Windows x64 and does not require the .NET SDK or Visual Studio.

The existing Stream Deck protocol remains compatible and does not require a reinstall on an already configured machine. A rebuilt optional `Soundforge-StreamDeck-0.9a.3.streamDeckPlugin` is included for new testers.

## Locations

- Source workspace: `F:\Folders\Useful Stuff\Visual Studio\Soundforge 0.9a.3`
- Versioned artifacts: `F:\Folders\Useful Stuff\Visual Studio\Soundforge Builds\0.9a.3`
- New-install default on this PC: `F:\Folders\Useful Stuff\Visual Studio\Soundforge Installed`

No 0.9a.3 output is written beneath the old 0.8a.7 workspace.
