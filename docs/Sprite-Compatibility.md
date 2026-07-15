# Sprite compatibility guide

## What "compatible" means

SMB3 does not expose a separate per-level enemy-graphics-group setting. The useful safe unit is a **vanilla-proven set**: enemies that Nintendo's original ROM loads in the same level. Use one of the sets below as a starting palette. It proves that the level can load their graphics and behavior together.

It does **not** guarantee that every member may occupy the same screen at once. The NES can show only eight hardware sprites on one scanline; crowded tall or large enemies can flicker even when every enemy is individually correct.

## Rules that avoid graphical mistakes

1. Start with the set closest to the level's theme and tileset.
2. Keep the level's original object palette unless intentionally testing a palette change. Palette changes can make valid sprites use the wrong colors.
3. Do not mix special-purpose set pieces (airship cannons, giant-world enemies, boss/fortress machinery, or water-current controllers) into an unrelated set without a Mesen test.
4. Test the actual encounter in Mesen after placing sprites. Test the busiest screen, not just the editor preview.
5. Treat flicker, missing pieces, or incorrect colors as a reason to remove or separate sprites; do not assume that a different placement will fix a graphics-bank conflict.

## Vanilla-proven sets

Every item in a row occurs in the named original level.

| Set | Original level | Sprites proven together |
| --- | --- | --- |
| Ground mix | W1-1 | Goomba; green/red Koopa; Paragoomba; green-hopping Paratroopa; Venus Fire Trap; green Piranha; fire-spitting green Piranha; Goal Card |
| Ground hazards | W3-9 | Green-hopping Paratroopa; green Koopa; Paragoomba with Microgoombas; green-flipped Piranha; Bob-omb; green Cheep-Cheep; Cannon Bullet Bill |
| Plant garden | W7-8 | Green/red Piranha; green-flipped/red-flipped Piranha; Venus Fire Trap; flipped Venus Fire Trap; Nipper; hopping Nipper; fire-spitting Nipper; Patooie; Piranha-and-Spikeball; Goal Card |
| Water | W3-5 | Green Cheep-Cheep; Big Bertha; Blooper; Blooper with Kids; upward/downward water currents |
| Fortress | W6-F3 | Boo; Boo Stretch; Thwomp; clockwise/counter-clockwise Roto Disc; dual Roto Discs; Boss Boom Boom |
| Fortress, small | W2-F | Dry Bones; Thwomp; Boo; Boss Boom Boom |
| Giant World | W4-1 | Giant green/red Koopa; Giant Goomba; Giant green Piranha; Giant green Paratroopa; Venus Fire Trap |
| Sky/platform | W5-9 | Red-flying Paratroopa; Fire Chomp; wood oscillating platforms; Auto-Scroll |
| Airship cannon | W8-T1 | Auto-Scroll; cannon variants; cannon Bob-omb spawners; Fire Jet; Rocky Wrench |
| Airship deck | W8-A | Auto-Scroll; Fire Jet; Rocky Wrench; Airship Propeller; background-cloud event |
| Final castle | W8-C | Cannon Laser; InvisiLift; Hotfoot; Podoboo; Roto Disc; dual-sync Roto Disc; sliding Thwomp; Boss Bowser |

## Deliberately isolated choices

Use these only in a level built around them unless a new combination is emulator-tested:

- Bosses: Bowser, Boom Boom, and the world-dependent Koopaling.
- Giant-world enemies.
- Airship/cannon family: cannons, Fire Jets, Rocky Wrenches, and airship machinery.
- Level controllers and events: Auto-Scroll, Bonus Controller, and event entries. These change level behavior rather than acting as ordinary enemies.
- Water currents and special platform/rotary-lift entries. They are safe with their recorded vanilla sets but need layout-specific testing.

## Evidence and limits

The sets above were read from the verified US PRG1 ROM's original enemy streams using the same catalog locations used by Warp Whistle. They are a conservative authoring reference, not a claim that all other combinations fail. New combinations remain valid candidates, but must be checked in Mesen on the intended level and with the intended palette before being promoted to this guide.
