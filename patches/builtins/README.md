# Built-in ASM patch suite

This is an advanced multi-feature package. `patch.asm` assembles Quick Retry,
Start + Select, and Full-Level Auto-Scroll together because they share a fixed
bank resolver, configuration table, and limited verified free regions.

`patch.json` is authoritative for discovery, display metadata, recommended
defaults, profile compatibility, per-level support, requirements, and verified
ROM writes. The editor owns each project's global and per-level choices; the ASM
only reads the generated configuration.

For a small package layout, see `patches/examples/simple-global`.
