# CpLauncher

Member-facing launcher (Phase 3 of `..\.docs\cp-launcher-v1-plan.md`). Avalonia 12 + CommunityToolkit.Mvvm,
compiled bindings, trimmed single-file exe. UI and orchestration only; patching, manifest parsing, signatures
and the install state machine are L2Mod.Core.

## Layout
- `Services/` – settings (`settings.json`), data folder (`LauncherPaths`), manifest sources (HTTP / folder),
  `ManifestService` (download, verify with the trusted keys, offline cache), `AssetCache` (overlay PNGs checked
  against the signed hash), `PackPreviewRenderer` (thumbnails from the member's own Icon.utx), `StatusTexts`
  (Polish texts for Core's states), game start, bootstrap.
- `ViewModels/` – `MainWindowViewModel` (setup ↔ dashboard), `SetupViewModel`, `DashboardViewModel`.
- `Views/` – XAML, `RgbaImageConverter`, dialogs.
- `Keys/manifest-public.pem` – **placeholder** public key (nobody has its private half). Replace it with the
  leader key from the Phase 2 keygen before the first release.

## Data
`%LocalAppData%\CpLauncher\`: `settings.json`, `state.json`, `backups\<sha256>\Icon.utx`, `backups\Option.ini`,
`cache\manifest.json(.sig)`, `cache\assets\`. `--data <dir>` or `CPLAUNCHER_DATA` point it elsewhere.

## Development run (fake client only)
```
dotnet run scripts/New-DevSource.cs -- <copy of the original Icon.utx> work/e2e
dotnet run --project src/CpLauncher -- --data work/e2e/data --client work/e2e/client --source work/e2e/source --dev-public-key work/e2e/source/dev-public.pem
```
The script signs with a throwaway key (the private half is never written) and refuses folders outside `work\`.
`--smoke <report.txt>` applies and restores the selected packs, tries the first preset, writes a report plus
window PNGs and exits.

## Publish
```
dotnet publish src/CpLauncher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=true -o work/publish
```
