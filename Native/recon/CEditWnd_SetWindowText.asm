[2K┌ method.CEditWnd.virtual_292(int32_t arg_4h);
│           ; var int32_t var_24h @ stack - 0x24
│           ; var int32_t var_20h @ stack - 0x20
│           ; var int32_t var_18h @ stack - 0x18
│           ; var int32_t var_14h @ stack - 0x14
│           ; var int32_t var_10h @ stack - 0x10
│           ; var int32_t var_ch @ stack - 0xc
│           ; var int32_t var_8h @ stack - 0x8
│           ; var int32_t var_4h @ stack - 0x4
│           ; arg int32_t arg_4h @ stack + 0x4
│           0x10097af0      mov   eax, dword fs:[0]
│           0x10097af6      push  0xffffffffffffffff
│           0x10097af8      push  0x100f530e
│           0x10097afd      push  eax
│           0x10097afe      mov   dword fs:[0], esp
│           0x10097b05      sub   esp, 8
│           0x10097b08      push  ebx
│           0x10097b09      push  esi
│           0x10097b0a      mov   esi, ecx
│           0x10097b0c      mov   ecx, dword [arg_4h]
│           0x10097b10      mov   eax, dword [ecx]
│           0x10097b12      test  eax, eax
│       ┌─< 0x10097b14      je    0x10097b2a
│       │   0x10097b16      mov   dword [var_18h], eax
│       │   0x10097b1a      xor   ebx, ebx
│       │   0x10097b1c      mov   eax, dword [var_14h]
│       │   0x10097b20      lock  inc dword [eax]
│       │   0x10097b23      sete  bl
│       │   0x10097b26      mov   dword [var_10h], ebx
│       └─> 0x10097b2a      mov   ecx, dword [ecx]
│           0x10097b2c      mov   dword [arg_4h], ecx
│           0x10097b30      cmp   dword [data.10341b04], 0             ; [0x10341b04:4]=0
│           0x10097b37      mov   dword [var_4h], 0
│       ┌─< 0x10097b3f      je    0x10097b50
│       │   0x10097b41      push  1                                    ; 1
│       │   0x10097b43      lea   ecx, [arg_4h]
│       │   0x10097b47      call  fcn.100478f0                         ; fcn.100478f0
│       │   0x10097b4c      mov   ecx, dword [var_4h + 0x4]
│       └─> 0x10097b50      mov   eax, dword [esi + 0x1e4]
│           0x10097b56      test  eax, eax
│       ┌─< 0x10097b58      jle   0x10097ba3
│       │   0x10097b5a      test  ecx, ecx
│      ┌──< 0x10097b5c      je    0x10097b63
│      ││   0x10097b5e      mov   ecx, dword [ecx + 8]
│     ┌───< 0x10097b61      jmp   0x10097b65
│     │└──> 0x10097b63      xor   ecx, ecx
│     │ │   ; CODE XREF from method.CEditWnd.virtual_292 @ 0x10097b61
│     └───> 0x10097b65      cmp   ecx, eax
│      ┌──< 0x10097b67      jle   0x10097ba3
│      ││   0x10097b69      push  eax
│      ││   0x10097b6a      lea   eax, [var_14h]
│      ││   0x10097b6e      push  eax
│      ││   0x10097b6f      lea   ecx, [arg_4h]
│      ││   0x10097b73      call  fcn.10001040                         ; fcn.10001040
│      ││   0x10097b78      push  eax
│      ││   0x10097b79      lea   ecx, [var_4h]
│      ││   0x10097b7d      mov   byte [var_ch], 1
│      ││   0x10097b82      call  fcn.10047590                         ; fcn.10047590
│      ││   0x10097b87      mov   eax, dword [var_20h]
│      ││   0x10097b8b      xor   ebx, ebx
│      ││   0x10097b8d      mov   byte [var_10h], bl
│      ││   0x10097b91      cmp   eax, ebx
│     ┌───< 0x10097b93      je    0x10097b9f
│     │││   0x10097b95      push  eax
│     │││   0x10097b96      lea   ecx, [var_20h]
│     │││   0x10097b9a      call  fcn.100472d0                         ; fcn.100472d0
│     └───> 0x10097b9f      mov   dword [var_20h], ebx
│      └└─> 0x10097ba3      lea   ecx, [arg_4h]
│           0x10097ba7      push  ecx
│           0x10097ba8      lea   ecx, [esi + 0x1ec]
│           0x10097bae      call  fcn.10047590                         ; fcn.10047590
│           0x10097bb3      mov   ecx, esi
│           0x10097bb5      call  fcn.10096560                         ; fcn.10096560
│           0x10097bba      mov   ecx, esi
│           0x10097bbc      call  fcn.10096f70                         ; fcn.10096f70
│           0x10097bc1      mov   eax, dword [esi + 0x1a8]
│           0x10097bc7      test  eax, eax
│       ┌─< 0x10097bc9      je    0x10097bd0
│       │   0x10097bcb      mov   eax, dword [eax + 8]
│      ┌──< 0x10097bce      jmp   0x10097bd2
│      │└─> 0x10097bd0      xor   eax, eax
│      │    ; CODE XREF from method.CEditWnd.virtual_292 @ 0x10097bce
│      └──> 0x10097bd2      mov   dword [esi + 0x1e0], eax
│           0x10097bd8      mov   dword [esi + 0x1dc], eax
│           0x10097bde      mov   eax, dword [var_4h + 0x4]
│           0x10097be2      pop   esi
│           0x10097be3      mov   dword [var_8h], 0xffffffff           ; [0xffffffff:4]=-1 ; -1
│           0x10097beb      pop   ebx
│           0x10097bec      test  eax, eax
│       ┌─< 0x10097bee      je    0x10097bfa
│       │   0x10097bf0      push  eax
│       │   0x10097bf1      lea   ecx, [var_4h + 0x4]
│       │   0x10097bf5      call  fcn.100472d0                         ; fcn.100472d0
│       └─> 0x10097bfa      mov   ecx, dword [var_10h]
│           0x10097bfe      mov   dword fs:[0], ecx
│           0x10097c05      add   esp, 0x14
└           0x10097c08      ret   4
