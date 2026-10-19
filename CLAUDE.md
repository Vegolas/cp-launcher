# L2Mod – cosmetic Lineage 2 client patching tools

Lives inside the L2Dex game client folder (`C:\Lineage2Dex`). The game files around it are **not** part of this project. Build on copies in `work\`; the only way anything reaches `..\SysTextures` is `scripts\Install-IconPatch.ps1` (hash-checked install/restore). Never touch `..\system_season` or other client folders.

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
  - `Recipes/` – mapping files -> operations with the approved styles (`RecipeDefaults`: badges 20 px br, debuff tags 8 px tl in CC/DOT/STAT colours).
  - `Manifests/` – manifest schema 1 model and strict parser; `Signing/` – ECDSA P-256 detached signature of manifest.json.
  - `Installation/` – per-file install state machine (`PatchInstaller`), Option.ini presets (`OptionPresetInstaller`), file-system/process abstractions; `Ini/` – Option.ini merge.
- `src/L2Mod.Cli` – .NET 10 CLI: `list`, `export`, `replace`, `badge` for DXT1 textures in `.utx` packages, plus the leader commands `keygen`, `build`, `sign`, `verify`, `publish` (dry run until hosting is chosen); see `recipes/README.md`.
- `src/L2Mod.Core/Publishing` – recipe project loader/compiler, manifest writer, dist builder/verifier, signing keys.
- `recipes/recipes.json` – the leader's pack list (4 packs, all referencing `mappings/`); bump `version` on every change. `keys/` holds only the public key.
- `mappings/dances-songs.txt` – Dance/Song icon -> equivalent buff icon (badge) mapping (installed).
- `mappings/chants.txt` (22 chant/Overlord badges), `mappings/debuff-tags.txt` (90 debuff tags: CC/DOT/STAT), `mappings/mob-debuff-tags.txt` (25 monster/boss debuff tags) – approved by the user on 2026-10-19 for the CP launcher packs.
- `spikes/AvaloniaSpike` – throwaway Phase 0 spike (single-file publish measurements, references `L2Mod.Core`); `spikes/sheets/*.py` – review sheet helpers (need user-site Pillow, run without `-I`).
- CP launcher plan: `..\.docs\cp-launcher-v1-plan.md`.
- `tests/L2Mod.Core.Tests` – xUnit (also covers the CLI's `CommandLine`); `TestPackageBuilder`/`TestIcons` build synthetic L2-style packages, `Fakes.cs` has in-memory file system, process monitor and asset store.
- `work/` – local copies of game files and previews (never commit or share: NCSoft assets).

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
- `L2Mod.Core` stays dependency-free and trim-safe: no reflection-based JSON (use `JsonDocument` or a source-generated context), no extra packages.
- Never write into the client folders from code or tests; installer tests use the fakes, real-file checks use copies under `work/`.
- No keys in the repo: tests generate throwaway ECDSA keys; the real private key lives in `%USERPROFILE%\.l2mod\`.
- Code style: braces everywhere, no one-liner bodies.
