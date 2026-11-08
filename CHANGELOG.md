# Changelog

Changes since the first public release of Satisfactory Dev Mod Manager. The first public release itself is described in the [README](README.md); this file lists what changed in each public update after it.

## 0.2.0 (2026-11-08)

### Added
- **Crash log:** unexpected errors are always saved to `crash.log` next to the settings file (under 1 MB), and the error message says where it is.
- **Detailed log (off by default):** turn it on under Settings → Storage to record everything SDMM does to your PC and servers, its web requests and errors in full, in `sdmm.log` (under 5 MB, oldest lines dropped first). Logs never contain tokens, keys or passwords.
- **Optional VirusTotal check:** under Settings → Security, with your own free VirusTotal API key, SDMM checks each mod file the first time it's installed and asks before installing one that antivirus engines flag. Off by default; it looks files up by fingerprint only, unless you also allow uploading files VirusTotal hasn't seen.
- **Launch options per game:** Settings → Games & servers has a Launch options column where you can set the switches **Play** passes to each game, for example `-DX12` or `-log`. Steam's own launch options don't apply when SDMM starts a game, because it runs that install's exe directly.
