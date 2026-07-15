# SMAS-SMB3 editor handoff

Reviewed 2026-07-15 from the current Warp Whistle working tree and the exploratory `smas-smb3` workspace. This supersedes the initial chat review from 2026-07-13.

## Decision

Keep SMAS-SMB3 as an independent project. Reuse platform-neutral infrastructure and verified engineering patterns, not the NES backend.

Support both US standalone Super Mario All-Stars and US Super Mario All-Stars + Super Mario World as separate authenticated ROM profiles over one SMAS-SMB3 format backend. Direct comparison now shows that SMAS+SMW support is a profile/container problem first, not a second level-editor implementation:

- all 680 raw SMB3 pointer-table entries match standalone SMAS;
- World 1-1's complete level and enemy streams match byte-for-byte;
- all mapped 32-byte stream samples match across 116 distinct enemy targets and 120 distinct level targets;
- the full executable/data banks are not identical, so patches and hook signatures must remain profile-specific.

The initial port is clearly feasible. The old multi-week estimate was too conservative for a GPT-assisted prototype given Warp Whistle's 2-3 day result and the new profile evidence. Combined-ROM recognition and catalog parity are hours-scale work. An exact codec, renderer, and safe first write remain evidence-gated; do not assign a full-editor date until those three gates pass.

## Snapshot and caveats

Warp Whistle was reviewed on branch `codex/expanded-layout-capacity` at `00345cf` (`v0.1.0-alpha.1`) with a substantially dirty working tree. The review includes those uncommitted files, not only the tag. Major uncommitted areas include overworld editing, project-format changes, enhanced storage/patch updates, packaging, and release automation. Do not copy the branch wholesale or treat every reviewed capability as released.

Current validation with the authenticated US PRG1 test ROM:

- Release build: succeeded with 0 warnings and 0 errors.
- Executable xUnit suite: 89 total, 87 passed, 2 failed, 0 skipped.
- Failures: `UsesVerifiedCornerTileAtAPathTurn` and `EraseLakeStroke_Uses42AndReshapesNearbyWater` in `OverworldTerrainBrushTests`.
- `dotnet test` was not usable in this environment because its SDK testhost dependency was absent. The repository's in-process executable runner was used with `dotnet run --project tests/Smb3Editor.Core.Tests`.

The level catalog, codec, renderer, project, patch, BPS, and overworld parser/serializer tests passed. The new terrain auto-brush is the unsettled portion and should not be transferred as verified logic.

The `smas-smb3` repository currently has no commits; every project file is untracked on `main`. Preserve that baseline in source control before beginning the next implementation slice.

Its existing Debug test artifact passes all 6 synthetic read-only tests. It was executed without rebuilding because this review does not have write access to that sibling workspace; run a clean build there when work switches over.

## New SMAS+SMW evidence

The user-supplied US combined image was compared read-only against the authenticated standalone US image.

| Property | Standalone SMAS | SMAS+SMW |
| --- | --- | --- |
| Payload | 2,097,152 bytes | 2,621,440 bytes (20 Mbit) |
| Internal title | `SUPER MARIO ALL_STARS` | `ALL_STARS + WORLD` |
| Map mode | `$20` LoROM | `$20` LoROM |
| ROM-size code | `$0B` | `$0C` |
| SHA-1 | `c05817c5b7df2fbfe631563e0b37237156a8f6b6` | `d245e41a2b590f7d63666b0772cbddfb26f254a2` |
| Header checksum | `$AA5C` | `$60D8` |

The combined SHA-1 is independently cataloged as the good US image by [TASVideos/No-Intro](https://tasvideos.org/Games/3209/Versions/View/3422).

The World 1-1 roots are unchanged in both images:

- enemy table `$21D8F3` -> data `$27EAD7`;
- level table `$21D932` -> data `$249F2E`.

All 340 level pointers and all 340 enemy pointers match. World 1-1 has an identical 256-byte header/layout stream through its terminator and an identical 47-byte enemy stream through its terminator.

This is not evidence that the whole SMB3 payload is identical. Banks `$20-$2A` differ by 32,331 bytes, mainly bank `$20` (21,460) and bank `$27` (9,837). Yoshifanatic's SMAS+W work likewise treats the 2.5 MiB target and version-specific mapping as deliberate concerns; see the [author's technical notes](https://www.smwcentral.net/?p=viewthread&t=96112).

Implications:

- share the level/enemy catalog and, once proven, the codec;
- keep immutable source identities, container sizes, header rules, writable profiles, patch signatures, and resource assumptions separate;
- compile back into the same source variant and preserve every byte outside deliberate SMB3 edits;
- never convert standalone SMAS to SMAS+SMW, or the reverse, as a side effect of export;
- treat the SMW portion as preserved opaque data while the product scope is SMB3 only.

The current `smas-smb3` loader rejects SMAS+SMW because it hardcodes a 2 MiB payload and the standalone internal title. Add a second strict profile; do not weaken those checks.

## What Warp Whistle now teaches us

Since the first review, Warp Whistle has added several lessons that should shape the new editor:

1. **Authenticated source profiles work.** PRG0 and PRG1 use distinct identities while sharing a logical editor backend. Apply that exact separation to standalone SMAS and SMAS+SMW.
2. **Catalog first, UI second.** Warp Whistle enumerates 80 stages dynamically, bounds every stream, round-trips every decoded stream, renders the full catalog, and mutation-sweeps movable coordinates. SMAS should establish equivalent coverage over all 340 pointer slots before export.
3. **Aliases are first-class.** The 340 SMAS slots collapse to far fewer distinct raw targets. Record shared ownership before relocating or resizing anything; one apparent stage edit may affect multiple slots.
4. **Run-time rendering can reduce reimplementation.** Warp Whistle's bounded 6502 generator gives high fidelity, but that code cannot move to SNES. Prototype a bounded 65C816 generator against a managed SNES renderer and choose after both render World 1-1 from ROM-derived graphics.
5. **Vanilla and enhanced modes need a hard boundary.** Warp Whistle's output/storage enums, immutable clean-source checks, and explicit enhanced builder are useful architecture. MMC3 expansion rules are not.
6. **Patch packages need signatures.** Manifests, supported-profile lists, exact original-byte signatures, bounded assembled writes, deterministic output, and default-off features should carry forward. ASM6f source, 6502 bytes, and NES hook locations should not.
7. **State patches require full-state analysis.** Quick Retry exposed stale RAM; enhanced saves required an explicit schema and reset-preservation rules; auto-scroll required frame traces. Their desired behavior is reusable, but every 65C816 hook and SMAS SRAM field must be rediscovered per profile.
8. **Overworlds are tables plus constraints, not just pictures.** The NES work found shared page pools, pointer aliases, fixed selector tables, and terrain whose graphics do not prove traversal behavior. Reuse the investigation method, not any NES address or capacity.
9. **Optional ROM tests must be visible.** Warp Whistle's ROM-backed tests return early when no ROM is configured. The new project should report ROM integration tests as explicit skips or a separate required command so a synthetic-only pass cannot be mistaken for full verification.

## Reuse boundary

| Treatment | Warp Whistle material | SMAS-SMB3 use |
| --- | --- | --- |
| Deliberately extract with tests | `Diagnostics.cs`, `AtomicFile.cs`, `BpsCodec.cs`, `UndoRedoHistory.cs`, `BankAllocator.cs` | Platform-neutral utilities; rename namespaces and keep the projects independent. |
| Adapt the design | `ProjectDocument.cs`, `ProjectStore.cs`, `RomCatalogBuilder.cs`, `EmulatorLauncher.cs`, app settings/workspace handling | Create SMAS-native records, migrations, catalog ownership, and source-profile checks. |
| Use as a test-pattern reference | `RomImageTests.cs`, `LevelCodecTests.cs`, `Smb3LevelRendererTests.cs`, `AsmPatchCompilerTests.cs`, `EnhancedMmc3RomBuilderTests.cs` | Exact unchanged round trips, full-catalog sweeps, bounds checks, signature rejection, deterministic builds, and readback verification. |
| Learn from, then rewrite | `Smb3LevelCodec.cs`, `Smb3LevelRenderer.cs`, `OverworldModels.cs`, `Asm6fPatchCompiler.cs`, `ChocolateAddOnCompiler.cs` | SMAS headers, 24-bit pointers, 65C816 execution, SNES graphics/palettes, SMAS tables, and a SNES patch toolchain. |
| Do not transfer | `RomImage.cs`, `RomProfile.cs`, `Cpu6502Sandbox.cs`, `ChrTileDecoder.cs`, `EnhancedMmc3RomBuilder.cs`, `DirectLevelTestBuilder.cs`, NES patch sources | iNES/MMC3, 6502, CHR 2bpp, NES RAM, bank layouts, hooks, and executable assumptions are platform-specific. |

Do not add NES/SMAS conditionals throughout either core and do not project-reference Warp Whistle. Small generic files may be deliberately extracted only when their tests move with them.

## Patch value

Existing patches are useful as behavioral specifications and failure-case studies:

- Quick Retry: normalize the same state a stock death/reload path normalizes before re-entry.
- Return to map: identify the canonical stock loader and all controller-state sources.
- Continuous auto-scroll: preserve the intended end-of-level and goal-card behavior, verified with frame traces.
- Infinite lives: keep single-purpose hooks narrow and signature-checked.
- Enhanced saves: define an explicit versioned state schema and restore boundary.

None of the NES patch bytes, ASM6f directives, mapper banking, `$6000-$7FFF` assumptions, or `$7997-$79FF` storage plan are portable. SMAS and SMAS+SMW already have stock SRAM, and the two ROM profiles need independent executable signatures even where their level data matches.

## Recommended next slice in `smas-smb3`

1. Commit the existing read-only baseline in the user's normal Git client/terminal.
2. Replace `SmasRomImage`'s single hardcoded shape with two immutable US profiles: standalone SMAS and SMAS+SMW. Accept the optional 512-byte copier header for each, but hash and address only the normalized payload.
3. Add tests proving both identities, exact titles/sizes/header fields, rejection of near matches, preservation of container kind, and identical traversal of the 680 SMB3 pointer entries.
4. Extend the probe to report aliases, sentinels, invalid mappings, allocation boundaries, and complete stream hashes for every distinct target. Require exact cross-profile equality or record every exception.
5. Implement the 13-byte SMAS level header and object/enemy command codecs. Decode and re-encode every distinct stream byte-identically before exposing edits.
6. Prototype World 1-1 rendering from each source ROM. Compare a bounded 65C816 generator with a managed renderer using SNES 4bpp graphics, tilemaps, and palettes.
7. Make the first write deliberately small: move one object without changing stream length, compile into a fresh copy of the same profile, reopen it, require exact readback, and test it in Mesen2.
8. Add project persistence, undo/redo, BPS export, and the Avalonia shell only after the write gate passes.
9. Defer overworld editing, stream relocation, enhanced storage, and executable patches until the vanilla level slice is byte-identical and emulator-verified for both profiles.

## Handoff sources

Start with these Warp Whistle files, in this order:

- `README.md` and `docs/ARCHITECTURE.md` for the current product and safety boundary;
- `.local/references/verified-knowledge.md`, copying only the new SMAS+SMW comparison into the SMAS project's ledger;
- `src/Smb3Editor.Core/Diagnostics.cs`, `AtomicFile.cs`, `BpsCodec.cs`, `UndoRedoHistory.cs`, and their tests for possible extraction;
- `RomCatalogBuilder.cs`, `Smb3LevelCodec.cs`, `Smb3LevelRenderer.cs`, and their tests as architectural references only;
- `Asm6fPatchCompiler.cs`, patch manifests, and patch tests for package/signature design only;
- `OverworldModels.cs` and the overworld knowledge entries only as examples of how to prove table ownership and capacity.

Do not transfer ROMs, extracted Nintendo assets, local ROM paths, Foundry GPL implementation code, or disassembly code. Record sources and independently implement from documented behavior and authenticated ROM observations.

## Completion gate for the first editor milestone

The first SMAS-SMB3 milestone is complete only when both authenticated US profiles:

- normalize and identify correctly;
- enumerate the same understood 340 slots without out-of-bounds reads;
- decode and unchanged-round-trip every supported distinct stream exactly;
- render World 1-1 from their own ROM-derived assets;
- compile one same-size object move while preserving container size and all unrelated bytes;
- reopen the output and reproduce the edit exactly;
- boot and play the edited level in Mesen2.

That milestone is enough to start the actual editor UI with confidence. It does not yet justify relocation, enhanced patches, SMW editing, or hardware compatibility claims.
