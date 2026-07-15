# Verified SMB3 Knowledge Ledger

Use this as a compact, evidence-backed reference for future editor and add-on work. Entries apply to US PRG1 unless stated otherwise.

## Entry format

Record: `Claim | Evidence/source | Confidence | Last checked`.

Do not record guesses as facts. Keep unresolved alternatives explicit until a ROM or emulator test distinguishes them.

## Current entries

- The standard PRG1 image uses MMC3 mapper 4 with fixed-size PRG/CHR regions. | ROM header validation and profile parser tests. | High | 2026-07-13
- The stock death-exit hook site is file offset `$3CF9E`, containing `AE 26 07` in an unmodified PRG1 ROM and mapping to CPU `$8F8E`. | `ChocolateAddOnCompiler`, PRG1 bytes, Mesen trace. | High | 2026-07-13
- The stock pause/exit hook site is file offset `$3CE6D`, containing `AD E7 04`. | `ChocolateAddOnCompiler` signature checks and PRG1 bytes. | High | 2026-07-13
- Newly pressed controller buttons are read from zero-page `$18`; `$0517` holds the companion controller state used by the pause Start+Select check. | Working C# patch bytes, ASM6f output regression test, and PRG1 pause-path testing. | High | 2026-07-14
- `$00F1` is the player dying-state byte; `$0736` is the player lives byte; `$0713` is map return status; `$0014` is the level-exit request byte. | Southbird PRG1 disassembly and Mesen memory-write traces. | High | 2026-07-13
- The verified direct-level harness enters level preparation through CPU `$88C8`; its startup path also disables title updates and establishes PPU state before the jump. | `DirectLevelTestBuilder` and successful Play Level behavior. | High | 2026-07-13
- `$88C8` performs stock level setup and eventually calls the fixed-bank video/pointer helper at `$FE99` through the `$9A1D` loader path. | PRG1 disassembly/ROM bytes and Mesen execution trace. | High | 2026-07-13
- The `$FE99` helper consumes stack-derived parameters and performs an indirect jump through zero page; entering it with an invalid stack or loader state can loop or crash. | PRG1 bytes and Mesen trace showing the `$FE99` loop. | Medium-high | 2026-07-13
- Trace windows must be frame-based, not instruction-count-based; execution callbacks can fire thousands of times during one level-load loop. | `tools/retry-trace.lua` behavior and Mesen logs. | High | 2026-07-13
- Any change to a shared stock level-load call can corrupt ordinary level startup even when it appears to fix retry. | Reverted experimental retry prepare-hook patch and observed malformed level load. | High | 2026-07-13
- `$88C8` expects the preceding `Map_PrepareLevel` path to have established the active object/layout/enemy pointers; a retry jump that skips that preparation reaches the loader with invalid state. | PRG1 `prg030.asm`, Mesen trace showing `$E911 -> $88C8 -> $9A49/$FE99` looping, and guarded preparation-hook tests. | High | 2026-07-13
- A retry handoff must clear `Level_PauseFlag` at `$0376`, `Player_IsDying` at `$00F1`, `Level_ExitToMap` at `$0014`, and the sound state/queues at `$04E0-$04F7`; otherwise pause/death music can survive into the restarted level. | PRG1 symbol map, disassembly, and observed pause-select/death-jingle behavior. | High | 2026-07-13
