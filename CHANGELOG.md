# Changelog

Changes since the first public release of Satisfactory Dev Mod Manager. The first public release itself is described in the [README](README.md); this file lists what changed in each public update after it.

## Unreleased

### Added
- **Command line tool:** `SatisfactoryDevModManager-cli.exe` lists your games, servers and dev mods, installs, updates and removes dev builds, updates everything with a newer build, matches a game with a server, and applies modpacks, using the app's settings. Confirmations can be answered with `--yes` for scripts, and exit codes report what happened. See the README's "Command line" section.

### Fixed
- When a newer build of an installed mod is available, it's now selected in the version list, so the button says "Update to …" straight away instead of needing a different version picked first.
- The My game / Server switch and the game and server lists stay usable while SDMM is reading a server or checking for new builds. Picking another one stops the read in progress, so a slow server no longer locks you out until it finishes.
- **Sync with server** now lists every mod on either side, including ones installed by SMM, the host or by hand and ones already in sync, with what will change at the top.
- **Match** showed its "installed, restart the server" message after the first part finished while the rest was still running. It now shows the step it's on and one message when everything is done. Applying a modpack to a server works the same way.
- The server banner no longer shows the login name in the server's address, so screenshots don't share it.

## 0.2.2 (2026-11-08)

### Fixed
- The grey placeholder text in the search box and the token and key boxes started to the left of where typing starts. It now lines up with the cursor. Text boxes keep their size.

## 0.2.1 (2026-11-08)

### Fixed
- A launch option still being typed was lost if Settings was closed with Enter, Esc or the window's X; it's now kept.
- `crash.log` was created on every start (with a startup line) even without a crash. It's now only written when there's a crash, and each entry includes the SDMM version and Windows build.
- Reading a hosted server that's running showed the "server is probably running" prompt again on every refresh. When you're only looking, it's now asked once per server per session; changes still always ask.
- VirusTotal checks now keep to the free key's limit of 4 a minute across installs done one after another, instead of starting fresh for each.
- When one dependency is needed by several mods with different version ranges, the banner now lists each mod's own range.
- A few messages still named windows and buttons that have since been renamed ("Sources", "Check GitHub"); they now match.

### Changed
- Cached SMR logos not used for 30 days are deleted, so the cache doesn't keep growing; they're downloaded again when needed.
- Updated System.Security.Cryptography.ProtectedData (which encrypts saved tokens, keys and passwords) to 10.0.14.

## 0.2.0 (2026-11-08)

### Added
- **Crash log:** unexpected errors are always saved to `crash.log` next to the settings file (under 1 MB), and the error message says where it is.
- **Detailed log (off by default):** turn it on under Settings → Storage to record everything SDMM does to your PC and servers, its web requests and errors in full, in `sdmm.log` (under 5 MB, oldest lines dropped first). Logs never contain tokens, keys or passwords.
- **Optional VirusTotal check:** under Settings → Security, with your own free VirusTotal API key, SDMM checks each mod file the first time it's installed and asks before installing one that antivirus engines flag. Off by default; it looks files up by fingerprint only, unless you also allow uploading files VirusTotal hasn't seen.
- **Launch options per game:** Settings → Games & servers has a Launch options column where you can set the switches **Play** passes to each game, for example `-DX12` or `-log`. Steam's own launch options don't apply when SDMM starts a game, because it runs that install's exe directly.
