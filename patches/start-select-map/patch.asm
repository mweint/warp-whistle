; SMB3 US PRG1 example. WW reads these lines to safely apply the output below.
; @ww profile us-prg1
; @ww hook $3CE6D expect ADE704
; @ww free $3E250 size 128 fill FF

; Output order: a three-byte hook, then the routine in unused fixed-bank space.

; Replace the stock pause-input load at CPU $8E5D with JMP $E240.
  jmp $e240

; This routine runs only while the game is paused.  Select requests the stock
; "return to map" path; otherwise, reproduce the instruction we replaced.
  base $e240
  lda $0376                 ; pause flag
  beq original_pause
  lda $0518                 ; newly pressed buttons
  ora $0517                 ; held buttons
  and #$20                  ; Select
  beq original_pause
  lda #$01
  sta $0713                 ; leave level without completion
  sta $0014                 ; request map exit
  jmp $8f31                 ; stock exit handler

original_pause:
  lda $04e7                 ; original instruction
  jmp $8e60                 ; continue after the hook
