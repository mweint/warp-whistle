; Extend a completed stock horizontal controller to the authored level end.
full_autoscroll_end:
  jsr patch_resolve_flags
  and #FLAG_FULL_AUTOSCROLL
  beq full_autoscroll_stock
  lda $0580
  ora #$80
  sta $0580
  lda $7a0a
  cmp $22
  bcs full_autoscroll_boundary
  lda #$08
  sta $7a0e
  lda #$00
  sta $7a0f
  sta $7a11
  sta $7a13
  pla
  pla
  rts

full_autoscroll_boundary:
  lda #$00
  sta $7a0e
  pla
  pla
  rts

full_autoscroll_stock:
  lda #$00
  sta $7a0e
  ldy $7a02
  lda $b934,y
  bne full_autoscroll_end_loop
  sta $7a0f
  sta $7a10
  rts

full_autoscroll_end_loop:
  jmp $bc0c

; Reserve the final 14 bytes of the verified bank-9 fill for the goal helper.
  dsb $bff2-$,$ff

full_autoscroll_goal_helper:
  jsr $bf22
  lda $7a0c
  sta $fd
  lda $7a0a
  sta $12
  rts
