---
name: smb3-vanilla-guardrails
description: Preserve original Super Mario Bros. 3 behavior while designing, reviewing, testing, or implementing changes in the Smb3Editor repository. Use for ROM parsing, compilation, level editing, rendering, metadata, export, UI, and tests. Treat mechanics-changing or ROM-expanding "chocolate" work as out of scope unless the user explicitly requests chocolate features and an isolated mode boundary.
---

# SMB3 Vanilla Guardrails

Treat vanilla compatibility as the default product contract.

## Preserve vanilla behavior

- Edit only data the original game already supports: level generators, enemies, junctions, headers, palettes, and existing music selections.
- Preserve mapper, PRG/CHR sizes, bank rules, pointers, executable game code, physics, mechanics, object behavior, and hardware compatibility.
- Prefer accurate modeling, validation, diagnostics, and UI constraints over patching the ROM to accept otherwise invalid data.
- Read graphics, palettes, generator behavior, and sprite previews from the user-supplied ROM. Metadata may come from independently reviewed public documentation.
- Require unchanged projects to compile byte-identically and changed projects to remain valid for the original engine.

## Enforce the chocolate boundary

- Do not implement ROM expansion, mapper changes, custom game code, altered physics, new mechanics, or modified generator/enemy behavior from an ordinary editor request.
- Do not quietly relax vanilla restrictions to improve editor convenience.
- Proceed with chocolate work only when the user explicitly identifies it as chocolate or explicitly authorizes breaking vanilla behavior.
- Require chocolate features to be isolated behind a deliberate project/editor mode switch. Vanilla projects must remain the default and must not acquire chocolate-only data or output accidentally.
- Before implementing the first chocolate feature, define its mode flag, project-format representation, compiler path, compatibility warning, and tests as a separate plan.

## Review every change

Classify the change before editing:

1. **Editor-only:** UI, visualization, workflow, diagnostics, metadata, or safety. Allowed when it does not alter output semantics.
2. **Vanilla data:** Changes data accepted by the stock engine. Allowed with bounds, round-trip, and ROM verification tests.
3. **Chocolate:** Changes executable behavior, storage limits, hardware contract, or supported game semantics. Stop unless explicitly authorized for chocolate mode.

When uncertain, treat the change as chocolate and preserve current vanilla behavior.

## Preserve and expose designer intent

- Do not silently mutate user-authored data or impose artificial editing barriers.
- Prefer allowing an edit, validating the resulting current state, and explaining concrete consequences.
- Diagnose only present, demonstrable conditions; do not warn about hypothetical future misuse.
- Distinguish unusual, unsafe, and unencodable states using evidence from the current document.
- When an automatic correction is genuinely required, state exactly what changed in the UI and make it undoable.
- When rejecting an action or blocking export, identify the exact reason and a practical correction.
- Keep displayed ranges, counts, and controls consistent with states the user can actually reach.
