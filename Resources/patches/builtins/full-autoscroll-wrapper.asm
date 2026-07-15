; Continue the camera after an early goal only for an extended controller.
full_autoscroll_wrapper:
  lda $0580
  and $7974
  bpl full_autoscroll_normal

full_autoscroll_goal:
  lda #$01
  sta $05fc
  lda $7a0a
  cmp $22
  lda #$00
  sbc #$00
  and #$08
  sta $7a0e
  ldx #$00
  jmp full_autoscroll_goal_helper

full_autoscroll_normal:
  jmp $b900
