; Resolve patch flags from up to seven layout-pointer records.
; PATCH_META: upper nibble = record count, lower three bits = global flags.
patch_resolve_flags:
  lda PATCH_META
  lsr a
  lsr a
  lsr a
  lsr a
  tay
  beq patch_resolve_global
  ldx #$00

patch_resolve_loop:
  lda PATCH_TABLE,x
  cmp $7eb9
  bne patch_resolve_next
  lda PATCH_TABLE+1,x
  cmp $7eba
  beq patch_resolve_found

patch_resolve_next:
  inx
  inx
  inx
  dey
  bne patch_resolve_loop

patch_resolve_global:
  lda PATCH_META
  and #$07
  rts

patch_resolve_found:
  lda PATCH_TABLE+2,x
  rts
