; Enhanced-only automatic checkpoint patch for verified SMB3 US PRG1.
; It stores the one-player map completion table plus World 1-8 position after
; a normal level return, then restores it after the stock map clear on startup.

; $8437: LDA #$00 / STA ($00),Y
  jsr preserve_save_wram
  nop

; $BCD4: LDA #$00 / STA ($00),Y
  jsr preserve_save_wram
  nop

; $84D1: stock Map_Completions clear loop. The wrapper keeps its exact loop
; behavior, then restores a valid checkpoint once the last byte is cleared.
  jmp map_clear_or_restore
  nop
  nop
  nop

; $9140: normal return-to-map path. Y is $06 for a completed ordinary level
; and $02 for a death; the runtime saves only the completed-level case.
  jmp save_checkpoint_and_return

; Fixed PRG bank 31, CPU $E240.
  base $e240
preserve_save_wram:
  lda $01          ; Reset clear pointer page
  cmp #$79
  bne write_zero
  cpy #$97
  bcs done
write_zero:
  lda #$00
  sta ($00),y
done:
  rts

map_clear_or_restore:
  sta $7d00,y
  dey
  bmi restore_checkpoint
  jmp $84d1

restore_checkpoint:
  lda $7997
  cmp #$57
  bne restore_done
  lda $7998
  cmp #$53
  bne restore_done
  lda $7999
  cmp #$01
  bne restore_done
  lda $799a
  cmp #$08
  bcs restore_done
  sta $0727
  lda $799b
  sta $0075
  lda $799c
  sta $0077
  lda $799d
  sta $0079
  ldx #$00
restore_completions:
  lda $79a0,x
  sta $7d00,x
  inx
  cpx #$40
  bne restore_completions
restore_done:
  jmp $84d7
  dsb $e2c0-$,$ff

; Fixed PRG bank 31, CPU $E911. This range is reserved by the existing patch
; infrastructure and is unused in Enhanced output.
  base $e911
save_checkpoint_and_return:
  cpy #$06
  bne save_done
  lda #$57
  sta $7997
  lda #$53
  sta $7998
  lda #$01
  sta $7999
  lda $0727
  sta $799a
  lda $0075
  sta $799b
  lda $0077
  sta $799c
  lda $0079
  sta $799d
  ldx #$00
save_completions:
  lda $7d00,x
  sta $79a0,x
  inx
  cpx #$40
  bne save_completions
save_done:
  jmp $84d7
  dsb $e980-$,$ff
