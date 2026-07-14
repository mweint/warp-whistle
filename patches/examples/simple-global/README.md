# Simple global patch example

This nested example is intentionally not discovered by Warp Whistle. Copy this
folder directly under `patches/`, give it a unique id, and replace the example
hook, expected bytes, and ASM behavior with verified PRG1 values.

Omitting `recommendedDefault` makes the recommendation off. Omitting
`supportsLevelOverrides` makes the patch global-only, which is the safe default.
