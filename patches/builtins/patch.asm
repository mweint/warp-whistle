; Warp Whistle built-in chocolate patches for SMB3 US PRG1.
; The manifest maps these fixed-size output blocks to verified ROM locations.

FLAG_QUICK_RETRY = $01
FLAG_START_SELECT = $02
FLAG_FULL_AUTOSCROLL = $04
FLAG_INFINITE_LIVES = $08
PATCH_TABLE = $ff2a
PATCH_META = $ff3f

; Hook output block (17 bytes).
  jmp quick_retry_exit
  jmp start_select_quit
  jsr quick_retry_prepare
  jsr full_autoscroll_end
  nop
  nop
  jsr full_autoscroll_wrapper

; Fixed-bank runtime output block (128 bytes at CPU $E240).
  base $e240
  include quick-retry.asm
  include start-select-map.asm
  include resolver.asm
  dsb $e2c0-$,$ff

; Retry entry, retry preparation, and goal wrapper output block
; (111 bytes at CPU $E911).
  base $e911
  include retry-entry.asm
  include full-autoscroll-wrapper.asm
  dsb $e980-$,$ff

; Auto-scroll runtime output block (167 bytes in PRG bank 9 at CPU $BF59).
  base $bf59
  include full-level-auto-scroll.asm
  dsb $c000-$,$ff

; Per-level configuration output block (22 bytes at CPU $FF2A).
; C# fills seven optional records and PATCH_META after assembly.
  base $ff2a
  dsb 22,$ff
