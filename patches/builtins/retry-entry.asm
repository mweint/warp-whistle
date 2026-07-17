; Re-enter level preparation after clearing the stock death/gameplay state.
quick_retry_entry:
  ldx #$ff
  txs
  ; Retain page $07 because it contains the persistent active-area and mapper
  ; context restored by the retry flow.
  ldy #$06
  jsr $96ce
  inc $7955
  lda #$28
  sta $2000
  sta $ff
  lda #$00
  sta $2001
  lda #$04
  sta $05ee
  lda $7eb9
  sta $61
  lda $7eba
  sta $62
  lda $7ebb
  sta $65
  lda $7ebc
  sta $66
  lda #$00
  sta $0713
  lda #$01
  sta $7ef0
  jmp $88c8

; The normal loader calls Map_PrepareLevel. Retry already retained the active
; area pointers, so skip that call for one pass only.
quick_retry_prepare:
  lda $7ef0
  beq quick_retry_prepare_original
  lda #$00
  sta $7ef0
  rts

quick_retry_prepare_original:
  jsr $b0ff
  rts

; Keep the auto-scroll wrapper at the verified CPU address used by Play Level.
  dsb $e95d-$,$ff
