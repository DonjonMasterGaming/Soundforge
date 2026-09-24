# 0.9a.4 Phase 1 — Google desktop authentication

This phase adds authentication only. There is no Drive browser, catalogue, new
audio cache or network-aware playback. Baseline: `741de35d35760f67e433923dfd01d4b0309c83f3`.
See BASELINE-0.9a.3.md for the user's accepted Proton 9.0-4 behaviour.

## Build and validate

The user explicitly approved C: source/build/test output for this phase. No F:
access is required. With .NET 8 SDK on PATH, from the repository root:

```powershell
.\validate-0.9a.4.ps1 -AllowCDriveOutput
```

On the implementation machine a local SDK was installed in ignored `.sdk`:

```powershell
.\validate-0.9a.4.ps1 -Dotnet "$PWD\.sdk\dotnet.exe" -AllowCDriveOutput
```

The script fails immediately on any failed command. It builds the Release
solution, explicitly builds and runs the separate regression harness, then
publishes a self-contained win-x64 application. Each run uses a new isolated
`.validation/run-...` directory. The printed `Soundforge-0.9a.4-win-x64` directory
is the complete app. `published-sha256.json` beside it records published file
hashes. No ZIP, updater package or installation update is performed.

Do not run the old build-0.9a.3.ps1 for this preview. The updater and Stream Deck
versions are intentionally unchanged. Do not publish the test harness into an
installation: its existing fixture cleanup removes its own adjacent cache.

## Configure Google

1. Open https://console.cloud.google.com/ and create/select a test project.
2. In APIs & Services → Library, enable **Google Drive API**.
3. Open Google Auth platform → Branding (Get started if new). Supply an app name,
   support email and developer contact email.
4. Under Audience, select External unless using an appropriate internal Workspace
   project. Keep the app in Testing and add the Google accounts that will test it.
5. Under Data Access, add `openid`, `https://www.googleapis.com/auth/userinfo.email`
   and `https://www.googleapis.com/auth/drive.readonly`.
6. Under Clients → Create client, choose **Desktop app**, name it Soundforge test,
   and download its JSON. Do not choose Web application or a service account.
   The desktop flow uses a dynamically selected 127.0.0.1 port; do not configure
   a fixed web redirect URI or paste an authorization code.
7. Keep that JSON outside the repository. In Soundforge click **Connect Google
   Drive… → Import Google Desktop client JSON…** and select it. The application
   validates the `installed` client shape and copies it to the current user's
   `%LOCALAPPDATA%\Soundforge\auth\oauth-client.json`.

No account, client ID or secret has been supplied by the implementation. Tokens
and configuration are never part of projects, recovery manifests or publish
output. Git ignore rules exclude common credential/configuration names.

Google's restricted `drive.readonly` scope enables the later native folder
browser. Broad public release needs the applicable Google verification process.
External Testing refresh tokens with this scope normally expire after seven
days; a later reconnect failure may require a new interactive Connect.

References:
- https://developers.google.com/workspace/guides/configure-oauth-consent
- https://developers.google.com/workspace/guides/create-credentials
- https://developers.google.com/identity/protocols/oauth2/native-app
- https://developers.google.com/workspace/drive/api/guides/api-specific-auth
- https://developers.google.com/identity/protocols/oauth2#expiration

## Windows authentication test

1. Run Soundforge.exe in the complete validated publish directory.
2. Open a known local project and verify playback/output remains normal.
3. Open **Connect Google Drive…**, import the desktop client JSON, then select
   Windows user protection for native Windows or passphrase encryption.
4. For passphrase encryption enter a new passphrase of at least 12 characters.
   This protects stored tokens; it is not your Google password. Keep it safe:
   it is deliberately not saved. The field clears after an operation.
5. Click **Connect Google Drive**. The system browser should open Google sign-in.
   Sign into an allowed test account and grant the requested permissions.
6. The browser should report authorization received; the app must report
   **Connected: <account>**. The browser message alone does not prove token
   exchange or encrypted saving succeeded.
7. Close/reopen Soundforge, open the connection window, choose the same storage
   method, re-enter the passphrase if needed, and click **Reconnect saved account**.
   This explicitly refreshes the token and verifies account identity via Google's
   HTTPS UserInfo endpoint; it should connect without opening the browser.
8. Test Cancel, denied consent, wrong passphrase and loss of internet. No failed
   operation should erase working saved credentials or interrupt local audio.
9. Click Disconnect. Reconnect should report no saved account. Audio/project
   data remain intact. Disconnect is local; revoke Google consent separately in
   your Google account's third-party connections if wanted.

## Steam Deck / Proton 9.0-4 test

1. Run the validation command above on Windows. Copy **every file and subdirectory**
   from the printed publish folder to a new folder on the Deck. Retain the 0.9a.3
   folder unchanged. Do not copy local saved tokens from Windows.
2. Add the new Soundforge.exe as a non-Steam game (or configure a separate test
   shortcut), select compatibility **Proton 9.0-4**, and set Start In to that folder.
3. Start in Desktop Mode with a working default browser. Import the same Desktop
   client JSON using Soundforge's file picker; it is stored in that Proton prefix's
   Windows LocalAppData. The test account must be in Google's allowed test users.
4. Choose **Passphrase encrypted**, enter the credential passphrase and Connect.
   Windows user protection is explicitly blocked when Wine is detected; there is
   no plaintext fallback. Session-only is an explicit alternative but cannot test
   persistence across restart.
5. Confirm browser launch → Google consent → 127.0.0.1 callback → account connected.
   Record which stage fails if it does not complete. The five-minute timeout and
   Cancel button terminate the listener. No clipboard/share-link entry is needed.
6. Restart using the same Steam shortcut/Proton prefix. Select passphrase mode,
   enter the passphrase and Reconnect to prove encrypted persistence and refresh.
7. Disconnect networking and verify existing local/cached project playback and
   portable project save/reopen. This phase does not download authenticated Drive
   audio. Reconnect should fail gracefully while audio continues.
8. Repeat in Gaming Mode to validate the browser handoff there. A Desktop Mode
   success does not establish Gaming Mode browser behaviour.
9. Recheck PulseAudio output, trims, scene/pool transitions and Stream Deck where
   available. Keep the baseline application for comparison.

## Security and implementation boundaries

- GoogleOAuthService implements IGoogleAccountProvider and uses injectable HTTP,
  authorization broker, credential store and time dependencies. No project/audio
  references exist in the authentication subsystem.
- Authorization uses random state, PKCE S256, an IPv4-loopback-only listener,
  bounded headers, host/path checks, cancellation and timeout. Wrong-state requests
  cannot complete sign-in. HTTPS token requests do not follow redirects.
- Identity is read from Google's authenticated UserInfo endpoint, not an unverified
  ID-token payload. Reconnect verifies the stored client and account subject.
- Passphrase storage uses PBKDF2-SHA256 (600,000 iterations), random salt, AES-256-GCM
  and authenticated ciphertext. Native Windows storage uses DPAPI CurrentUser.
  Writes flush a unique temporary file before replacement. Errors do not downgrade
  storage. Session-only tokens live only as long as the connection window.
- UI error messages do not echo remote token bodies or callback codes. No token
  logging is implemented. Managed-memory secrets cannot be guaranteed zeroed;
  temporary plaintext byte/key buffers are cleared where practical.
- Windows DPAPI does not prove equivalent security under Wine. The explicit
  portable passphrase mode avoids relying on that assumption.
- CloudAudioCache still uses its existing HTTPS/private-address rules; only DNS
  resolution is injected. Cloud regressions now use fake DNS as well as fake HTTP.

Real Google sign-in, consent configuration, browser integration and Proton remain
manual acceptance gates. Automated tests use fixtures and loopback only.

## Implementation-machine validation, 2026-09-24

- SDK: 8.0.425, installed locally in ignored `.sdk`.
- Final validation run: `.validation/run-20260924-124128-2200372f`.
- Release solution build: passed, 0 warnings and 0 errors.
- Explicit regression-project build: passed, 0 warnings and 0 errors.
- Full harness: passed (project cache/persistence/recovery, public cloud import,
  OAuth/encrypted storage, updater rollback, audio formats, transitions, fades,
  trims, completion and scene ordering).
- Self-contained win-x64 publish: passed; full output in that run's
  `Soundforge-0.9a.4-win-x64` folder; sibling SHA-256 manifest generated.
- No OAuth client/credential files were found in the publish tree.
- `git diff --check`: passed. Git reports ordinary LF-to-CRLF normalization notices.
- Audio, project models, persistence, control and Stream Deck source are unchanged
  against the baseline. MainWindow changes only add the connection-window launcher.
- No real Google sign-in, visual UI acceptance or Steam Deck test was performed.
- No commit, push, installer execution or replacement of the baseline build.

## Subsequent user acceptance

The user confirmed real Windows Google authentication and credential persistence:
saved refresh credentials survived an application restart, Windows protection
unlocked them, and Reconnect succeeded without another browser login.

Deferred UX improvement: automatically reconnect Windows-protected accounts at
startup. Passphrase-protected accounts should continue to require explicit
unlocking. This behaviour has not been changed.

Cancellation/denied consent, offline reconnect, disconnect, and Steam Deck / Proton
Desktop and Gaming Mode acceptance remain outstanding. The user subsequently
authorised updating Git with the Phase 1 implementation.
