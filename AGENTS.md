# Soundforge workspace policy

- Treat this F: directory as the source of truth for Soundforge development.
- Keep source code, test harnesses, build intermediates, published releases, Stream Deck plugin packages, and development archives on F:.
- Do not create Soundforge build or test output under C:.
- Publish normal test builds to this version directory's `release` folder.
- Create a portable package only when the user explicitly requests one, and keep it on F:.
- Preserve the Windows WPF + NAudio architecture and the Project -> Scenes -> Layers -> Playlists -> Tracks model.
- Windows-managed application settings and an installed Stream Deck plugin may remain under the user's AppData because those host applications require their standard per-user locations.
