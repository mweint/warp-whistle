# Architecture

## Safety boundary

`Smb3Editor.Core` owns every operation that reads or writes binary data. The UI cannot address ROM offsets directly. `RomImage.Load` validates the iNES structure, normalizes away untrusted trailing bytes, checks mapper and dimensions, and requires a known full-image SHA-1 before exposing a `RomImage`.

The source byte array is immutable by convention. `RomCompiler` always creates a fresh copy, encodes changed documents into verified ranges, rejects capacity overflow, and returns an in-memory artifact. `AtomicFile` is the only output path: it writes and flushes a same-directory temporary file before replacing the destination, retaining one backup where requested.

## Data flow

1. `RomImage.Load` selects a `RomProfile`.
2. `Smb3LevelCodec.Decode` requires per-area byte signatures and creates an immutable `LevelDocument`.
3. UI commands replace the document and store prior snapshots in `UndoRedoHistory`.
4. `ProjectDocumentV2` stores normalized changed areas and symbolic area IDs, never ROM bytes. Version 1 projects are migrated on load with explicit diagnostics for ambiguous positions.
5. `RomCompiler` validates and compiles edits from the original source.
6. `BpsCodec` creates and reapplies the patch in memory before the UI writes it.

## Rendering

`Smb3LevelRenderer` encodes the current immutable document into isolated memory and executes the verified PRG1 ROM's own level loader with a hard instruction budget. Only the required PRG banks are mapped into a private 64 KiB `Cpu6502Sandbox`; generated metatiles are read from the sandbox's tile-memory region. Bounded write observation records which metatiles each generator affected so editor handles and hit testing use the generated result instead of assuming the encoded command coordinate is its visible top-left corner. The renderer then resolves the ROM's tileset metatile table, background CHR selectors, CHR bitplanes, and selected palette into an ARGB bitmap. No graphics are extracted into the project or distributed with the application.

The Avalonia canvas caches that bitmap, scales it with nearest-neighbor interpolation, and draws editor-only anchors, junction bounds, screen boundaries, and selections above it. The renderer composes previews for common enemies directly from the source ROM's CHR banks and object palette; dynamic or not-yet-mapped enemies retain a fallback position marker. PRG0 execution requires a separate verified profile and currently falls back to diagnostics rather than speculative rendering.

Horizontal generators use the full five-bit SMB3 row field. Vertical generators and enemies use orientation-specific screen/row packing normalized to the editor's 15-row vertical screens. Original raw position bytes remain attached to typed models so unrelated edits preserve reserved bits. Junction records remain read-only until symbolic destination editing is implemented.

## Compatibility policy

Only areas with a known ROM revision, fixed bounds, and independent stream signatures are editable. Unsupported revisions, ROM hacks, moved data, malformed commands, and exhausted byte budgets are diagnostics—not guesses. This intentionally favors a small safe catalog over broad corruption-prone compatibility.
