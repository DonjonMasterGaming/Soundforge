# Soundforge workspace policy

- Treat `F:\Folders\Useful Stuff\Visual Studio\Soundforge 0.9a.3` as the source of truth for Soundforge development.
- Keep source code, test harnesses, build intermediates, published releases, Stream Deck plugin packages, and development archives on F:.
- Do not create Soundforge build or test output under C:.
- Publish versioned build artifacts to `F:\Folders\Useful Stuff\Visual Studio\Soundforge Builds`; do not create a `release` folder in this workspace.
- Install/update the testable application in `F:\Folders\Useful Stuff\Visual Studio\Soundforge Installed` without deleting settings, projects, caches, autosaves, or other user data.
- Create portable/update packages only when explicitly requested, under the versioned build folder on F:.
- Preserve the Windows WPF + NAudio architecture and the Project -> Scenes -> Layers -> Playlists -> Tracks model.
- Windows-managed application settings and an installed Stream Deck plugin may remain under the user's AppData because those host applications require their standard per-user locations.
