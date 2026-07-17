; Quick Retry death hook. Normal completion and time-up retain stock behavior.
quick_retry_exit:
  lda $f1
  beq quick_retry_original
  cmp #$03
  beq quick_retry_original
  jsr patch_resolve_flags
  and #FLAG_QUICK_RETRY
  beq quick_retry_original
  ldx $0726
  lda $0736,x
  beq quick_retry_original
  jsr patch_resolve_flags
  and #FLAG_INFINITE_LIVES
  bne quick_retry_restart
  dec $0736,x

quick_retry_restart:
  jmp $e911

quick_retry_original:
  ldx $0726
  jmp $8f91
