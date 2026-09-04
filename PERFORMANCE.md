# Project I/O measurements — 2026-08-31

Measured locally on F: with a 304,203,182-byte sample of six existing cached audio sources: four WAVs (two smaller and two larger files), plus two MP3s. Original sources were only read. Temporary output was deleted after testing.

## Compression

| Mode | Trial 1 | Trial 2 | Archive bytes |
| --- | ---: | ---: | ---: |
| Optimal (current default) | 22.65 s | 28.42 s | 291,508,197 |
| Fastest | 22.30 s | 19.08 s | 291,270,248 |
| No compression | 5.15 s | 4.69 s | 304,204,092 |

Trial order was reversed on the second run. Every archive was decompressed and compared to original source SHA-256 hashes. All passed. The sample benefits little from ZIP compression; uncompressed output was about 4.4% larger than Optimal. Results are affected by disk caching/activity and audio content and are not a whole-project prediction. The application default remains Optimal pending a user-facing save-mode decision.

## Loading

A separate project-shaped archive of the same sample included a scene manifest. Loader timings:

- First extraction, including length/CRC verification and disk flush: **15.577 s**.
- Repeat load with an intact verified cache: **0.010 s**.

These measure project-store work, not application launch or rendering the WPF scene editor. The disk data was already warm from the compression integrity check. Existing caches without an index require one initial CRC read; that migration was tested for correctness but not timed on the full user library.

## Safety checks

The regression harness covers intact reuse, legacy cache verification without rewriting audio, missing/truncated/same-length modified cache repair, changed audio in a project at the same path, malformed/null cache index, interrupted extraction, invalid ZIP CRC, preservation of the previous save after failure, and detached save snapshots. Cached files are not rehashed on every warm load: unchanged ZIP CRC/length and local size/write time reuse an earlier verification. This detects ordinary changes, not deliberate timestamp-preserving tampering.

No original project, music file, or recovery snapshot was modified by the benchmarks.
