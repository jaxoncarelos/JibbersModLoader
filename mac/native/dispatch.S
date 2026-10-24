.text
#if defined(__aarch64__)
.p2align 2
_jh_stub0:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #0
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub0:
1:  movq _jh_destinations+0(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub1:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #8
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub1:
1:  movq _jh_destinations+8(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub2:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #16
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub2:
1:  movq _jh_destinations+16(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub3:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #24
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub3:
1:  movq _jh_destinations+24(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub4:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #32
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub4:
1:  movq _jh_destinations+32(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub5:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #40
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub5:
1:  movq _jh_destinations+40(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub6:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #48
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub6:
1:  movq _jh_destinations+48(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub7:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #56
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub7:
1:  movq _jh_destinations+56(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub8:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #64
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub8:
1:  movq _jh_destinations+64(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub9:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #72
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub9:
1:  movq _jh_destinations+72(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub10:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #80
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub10:
1:  movq _jh_destinations+80(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub11:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #88
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub11:
1:  movq _jh_destinations+88(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub12:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #96
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub12:
1:  movq _jh_destinations+96(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub13:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #104
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub13:
1:  movq _jh_destinations+104(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub14:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #112
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub14:
1:  movq _jh_destinations+112(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub15:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #120
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub15:
1:  movq _jh_destinations+120(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub16:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #128
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub16:
1:  movq _jh_destinations+128(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub17:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #136
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub17:
1:  movq _jh_destinations+136(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub18:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #144
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub18:
1:  movq _jh_destinations+144(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub19:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #152
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub19:
1:  movq _jh_destinations+152(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub20:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #160
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub20:
1:  movq _jh_destinations+160(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub21:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #168
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub21:
1:  movq _jh_destinations+168(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub22:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #176
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub22:
1:  movq _jh_destinations+176(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub23:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #184
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub23:
1:  movq _jh_destinations+184(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub24:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #192
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub24:
1:  movq _jh_destinations+192(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub25:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #200
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub25:
1:  movq _jh_destinations+200(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub26:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #208
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub26:
1:  movq _jh_destinations+208(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub27:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #216
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub27:
1:  movq _jh_destinations+216(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub28:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #224
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub28:
1:  movq _jh_destinations+224(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub29:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #232
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub29:
1:  movq _jh_destinations+232(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub30:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #240
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub30:
1:  movq _jh_destinations+240(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub31:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #248
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub31:
1:  movq _jh_destinations+248(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub32:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #256
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub32:
1:  movq _jh_destinations+256(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub33:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #264
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub33:
1:  movq _jh_destinations+264(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub34:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #272
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub34:
1:  movq _jh_destinations+272(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub35:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #280
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub35:
1:  movq _jh_destinations+280(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub36:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #288
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub36:
1:  movq _jh_destinations+288(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub37:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #296
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub37:
1:  movq _jh_destinations+296(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub38:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #304
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub38:
1:  movq _jh_destinations+304(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub39:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #312
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub39:
1:  movq _jh_destinations+312(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub40:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #320
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub40:
1:  movq _jh_destinations+320(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub41:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #328
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub41:
1:  movq _jh_destinations+328(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub42:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #336
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub42:
1:  movq _jh_destinations+336(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub43:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #344
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub43:
1:  movq _jh_destinations+344(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub44:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #352
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub44:
1:  movq _jh_destinations+352(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub45:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #360
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub45:
1:  movq _jh_destinations+360(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub46:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #368
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub46:
1:  movq _jh_destinations+368(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub47:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #376
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub47:
1:  movq _jh_destinations+376(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub48:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #384
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub48:
1:  movq _jh_destinations+384(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub49:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #392
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub49:
1:  movq _jh_destinations+392(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub50:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #400
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub50:
1:  movq _jh_destinations+400(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub51:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #408
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub51:
1:  movq _jh_destinations+408(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub52:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #416
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub52:
1:  movq _jh_destinations+416(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub53:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #424
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub53:
1:  movq _jh_destinations+424(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub54:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #432
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub54:
1:  movq _jh_destinations+432(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub55:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #440
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub55:
1:  movq _jh_destinations+440(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub56:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #448
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub56:
1:  movq _jh_destinations+448(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub57:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #456
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub57:
1:  movq _jh_destinations+456(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub58:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #464
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub58:
1:  movq _jh_destinations+464(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub59:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #472
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub59:
1:  movq _jh_destinations+472(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub60:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #480
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub60:
1:  movq _jh_destinations+480(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub61:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #488
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub61:
1:  movq _jh_destinations+488(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub62:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #496
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub62:
1:  movq _jh_destinations+496(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub63:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #504
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub63:
1:  movq _jh_destinations+504(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub64:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #512
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub64:
1:  movq _jh_destinations+512(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub65:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #520
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub65:
1:  movq _jh_destinations+520(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub66:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #528
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub66:
1:  movq _jh_destinations+528(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub67:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #536
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub67:
1:  movq _jh_destinations+536(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub68:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #544
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub68:
1:  movq _jh_destinations+544(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub69:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #552
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub69:
1:  movq _jh_destinations+552(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub70:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #560
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub70:
1:  movq _jh_destinations+560(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub71:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #568
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub71:
1:  movq _jh_destinations+568(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub72:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #576
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub72:
1:  movq _jh_destinations+576(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub73:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #584
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub73:
1:  movq _jh_destinations+584(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub74:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #592
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub74:
1:  movq _jh_destinations+592(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub75:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #600
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub75:
1:  movq _jh_destinations+600(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub76:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #608
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub76:
1:  movq _jh_destinations+608(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub77:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #616
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub77:
1:  movq _jh_destinations+616(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub78:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #624
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub78:
1:  movq _jh_destinations+624(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub79:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #632
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub79:
1:  movq _jh_destinations+632(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub80:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #640
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub80:
1:  movq _jh_destinations+640(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub81:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #648
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub81:
1:  movq _jh_destinations+648(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub82:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #656
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub82:
1:  movq _jh_destinations+656(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub83:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #664
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub83:
1:  movq _jh_destinations+664(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub84:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #672
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub84:
1:  movq _jh_destinations+672(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub85:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #680
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub85:
1:  movq _jh_destinations+680(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub86:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #688
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub86:
1:  movq _jh_destinations+688(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub87:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #696
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub87:
1:  movq _jh_destinations+696(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub88:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #704
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub88:
1:  movq _jh_destinations+704(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub89:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #712
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub89:
1:  movq _jh_destinations+712(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub90:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #720
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub90:
1:  movq _jh_destinations+720(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub91:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #728
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub91:
1:  movq _jh_destinations+728(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub92:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #736
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub92:
1:  movq _jh_destinations+736(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub93:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #744
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub93:
1:  movq _jh_destinations+744(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub94:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #752
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub94:
1:  movq _jh_destinations+752(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub95:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #760
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub95:
1:  movq _jh_destinations+760(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub96:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #768
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub96:
1:  movq _jh_destinations+768(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub97:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #776
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub97:
1:  movq _jh_destinations+776(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub98:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #784
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub98:
1:  movq _jh_destinations+784(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub99:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #792
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub99:
1:  movq _jh_destinations+792(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub100:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #800
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub100:
1:  movq _jh_destinations+800(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub101:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #808
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub101:
1:  movq _jh_destinations+808(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub102:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #816
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub102:
1:  movq _jh_destinations+816(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub103:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #824
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub103:
1:  movq _jh_destinations+824(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub104:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #832
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub104:
1:  movq _jh_destinations+832(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub105:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #840
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub105:
1:  movq _jh_destinations+840(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub106:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #848
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub106:
1:  movq _jh_destinations+848(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub107:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #856
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub107:
1:  movq _jh_destinations+856(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub108:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #864
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub108:
1:  movq _jh_destinations+864(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub109:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #872
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub109:
1:  movq _jh_destinations+872(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub110:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #880
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub110:
1:  movq _jh_destinations+880(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub111:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #888
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub111:
1:  movq _jh_destinations+888(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub112:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #896
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub112:
1:  movq _jh_destinations+896(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub113:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #904
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub113:
1:  movq _jh_destinations+904(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub114:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #912
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub114:
1:  movq _jh_destinations+912(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub115:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #920
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub115:
1:  movq _jh_destinations+920(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub116:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #928
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub116:
1:  movq _jh_destinations+928(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub117:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #936
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub117:
1:  movq _jh_destinations+936(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub118:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #944
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub118:
1:  movq _jh_destinations+944(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub119:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #952
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub119:
1:  movq _jh_destinations+952(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub120:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #960
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub120:
1:  movq _jh_destinations+960(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub121:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #968
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub121:
1:  movq _jh_destinations+968(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub122:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #976
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub122:
1:  movq _jh_destinations+976(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub123:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #984
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub123:
1:  movq _jh_destinations+984(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub124:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #992
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub124:
1:  movq _jh_destinations+992(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub125:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1000
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub125:
1:  movq _jh_destinations+1000(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub126:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1008
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub126:
1:  movq _jh_destinations+1008(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub127:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1016
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub127:
1:  movq _jh_destinations+1016(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub128:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1024
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub128:
1:  movq _jh_destinations+1024(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub129:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1032
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub129:
1:  movq _jh_destinations+1032(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub130:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1040
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub130:
1:  movq _jh_destinations+1040(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub131:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1048
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub131:
1:  movq _jh_destinations+1048(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub132:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1056
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub132:
1:  movq _jh_destinations+1056(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub133:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1064
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub133:
1:  movq _jh_destinations+1064(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub134:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1072
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub134:
1:  movq _jh_destinations+1072(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub135:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1080
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub135:
1:  movq _jh_destinations+1080(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub136:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1088
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub136:
1:  movq _jh_destinations+1088(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub137:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1096
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub137:
1:  movq _jh_destinations+1096(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub138:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1104
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub138:
1:  movq _jh_destinations+1104(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub139:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1112
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub139:
1:  movq _jh_destinations+1112(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub140:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1120
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub140:
1:  movq _jh_destinations+1120(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub141:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1128
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub141:
1:  movq _jh_destinations+1128(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub142:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1136
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub142:
1:  movq _jh_destinations+1136(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub143:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1144
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub143:
1:  movq _jh_destinations+1144(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub144:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1152
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub144:
1:  movq _jh_destinations+1152(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub145:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1160
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub145:
1:  movq _jh_destinations+1160(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub146:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1168
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub146:
1:  movq _jh_destinations+1168(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub147:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1176
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub147:
1:  movq _jh_destinations+1176(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub148:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1184
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub148:
1:  movq _jh_destinations+1184(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub149:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1192
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub149:
1:  movq _jh_destinations+1192(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub150:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1200
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub150:
1:  movq _jh_destinations+1200(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub151:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1208
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub151:
1:  movq _jh_destinations+1208(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub152:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1216
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub152:
1:  movq _jh_destinations+1216(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub153:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1224
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub153:
1:  movq _jh_destinations+1224(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub154:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1232
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub154:
1:  movq _jh_destinations+1232(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub155:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1240
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub155:
1:  movq _jh_destinations+1240(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub156:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1248
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub156:
1:  movq _jh_destinations+1248(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub157:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1256
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub157:
1:  movq _jh_destinations+1256(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub158:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1264
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub158:
1:  movq _jh_destinations+1264(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub159:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1272
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub159:
1:  movq _jh_destinations+1272(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub160:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1280
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub160:
1:  movq _jh_destinations+1280(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub161:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1288
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub161:
1:  movq _jh_destinations+1288(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub162:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1296
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub162:
1:  movq _jh_destinations+1296(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub163:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1304
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub163:
1:  movq _jh_destinations+1304(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub164:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1312
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub164:
1:  movq _jh_destinations+1312(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub165:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1320
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub165:
1:  movq _jh_destinations+1320(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub166:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1328
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub166:
1:  movq _jh_destinations+1328(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub167:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1336
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub167:
1:  movq _jh_destinations+1336(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub168:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1344
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub168:
1:  movq _jh_destinations+1344(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub169:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1352
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub169:
1:  movq _jh_destinations+1352(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub170:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1360
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub170:
1:  movq _jh_destinations+1360(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub171:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1368
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub171:
1:  movq _jh_destinations+1368(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub172:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1376
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub172:
1:  movq _jh_destinations+1376(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub173:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1384
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub173:
1:  movq _jh_destinations+1384(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub174:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1392
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub174:
1:  movq _jh_destinations+1392(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub175:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1400
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub175:
1:  movq _jh_destinations+1400(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub176:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1408
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub176:
1:  movq _jh_destinations+1408(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub177:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1416
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub177:
1:  movq _jh_destinations+1416(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub178:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1424
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub178:
1:  movq _jh_destinations+1424(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub179:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1432
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub179:
1:  movq _jh_destinations+1432(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub180:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1440
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub180:
1:  movq _jh_destinations+1440(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub181:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1448
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub181:
1:  movq _jh_destinations+1448(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub182:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1456
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub182:
1:  movq _jh_destinations+1456(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub183:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1464
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub183:
1:  movq _jh_destinations+1464(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub184:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1472
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub184:
1:  movq _jh_destinations+1472(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub185:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1480
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub185:
1:  movq _jh_destinations+1480(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub186:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1488
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub186:
1:  movq _jh_destinations+1488(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub187:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1496
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub187:
1:  movq _jh_destinations+1496(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub188:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1504
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub188:
1:  movq _jh_destinations+1504(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub189:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1512
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub189:
1:  movq _jh_destinations+1512(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub190:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1520
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub190:
1:  movq _jh_destinations+1520(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub191:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1528
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub191:
1:  movq _jh_destinations+1528(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub192:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1536
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub192:
1:  movq _jh_destinations+1536(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub193:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1544
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub193:
1:  movq _jh_destinations+1544(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub194:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1552
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub194:
1:  movq _jh_destinations+1552(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub195:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1560
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub195:
1:  movq _jh_destinations+1560(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub196:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1568
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub196:
1:  movq _jh_destinations+1568(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub197:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1576
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub197:
1:  movq _jh_destinations+1576(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub198:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1584
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub198:
1:  movq _jh_destinations+1584(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub199:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1592
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub199:
1:  movq _jh_destinations+1592(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub200:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1600
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub200:
1:  movq _jh_destinations+1600(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub201:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1608
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub201:
1:  movq _jh_destinations+1608(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub202:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1616
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub202:
1:  movq _jh_destinations+1616(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub203:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1624
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub203:
1:  movq _jh_destinations+1624(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub204:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1632
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub204:
1:  movq _jh_destinations+1632(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub205:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1640
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub205:
1:  movq _jh_destinations+1640(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub206:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1648
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub206:
1:  movq _jh_destinations+1648(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub207:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1656
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub207:
1:  movq _jh_destinations+1656(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub208:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1664
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub208:
1:  movq _jh_destinations+1664(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub209:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1672
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub209:
1:  movq _jh_destinations+1672(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub210:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1680
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub210:
1:  movq _jh_destinations+1680(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub211:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1688
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub211:
1:  movq _jh_destinations+1688(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub212:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1696
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub212:
1:  movq _jh_destinations+1696(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub213:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1704
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub213:
1:  movq _jh_destinations+1704(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub214:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1712
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub214:
1:  movq _jh_destinations+1712(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub215:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1720
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub215:
1:  movq _jh_destinations+1720(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub216:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1728
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub216:
1:  movq _jh_destinations+1728(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub217:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1736
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub217:
1:  movq _jh_destinations+1736(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub218:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1744
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub218:
1:  movq _jh_destinations+1744(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub219:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1752
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub219:
1:  movq _jh_destinations+1752(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub220:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1760
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub220:
1:  movq _jh_destinations+1760(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub221:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1768
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub221:
1:  movq _jh_destinations+1768(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub222:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1776
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub222:
1:  movq _jh_destinations+1776(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub223:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1784
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub223:
1:  movq _jh_destinations+1784(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub224:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1792
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub224:
1:  movq _jh_destinations+1792(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub225:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1800
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub225:
1:  movq _jh_destinations+1800(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub226:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1808
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub226:
1:  movq _jh_destinations+1808(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub227:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1816
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub227:
1:  movq _jh_destinations+1816(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub228:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1824
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub228:
1:  movq _jh_destinations+1824(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub229:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1832
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub229:
1:  movq _jh_destinations+1832(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub230:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1840
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub230:
1:  movq _jh_destinations+1840(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub231:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1848
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub231:
1:  movq _jh_destinations+1848(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub232:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1856
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub232:
1:  movq _jh_destinations+1856(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub233:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1864
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub233:
1:  movq _jh_destinations+1864(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub234:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1872
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub234:
1:  movq _jh_destinations+1872(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub235:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1880
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub235:
1:  movq _jh_destinations+1880(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub236:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1888
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub236:
1:  movq _jh_destinations+1888(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub237:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1896
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub237:
1:  movq _jh_destinations+1896(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub238:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1904
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub238:
1:  movq _jh_destinations+1904(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub239:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1912
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub239:
1:  movq _jh_destinations+1912(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub240:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1920
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub240:
1:  movq _jh_destinations+1920(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub241:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1928
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub241:
1:  movq _jh_destinations+1928(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub242:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1936
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub242:
1:  movq _jh_destinations+1936(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub243:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1944
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub243:
1:  movq _jh_destinations+1944(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub244:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1952
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub244:
1:  movq _jh_destinations+1952(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub245:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1960
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub245:
1:  movq _jh_destinations+1960(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub246:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1968
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub246:
1:  movq _jh_destinations+1968(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub247:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1976
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub247:
1:  movq _jh_destinations+1976(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub248:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1984
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub248:
1:  movq _jh_destinations+1984(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub249:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #1992
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub249:
1:  movq _jh_destinations+1992(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub250:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2000
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub250:
1:  movq _jh_destinations+2000(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub251:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2008
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub251:
1:  movq _jh_destinations+2008(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub252:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2016
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub252:
1:  movq _jh_destinations+2016(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub253:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2024
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub253:
1:  movq _jh_destinations+2024(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub254:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2032
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub254:
1:  movq _jh_destinations+2032(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
#if defined(__aarch64__)
.p2align 2
_jh_stub255:
    adrp x17, _jh_destinations@PAGE
    add x17, x17, _jh_destinations@PAGEOFF
    add x17, x17, #2040
1:  ldar x16, [x17]
    cbz x16, 1b
    br x16
#else
.p2align 4
_jh_stub255:
1:  movq _jh_destinations+2040(%rip), %r11
    testq %r11, %r11
    jz 1b
    jmp *%r11
#endif
.data
.p2align 3
.globl _jh_dispatch_stubs
_jh_dispatch_stubs:
    .quad _jh_stub0
    .quad _jh_stub1
    .quad _jh_stub2
    .quad _jh_stub3
    .quad _jh_stub4
    .quad _jh_stub5
    .quad _jh_stub6
    .quad _jh_stub7
    .quad _jh_stub8
    .quad _jh_stub9
    .quad _jh_stub10
    .quad _jh_stub11
    .quad _jh_stub12
    .quad _jh_stub13
    .quad _jh_stub14
    .quad _jh_stub15
    .quad _jh_stub16
    .quad _jh_stub17
    .quad _jh_stub18
    .quad _jh_stub19
    .quad _jh_stub20
    .quad _jh_stub21
    .quad _jh_stub22
    .quad _jh_stub23
    .quad _jh_stub24
    .quad _jh_stub25
    .quad _jh_stub26
    .quad _jh_stub27
    .quad _jh_stub28
    .quad _jh_stub29
    .quad _jh_stub30
    .quad _jh_stub31
    .quad _jh_stub32
    .quad _jh_stub33
    .quad _jh_stub34
    .quad _jh_stub35
    .quad _jh_stub36
    .quad _jh_stub37
    .quad _jh_stub38
    .quad _jh_stub39
    .quad _jh_stub40
    .quad _jh_stub41
    .quad _jh_stub42
    .quad _jh_stub43
    .quad _jh_stub44
    .quad _jh_stub45
    .quad _jh_stub46
    .quad _jh_stub47
    .quad _jh_stub48
    .quad _jh_stub49
    .quad _jh_stub50
    .quad _jh_stub51
    .quad _jh_stub52
    .quad _jh_stub53
    .quad _jh_stub54
    .quad _jh_stub55
    .quad _jh_stub56
    .quad _jh_stub57
    .quad _jh_stub58
    .quad _jh_stub59
    .quad _jh_stub60
    .quad _jh_stub61
    .quad _jh_stub62
    .quad _jh_stub63
    .quad _jh_stub64
    .quad _jh_stub65
    .quad _jh_stub66
    .quad _jh_stub67
    .quad _jh_stub68
    .quad _jh_stub69
    .quad _jh_stub70
    .quad _jh_stub71
    .quad _jh_stub72
    .quad _jh_stub73
    .quad _jh_stub74
    .quad _jh_stub75
    .quad _jh_stub76
    .quad _jh_stub77
    .quad _jh_stub78
    .quad _jh_stub79
    .quad _jh_stub80
    .quad _jh_stub81
    .quad _jh_stub82
    .quad _jh_stub83
    .quad _jh_stub84
    .quad _jh_stub85
    .quad _jh_stub86
    .quad _jh_stub87
    .quad _jh_stub88
    .quad _jh_stub89
    .quad _jh_stub90
    .quad _jh_stub91
    .quad _jh_stub92
    .quad _jh_stub93
    .quad _jh_stub94
    .quad _jh_stub95
    .quad _jh_stub96
    .quad _jh_stub97
    .quad _jh_stub98
    .quad _jh_stub99
    .quad _jh_stub100
    .quad _jh_stub101
    .quad _jh_stub102
    .quad _jh_stub103
    .quad _jh_stub104
    .quad _jh_stub105
    .quad _jh_stub106
    .quad _jh_stub107
    .quad _jh_stub108
    .quad _jh_stub109
    .quad _jh_stub110
    .quad _jh_stub111
    .quad _jh_stub112
    .quad _jh_stub113
    .quad _jh_stub114
    .quad _jh_stub115
    .quad _jh_stub116
    .quad _jh_stub117
    .quad _jh_stub118
    .quad _jh_stub119
    .quad _jh_stub120
    .quad _jh_stub121
    .quad _jh_stub122
    .quad _jh_stub123
    .quad _jh_stub124
    .quad _jh_stub125
    .quad _jh_stub126
    .quad _jh_stub127
    .quad _jh_stub128
    .quad _jh_stub129
    .quad _jh_stub130
    .quad _jh_stub131
    .quad _jh_stub132
    .quad _jh_stub133
    .quad _jh_stub134
    .quad _jh_stub135
    .quad _jh_stub136
    .quad _jh_stub137
    .quad _jh_stub138
    .quad _jh_stub139
    .quad _jh_stub140
    .quad _jh_stub141
    .quad _jh_stub142
    .quad _jh_stub143
    .quad _jh_stub144
    .quad _jh_stub145
    .quad _jh_stub146
    .quad _jh_stub147
    .quad _jh_stub148
    .quad _jh_stub149
    .quad _jh_stub150
    .quad _jh_stub151
    .quad _jh_stub152
    .quad _jh_stub153
    .quad _jh_stub154
    .quad _jh_stub155
    .quad _jh_stub156
    .quad _jh_stub157
    .quad _jh_stub158
    .quad _jh_stub159
    .quad _jh_stub160
    .quad _jh_stub161
    .quad _jh_stub162
    .quad _jh_stub163
    .quad _jh_stub164
    .quad _jh_stub165
    .quad _jh_stub166
    .quad _jh_stub167
    .quad _jh_stub168
    .quad _jh_stub169
    .quad _jh_stub170
    .quad _jh_stub171
    .quad _jh_stub172
    .quad _jh_stub173
    .quad _jh_stub174
    .quad _jh_stub175
    .quad _jh_stub176
    .quad _jh_stub177
    .quad _jh_stub178
    .quad _jh_stub179
    .quad _jh_stub180
    .quad _jh_stub181
    .quad _jh_stub182
    .quad _jh_stub183
    .quad _jh_stub184
    .quad _jh_stub185
    .quad _jh_stub186
    .quad _jh_stub187
    .quad _jh_stub188
    .quad _jh_stub189
    .quad _jh_stub190
    .quad _jh_stub191
    .quad _jh_stub192
    .quad _jh_stub193
    .quad _jh_stub194
    .quad _jh_stub195
    .quad _jh_stub196
    .quad _jh_stub197
    .quad _jh_stub198
    .quad _jh_stub199
    .quad _jh_stub200
    .quad _jh_stub201
    .quad _jh_stub202
    .quad _jh_stub203
    .quad _jh_stub204
    .quad _jh_stub205
    .quad _jh_stub206
    .quad _jh_stub207
    .quad _jh_stub208
    .quad _jh_stub209
    .quad _jh_stub210
    .quad _jh_stub211
    .quad _jh_stub212
    .quad _jh_stub213
    .quad _jh_stub214
    .quad _jh_stub215
    .quad _jh_stub216
    .quad _jh_stub217
    .quad _jh_stub218
    .quad _jh_stub219
    .quad _jh_stub220
    .quad _jh_stub221
    .quad _jh_stub222
    .quad _jh_stub223
    .quad _jh_stub224
    .quad _jh_stub225
    .quad _jh_stub226
    .quad _jh_stub227
    .quad _jh_stub228
    .quad _jh_stub229
    .quad _jh_stub230
    .quad _jh_stub231
    .quad _jh_stub232
    .quad _jh_stub233
    .quad _jh_stub234
    .quad _jh_stub235
    .quad _jh_stub236
    .quad _jh_stub237
    .quad _jh_stub238
    .quad _jh_stub239
    .quad _jh_stub240
    .quad _jh_stub241
    .quad _jh_stub242
    .quad _jh_stub243
    .quad _jh_stub244
    .quad _jh_stub245
    .quad _jh_stub246
    .quad _jh_stub247
    .quad _jh_stub248
    .quad _jh_stub249
    .quad _jh_stub250
    .quad _jh_stub251
    .quad _jh_stub252
    .quad _jh_stub253
    .quad _jh_stub254
    .quad _jh_stub255
