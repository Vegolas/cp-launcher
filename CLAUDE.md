# L2Mod – cosmetic Lineage 2 client patching tools

Lives inside the L2Dex game client folder (`C:\Lineage2Dex`). The game files around it are **not** part of this project. Build on copies in `work\`; from this repo, files reach the client only through the hand-run, hash-checked scripts (`scripts\Install-IconPatch.ps1`, `scripts\Install-EffectsExperiment.ps1`) and the launcher's installer, which may change only the whitelisted `ClientFiles.Patchable` (`SysTextures\Icon.utx`, `system_season\LineageSkillEffect.u`, `system_season\LineageEffect.u`) plus Option.ini presets. Never touch any other client file or folder.

Skill icons exist in three packages with identical content (`Icon.utx`, `Icon_c.utx`, `Icon_h.utx`); an in-game test confirmed the client uses **`Icon.utx`** for them, so patch only that one. Currently installed: `work\out\badges20\Icon.utx`, built with:
```
dotnet run --project src/L2Mod.Cli -- badge work/original/Icon.utx mappings/dances-songs.txt work/out/badges20/Icon.utx --size 20 --preview work/preview/badges20.png
```
(The tool default stays 16 px; the user prefers 20 px.)

Background research: `..\.docs\l2-client-patch-research.md`.

## Layout
- `src/L2Mod.Core` – the engine (library, dependency-free, `IsAotCompatible` because the launcher ships trimmed):
  - `Packages/` – NCSoft Ver111/Ver121 XOR crypto (`L2File`), UE2 package tables (`UnrealPackage`).
  - `Textures/` – texture property parsing and mip location (`TextureReader`), DXT1 codec incl. re-encoding only the blocks touched by a list of rectangles.
  - `Imaging/` – dependency-free PNG codec, RGBA image (crop, area resize), preview sheet, `IconCorner`.
  - `Badges/` – corner badge compositing and the badge mapping parser.
  - `Operations/` – `badge`, `tag` (lossless colour square), `overlay` (PNG with alpha); `OperationComposer` draws all operations of a texture in order and encodes it once.
  - `Recipes/` – mapping files -> operations with the approved styles (`RecipeDefaults`: badges 20 px br, debuff tags 8 px tl in CC/DOT/STAT colours); `EffectMappings` reads the effect mappings (label `# name | description` = the option members tick).
  - `Effects/` – the effect engine for `system_season` (moved from the experiment script, byte-identical): `EffectSwapOperation` (class names + SkillID swap, in place), `GoldClickArrowOperation` (LineageEffect.u, in place), `TargetIconOperation` (icon over the target, appended to LineageSkillEffect.u by `TargetIconBuilder` on `PackageEditor`, verified on every build; pixels and texture layout from the ORIGINAL Icon.utx). `EffectComposer` applies in-place changes first, then icons. All `IPatchOperation`s; `IIconOperation` is the Icon.utx kind.
  - `Manifests/` – manifest schema 1 model and strict parser (a pack has `operations` or `options`; an option is selected as `<pack id>/<option id>`, which `Manifest.GetOperations` and the launcher settings use) (`launcher.version` follows `LauncherVersion`, strict SemVer; a pack's optional `description` is read leniently: a bad one is dropped, not the pack, and `WriteChecked` refuses to publish it); skipped parts come as `ManifestReadResult.Notes` (code, `ManifestSkipReason`, item id/name) besides the English `Messages`. `Signing/` – ECDSA P-256 detached signature of manifest.json.
  - `Installation/` – per-file install state machine over all `ClientFiles.Patchable` (`PatchInstaller`; `PatchFileStatus`/`PatchFileResult` carry a `PatchReason`, the status an `OperationCount`; `ReadOriginal` gives the original for previews and for the icons over the target; Icon.utx keeps its old recipe key, the effect packages have their own `EffectEngineVersion`), Option.ini presets (`OptionPresetInstaller`), file-system/process abstractions; `Ini/` – Option.ini merge. One data dir can serve several client folders: state.json has a section per client folder (a pre-section state.json is adopted by the first folder that loads it), the Option.ini backup is per client folder (`backups\clients\<id>\`), Icon.utx backups are shared by hash.
  - `Updates/` – launcher self-update by hand-off: the running exe is never renamed; the new version runs from `<exe>.new --install-update`, waits for the old process and the named-mutex instance lock, swaps via `.tmp`/`.old`, restarts with `--updated-from`; rollback from `.old`. `UpdateOptions.ForwardedArguments` follow every started launcher.
- `src/CpLauncher` – the member launcher (Avalonia 12, CommunityToolkit.Mvvm, Polish UI, single-file publish ~32 MB). View models have no Avalonia types; every file change goes through Core. Data dir `%LocalAppData%\CpLauncher` (override `--data` / `CPLAUNCHER_DATA`); `--client`, `--source`, `--dev-public-key` (shows a warning banner). Built-in key `src/CpLauncher/Keys/manifest-public.pem` is the leader's real public key (keygen 2026-10-19, identical to `keys/signing-public.pem`; the private half is only in `%USERPROFILE%\.l2mod\`); `LauncherDefaults.ManifestSource` is `https://vegolas.github.io/cp-launcher/` (GitHub Pages, compiled in: never rename the repo or the account, keep the repo public). "Uruchom L2Dex" starts `L2Updater.exe`. Self-update is wired in: `Program.Main` takes the instance lock (second launcher → message box, exit) and runs the hand-off before any Avalonia setup; `Services/LauncherUpdates` gives an `UpdateService` per release source (only an https source, or http on loopback, can deliver; the release URL is relative to the manifest); cleanup runs once the window is up; a fresh update that crashes before that rolls back automatically; failed/rolled-back versions are stored as `skippedLauncherVersion`. `--data`/`--dev-public-key` are forwarded across restarts. `launcher.log` in the data dir logs starts, updates and crashes. `-p:CpLauncherSimulateStartupFailure=true` builds a test-only version that throws at startup; never publish it.
- `tests/CpLauncher.Tests` – launcher view models and services against fakes (installer and updater fakes linked from `tests/L2Mod.Core.Tests`).
- `scripts/New-DevSource.cs` – .NET 10 file-based script that builds a signed dev manifest with a throwaway key (never write it into `C:\Lineage2Dex`); `--launcher <version> <exe>` adds a launcher release. Manual launcher checks use a FAKE client folder under `work/`, never the real client.
- `scripts/Test-SelfUpdate.ps1` – end-to-end self-update check with real processes (1.0.0 → 1.0.1, and 1.0.0 → crashing 1.0.1 → rollback) on fake clients under `work\e2e-update\`, source served on `http://127.0.0.1:8765/`; needs the three builds described in its help and `src/CpLauncher/README.md`.
- Effect packs in the launcher (since 1.2.0, all off by default): `target-icons` (`mappings/debuff-icons.txt`, one option per status), `skill-effect-swaps` (`mappings/effect-swaps.txt`, one option per pair). They change `system_season\LineageSkillEffect.u` (L2Updater left it alone in a test). `gold-click-arrow` (LineageEffect.u) was published in 2026.11.09.1 and withdrawn in 2026.11.09.2: the patched `GstarFX_arrow_click`/`_loop` classes (main mesh material, colours, glow) showed no change in game, so L2Dex probably plays another effect on click-to-move (L2.ini is Ver413, unreadable). Launchers restore LineageEffect.u where it was installed. The op type stays in the code; do not re-add the pack without an in-game test. The icon over the target is not a status indicator: it plays on every cast that reaches the target, also a resisted one, for a fixed time. Skills without their own `l2_<id>_skill` play another skill's class, chosen in skillgrp.dat (out of scope), and every skill playing that class shows the icon; the mapping's `@<id>` points at it.
- `scripts/New-EffectsExperiment.cs` + `scripts/Install-EffectsExperiment.ps1` – manual in-game builds with Core's effect engine (swaps + gold arrow, `--icons` for icons over the target, `--probe` shows each effect class's number over the target to find `@<id>`). Originals go in `work\original-effects`, builds in `work\out\effects-test` / `effects-probe` (with `builds.txt` hashes). Only the user runs the install script on their client (hash-checked; `-Status`, `-Restore`); code and tests never write there. An experiment build in the client is an unknown version to the launcher: restore it with the script before using the effect packs.
- `src/L2Mod.Cli` – .NET 10 CLI: `list`, `export`, `replace`, `badge` for DXT1 textures in `.utx` packages, plus the leader commands `keygen`, `build`, `sign`, `verify`, `publish` (zip → `gh release create dist-<version>` → `gh workflow run pages.yml`; `gh` goes through `ICommandRunner`, faked in tests) and `stage-pages` (run by the Pages workflow); see `recipes/README.md`.
- `src/L2Mod.Core/Publishing` – recipe project loader/compiler, manifest writer, dist builder/verifier, signing keys, `DistArchive` (the published zip; extraction treats it as untrusted), `PagesSite` (verifies a dist against the launcher's key and keeps the previous version's files).
- `recipes/recipes.json` – the leader's pack list (4 icon packs and 2 effect packs, referencing `mappings/`); bump `version` on every change. `build` takes a folder with the originals (`work/original`: Icon.utx, LineageSkillEffect.u, LineageEffect.u). Effect packs need launcher 1.2.0: older launchers skip them with notes, so publish them together with that launcher release. `keys/` holds only the public key.
- `mappings/dances-songs.txt` – Dance/Song icon -> equivalent buff icon (badge) mapping (installed).
- `mappings/chants.txt` (22 chant/Overlord badges), `mappings/debuff-tags.txt` (90 debuff tags: CC/DOT/STAT), `mappings/mob-debuff-tags.txt` (25 monster/boss debuff tags) – approved by the user on 2026-10-19 for the CP launcher packs.
- `spikes/AvaloniaSpike` – throwaway Phase 0 spike (single-file publish measurements, references `L2Mod.Core`); `spikes/sheets/*.py` – review sheet helpers (need user-site Pillow, run without `-I`).
- CP launcher plan: `..\.docs\cp-launcher-v1-plan.md`.
- `tests/L2Mod.Core.Tests` – xUnit (also covers the CLI's `CommandLine`); `TestPackageBuilder`/`TestIcons` build synthetic L2-style packages, `Fakes.cs` has in-memory file system, process monitor and asset store.
- `work/` – local copies of game files and previews (never commit or share: NCSoft assets).
- `.github/workflows/ci.yml` – CI on GitHub `Vegolas/cp-launcher` (push to main, PRs): windows-latest, Release build with `-warnaserror`, all tests, single-file publish with trim warnings as errors, the exe as a 7-day artifact. Never add game files or real keys to CI; the self-update E2E stays manual.
- `.github/workflows/pages.yml` – `workflow_dispatch` (input `tag`, main only): downloads the release zip, `stage-pages` with the launcher's embedded public key plus the live version's release, deploys to GitHub Pages. No private key in CI, ever; signing stays local. Pages caches everything for 600 s and ignores `no-cache` and query strings.
- **The GitHub repo is PUBLIC.** Never commit game files, `work/`, keys, or personal/company data. Commits use the GitHub noreply address (repo-local `user.email`); never commit with the company e-mail. Do not rewrite history by force-push: GitHub keeps old commits reachable (Activity log).

## Commands
```
dotnet test
dotnet run --project src/L2Mod.Cli -- list work/original/Icon.utx skill1235
dotnet run --project src/L2Mod.Cli -- export work/original/Icon.utx icon.skill1235 out.png --scale 8
dotnet run --project src/L2Mod.Cli -- replace work/original/Icon.utx icon.skill1235 new.png work/out/Icon.utx
dotnet run --project src/L2Mod.Cli -- badge work/original/Icon.utx mappings/dances-songs.txt work/out/badges16/Icon.utx --preview work/preview/badges16.png
```

`badge` options: `--size 16` (incl. border; keep a multiple of 4 so only badge blocks are re-encoded), `--border 1`, `--border-color 000000` (saturated colours damage the badge under DXT1), `--crop N` (centre crop of the badge icon before shrinking), `--corner br|bl|tr|tl`, `--preview-scale 4`. Always run it on the original package, otherwise badges stack.

Install / restore in the client (game must be closed):
```
pwsh -File scripts/Install-IconPatch.ps1 -Source work/out/<build>
pwsh -File scripts/Install-IconPatch.ps1 -Source work/out/<build> -Restore
```

## Rules
- Patches are size-preserving and in place: same format, same dimensions, same mip count. No offsets move.
- `replace` refuses to write to its input and to existing files without `--overwrite`.
- Ver121 keys come from the file name, so a patched `Icon.utx` must be installed under that exact name.
- Active Anticheat files (`ActiveAnticheatCrypt` header: all `.dat`, `Interface.xdat`) are out of scope. Do not try to decrypt them.
- Only DXT1 textures are supported (10 430 of 11 112 textures in `Icon.utx`); RGBA8/DXT3 are rejected.
- `badge` (CLI) goes through `OperationComposer`; on `work/original/Icon.utx` with `--size 20` it must stay byte-identical to `work/out/badges20/Icon.utx` (SHA-256 `31813d0d24c7…`).
- The effect engine must keep the output of the experiment script that was tested in game (commit a4cd7d4, before the move to Core): `dotnet run scripts/New-EffectsExperiment.cs -- work/original-effects work/out/effects-check --icons mappings/debuff-icons.txt --icon-package work/original/Icon.utx` on the originals of C:\Lineage2Dex (files of 2026-10-12, Icon.utx = the launcher's backup `11f1bc1e…`) gives LineageSkillEffect.u `cbde648cf4da…` and LineageEffect.u `ca0cada856de…`, as the old script did. A change of its output needs `PatchInstaller.EffectEngineVersion` bumped and a new in-game test.
- Effect operations never ship game data: the icons over the target are copied from the member's own original Icon.utx at install time.
- `L2Mod.Core` stays dependency-free and trim-safe: no reflection-based JSON (use `JsonDocument` or a source-generated context), no extra packages.
- Never write into the client folders from code or tests; installer tests use the fakes, real-file checks use copies under `work/`.
- No keys in the repo: tests generate throwaway ECDSA keys; the real private key lives in `%USERPROFILE%\.l2mod\`.
- Code style: braces everywhere, no one-liner bodies.
