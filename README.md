# Warp Whistle

A clean, Windows-first Super Mario Bros. 3 level editor built with .NET 10 and Avalonia 12.

The application never ships with a ROM. It accepts only verified US PRG0/PRG1 images supplied by the user, keeps the source immutable, stores edits in a JSON project, and exports a new ROM or BPS patch atomically.

## Local workspace layout

The portable release unzips into a `WarpWhistle` folder. Launch `WarpWhistle.exe`, then place local files here:

```text
ROMs/                      Clean US PRG0 or PRG1 .nes files
Emulators/
    Mesen/                Mesen folder containing Mesen.exe
Projects/                  Editor projects
Exports/                   Exported ROMs and patches
Data/                      Settings, palette library, temporary play tests
```

When no previous choice is saved, Warp Whistle opens the first verified ROM in `ROMs/` and uses `Mesen.exe` found under `Emulators/`. The legacy `externals/` layout remains supported. Item art is kept in memory only; it is not written to a hidden preview cache.

## Current vertical slice

- Verifies clean US PRG0 and PRG1 ROMs by SHA-1 and iNES/MMC3 structure.
- Discovers 80 stock stages across all eight worlds from authenticated ROM pointer tables.
- Moves, adds, deletes, duplicates, and reorders level generators and enemies.
- Uses tileset-aware object names in the catalog instead of exposing raw generator numbers as the primary label.
- Edits screen count, palettes, music selection, and time setting.
- Provides undo/redo, searchable catalogs, byte budgets, diagnostics, and project autosave.
- Exports a new ROM or self-verifying BPS patch without overwriting the source.
- Launches a user-selected external emulator with argument-safe process invocation.
- Renders PRG0 and PRG1 stages from the user's ROM with SMB3's own bounded level generator, metatile tables, CHR graphics, and palettes.
- Renders ROM-derived previews for common enemies, including Goombas, Koopas, Piranha Plants, fish, and Bullet Bills.
- Redraws the generated game view immediately when supported level objects move.
- Tracks the tiles written by each generator so its editor handle and selection outline follow the object it actually creates.
- Uses stable, grid-snapped dragging across all 27 horizontal rows and multi-screen vertical areas.
- Ctrl+mouse-wheel changes zoom in 10% steps from 25% through 800%.
- Remembers and safely reopens the last verified ROM when it is still available.

Enhanced MMC3 storage is an explicit, unfinished chocolate-mode foundation. A project can opt into `EnhancedMmc3`/`ExpandedBanks` in the core model; it deterministically expands PRG1 from 256 KiB to 512 KiB, keeps the original fixed banks at the end, and reserves non-overlapping code, configuration, layout, palette, and music regions. Vanilla projects and exports remain fixed-size and byte-compatible. Runtime relocation and a user-facing mode switch are separate follow-up work.

Generator anchors, junctions, and selection bounds remain editor overlays over the game view. Enemies whose appearance is dynamic or not yet mapped use a fallback position marker; unsafe or unknown data is blocked rather than guessed.

## Development

```powershell
dotnet restore Smb3Editor.slnx
dotnet build Smb3Editor.slnx
dotnet run --project tests/Smb3Editor.Core.Tests
dotnet run --project src/Smb3Editor.App
```

Create the self-contained Windows build with:

```powershell
dotnet publish src/Smb3Editor.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64-warp-whistle
```

ROMs, patches, generated builds, and Warp Whistle `.wwproj` files are ignored by source control.
