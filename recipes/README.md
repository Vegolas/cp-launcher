# Recipes – authoring format 1

`recipes/` is the CP leader's source for the launcher. `build` compiles it into `dist/` (manifest schema 1,
see `..\.docs\cp-launcher-v1-plan.md` → "Format dystrybucyjny"). Members' launchers only ever see `dist/`.

## Workflow

```
dotnet run --project src/L2Mod.Cli -- keygen                      # once; private key -> %USERPROFILE%\.l2mod\, public -> keys/
dotnet run --project src/L2Mod.Cli -- build recipes work/original/Icon.utx dist [--overwrite]
#   review work/preview/packs/<pack-id>.png (+ .txt index) before signing
dotnet run --project src/L2Mod.Cli -- sign dist
dotnet run --project src/L2Mod.Cli -- verify dist
dotnet run --project src/L2Mod.Cli -- publish dist [--dry-run]    # release dist-<version> + GitHub Pages deployment
```

- `build` needs the **unmodified** `Icon.utx` (named exactly that). Its SHA-256 becomes `supportedOriginals`;
  members with any other `Icon.utx` get nothing applied until the recipes are rebuilt against it.
- Bump `version` whenever the recipes change (`build` warns when the manifest changed but the version did not).
- Preview sheets show NCSoft icons: they go to `work/preview/packs` (or `--previews`), never into `dist/`.
- `--overwrite` replaces an earlier build including its signature. A folder with any other file is refused.
- `sign` refuses a manifest the launcher would not read in full and checks the private key against
  `keys/signing-public.pem` when that file exists.

## Publishing (GitHub Pages)

Launchers read `https://vegolas.github.io/cp-launcher/` (`LauncherDefaults.ManifestSource`). `publish`:

1. verifies `dist/` with `keys/signing-public.pem` and checks that the launcher embeds the same key
   (`src/CpLauncher/Keys/manifest-public.pem`);
2. zips the manifest, its signature and the files it references into `dist-<version>.zip`;
3. `gh release create dist-<version>` on `Vegolas/cp-launcher` (tag on `main`, not marked latest);
4. `gh workflow run pages.yml -f tag=dist-<version>`.

The workflow (`.github/workflows/pages.yml`, main only) downloads the zip, runs `stage-pages` (verifies it against
the key the launcher embeds, refuses any file the manifest does not reference), keeps the files of the version
that was live until now, and deploys. The private key never leaves this computer.

- Once: `gh auth login` as Vegolas; repo Settings → Pages → Source: **GitHub Actions**; after `keygen`, copy
  `keys/signing-public.pem` over `src/CpLauncher/Keys/manifest-public.pem` (and release that launcher).
- A version is published once: the tag exists afterwards, so bump `version` for every publish.
- Roll back by starting the workflow with an older tag (Actions → Pages → Run workflow, or
  `gh workflow run pages.yml -f tag=dist-<old version>`). Launchers accept an older signed manifest; they never
  downgrade themselves.
- GitHub Pages caches everything for 600 s and ignores the launcher's `no-cache`: members get a new version up to
  10 minutes after a green run. Never reuse a file name for different bytes (the workflow refuses it).
- `--dry-run` verifies and prints the two `gh` commands without uploading.

## recipes.json

JSON with `//` comments and trailing commas allowed. Unknown fields are errors. Relative paths are relative to
the folder of `recipes.json`.

| Field | Required | Meaning |
|---|---|---|
| `format` | yes | `1` |
| `version` | yes | manifest version, e.g. `2026.10.20.1` (letters, digits, `.+-`; `build --version` overrides) |
| `enabled` | no (`true`) | `false` is the kill switch: launchers restore the originals |
| `launcher` | no | `{ "version": "1.0.0", "file": "path/to/CpLauncher.exe" }` → `dist/launcher/CpLauncher-1.0.0.exe` + SHA-256. Never reuse a version for different bytes (the CDN may keep serving the old file; the Pages workflow refuses it). |
| `packs` | yes | list of packs, in the order the launcher shows and applies them |
| `announcements` | no | `{ "date": "2026-10-20", "title": "…", "text": "…" }`; `text` may be an array of lines; plain text, no HTML |
| `optionPresets` | no | `{ "id": "mass-pvp", "name": "Masowe PvP", "values": { "Video": { "EffectDetail": "0" } } }`; values are strings or numbers; `GamePlayViewport*` keys are rejected |

### Packs

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | lower-case letters, digits, dashes; stable – members' selections are stored by id |
| `name` | yes | shown in the launcher (≤ 100 characters) |
| `description` | no | shown in the launcher once it supports it (schema 1 launchers ignore it); string or array of lines |
| `default` | no (`false`) | selected for new members |
| `type` | yes | `badge`, `tag` or `overlay` |
| `mapping` | yes | mapping file, one entry per line (below) |
| `size` | no | badge: 20 (4–32), tag: 8 (4–16); multiple of 4 so only whole DXT1 blocks change |
| `corner` | no | `br` `bl` `tr` `tl`; badge/overlay default `br`, tag default `tl` |
| `colors` | no, tag only | `{ "CC": "#E02020", "DOT": "#A040FF", "STAT": "#FF9900" }` (these are the defaults) |

Badge packs always get a 1 px black border and no crop (that is all schema 1 can express).

### Mapping files

Shared line format: `<key> <value>  # label`; blank lines and `#` lines are ignored; keys are unique.

| Type | Line | Example |
|---|---|---|
| `badge` | `<target texture> <badge texture>` | `skill0271  skill1068  # Dance of Warrior -> Might` |
| `tag` | `<target texture> <category>` | `skill0002  CC  # Confusion` |
| `overlay` | `<target texture> <image.png>` (relative to the mapping file, no spaces) | `etc_i.etc_xxx  marks/x.png  # label` |

Overlay PNGs: ≤ 32×32 and ≤ 64 KB, 8-bit RGB/RGBA or indexed. `build` re-encodes them (drops editor
metadata) and stores them as `dist/assets/<sha256>.png`.

The approved mappings live in `../mappings/` and are referenced from here, not copied.

## What build checks

- the manifest is written deterministically and read back by the launcher's parser; anything the launcher
  would skip (bad id, too long text, forbidden preset key, too many operations) fails the build;
- every target and badge texture exists in the original; all packs are applied together exactly as the
  launcher would (size-preserving, verified by re-reading); same-corner overlaps are reported as warnings;
- one before/after sheet per pack, the "after" decoded from the encoded file.
