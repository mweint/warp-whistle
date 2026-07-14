; While paused, Select requests SMB3's normal return-to-map path.
start_select_quit:
  lda $0376
  beq start_select_original
  lda $18
  ora $0517
  and #$20
  beq start_select_original
  jsr patch_resolve_flags
  and #FLAG_START_SELECT
  beq start_select_original
  lda #$00
  sta $f1
  lda #$01
  sta $0713
  sta $14
  jmp $8f31

start_select_original:
  lda $04e7
  jmp $8e60
