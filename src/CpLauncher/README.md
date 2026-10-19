# CpLauncher

Member-facing launcher (Phase 3 of `..\.docs\cp-launcher-v1-plan.md`). Avalonia 12 + CommunityToolkit.Mvvm,
compiled bindings, trimmed single-file exe. UI and orchestration only; patching, manifest parsing, signatures,
the install state machine and the self-update are L2Mod.Core.

## Layout
- `Program.cs` – single-instance lock, update hand-off before any UI, automatic rollback when a fresh update
  crashes before its window works (see "Self-update").
- `Services/` – settings (`settings.json`), data folder (`LauncherPaths`), manifest sources (HTTP / folder),
  `ManifestService` (download, verify with the trusted keys, offline cache), `AssetCache` (overlay PNGs checked
  against the signed hash), `PackPreviewRenderer` (thumbnails from the member's own Icon.utx), `StatusTexts`
  (Polish texts for Core's reasons, manifest notes and update states), `LauncherUpdates` (Core's
  `UpdateService` per release source, restart info, startup cleanup, hand-off, rollback), `LauncherBuild`
  (running version), `LauncherLog`, game start, bootstrap.
- `ViewModels/` – `MainWindowViewModel` (setup ↔ dashboard), `SetupViewModel`, `DashboardViewModel`.
- `Views/` – XAML, `RgbaImageConverter`, dialogs.
- `Keys/manifest-public.pem` – the leader's public key (a copy of `keys/signing-public.pem` from `keygen`). It must
  stay identical to that file: `publish` and the Pages workflow refuse a dist the launcher would reject.

## Data
`%LocalAppData%\CpLauncher\`: `settings.json`, `state.json` (one section per client folder),
`backups\<sha256>\Icon.utx` (shared, named by the original's hash), `backups\clients\<client id>\Option.ini`,
`cache\manifest.json(.sig)`, `cache\assets\`, `launcher.log`. `--data <dir>` or `CPLAUNCHER_DATA` point it
elsewhere.

## Self-update
The manifest's `launcher` entry (`version`, `url` relative to the manifest, `sha256`) offers a newer launcher.
"Aktualizuj" is enabled only when the manifest was just downloaded from an https source (plain http only on
127.0.0.1, for tests): a folder source or the offline copy shows the version but cannot deliver it.
1. Download to `<exe>.new` (64 MB limit, 30 s stall timeout), SHA-256 against the signed manifest.
2. Start `<exe>.new --install-update <old>`, quit. The running exe is never renamed.
3. The hand-off (`Program.Main`, no UI) waits for the lock and for the exe to be free, copies itself to
   `<exe>.tmp`, swaps (exe → `.old`, `.tmp` → exe) and starts the exe with `--updated-from <old>`.
4. The new version deletes `.old`/`.new`/`.tmp` once its window is up. If it crashes before that, it starts
   `.old --install-rollback <new>`; the previous version puts itself back and starts with
   `--rolled-back-from <new>`.
A version whose install failed (`--update-failed`) or that was rolled back is stored as
`skippedLauncherVersion` and not offered again. `--data` and `--dev-public-key` are forwarded to every launcher
the updater starts. Everything is logged to `launcher.log`.

## Development run (fake client only)
```
dotnet run scripts/New-DevSource.cs -- <copy of the original Icon.utx> work/e2e
dotnet run --project src/CpLauncher -- --data work/e2e/data --client work/e2e/client --source work/e2e/source --dev-public-key work/e2e/source/dev-public.pem
```
The script signs with a throwaway key (the private half is never written) and refuses folders outside `work\`.
`--launcher <version> <exe>` adds a launcher release to the dev source.
`--smoke <report.txt>` applies and restores the selected packs, tries the first preset, writes a report plus
window PNGs and exits. `--smoke-update <report.txt>` reports the offered update and clicks "Aktualizuj".

## Publish
```
dotnet publish src/CpLauncher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=true -o work/publish
```
Add `-p:Version=1.0.1` for another version. `-p:CpLauncherSimulateStartupFailure=true` makes a **test build**
that throws at startup (rollback check); never publish it.

## Self-update check with real processes
Publish `1.0.0`, `1.0.1` and a `1.0.1` test build with `CpLauncherSimulateStartupFailure` into
`work\e2e-update\builds\<name>\`, then:
```
pwsh -File scripts/Test-SelfUpdate.ps1 -Original <copy of the original Icon.utx>
```
It serves a dev source on `http://127.0.0.1:8765/` (Python's `http.server`), runs 1.0.0 → 1.0.1 and
1.0.0 → crashing 1.0.1 → rollback, on fake clients under `work\e2e-update\`.
