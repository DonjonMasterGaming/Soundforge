# Rollback baseline

Phase 1 of 0.9a.4 starts from commit
`741de35d35760f67e433923dfd01d4b0309c83f3`.

The user reports this exact repository was published self-contained for win-x64
and tested successfully on Steam Deck / Proton 9.0-4: launch, WPF interface,
PulseAudio output enumeration, local playback, and project save/reopen.
The binary and its hash were not supplied; this statement records user acceptance,
not a new automated verification. Retain that published directory separately.

Phase 1 must not change AudioEngine, AudioTrackPlayer, LocalAudioReader,
AudioDeviceManager, output selection or transition logic. Google authentication
is independent of projects and audio. Do not commit or push this phase until reviewed.

Rollback means using the retained 0.9a.3 application or a separate checkout of
the baseline commit; never reset away unreviewed work or remove user data.
