; 所有呼出し内だけでx87制御語を適用する、Windows x64用の演算部品です。
.code
GetControlState PROC
    sub rsp, 24
    fnstcw word ptr [rsp]
    stmxcsr dword ptr [rsp+8]
    mov eax, dword ptr [rsp+8]
    shl rax, 32
    movzx edx, word ptr [rsp]
    or rax, rdx
    add rsp, 24
    ret
GetControlState ENDP
SaveEnvironment PROC
    ; REX.Wで命令・データポインターも64bitの形式にします。
    db 048h
    fxsave [rcx]
    ret
SaveEnvironment ENDP
; 保存済みの呼出し元を使わず、専用の空スタックで演算を始めます。
InitializeEnvironment PROC
    fninit
    ret
InitializeEnvironment ENDP
RestoreEnvironment PROC
    db 048h
    fxrstor [rcx]
    ret
RestoreEnvironment ENDP
ApplyDispatchControl PROC
    ; x64の呼出し元が確保した引数領域を使います。
    mov word ptr [rsp+8], cx
    fninit
    fldcw word ptr [rsp+8]
    ret
ApplyDispatchControl ENDP
ClearPendingX87 PROC
    fnclex
    ret
ClearPendingX87 ENDP
WaitForX87 PROC
    fwait
    ret
WaitForX87 ENDP
GetX87Status PROC
    xor eax, eax
    fnstsw ax
    ret
GetX87Status ENDP
EvaluateBinaryX87 PROC FRAME
    sub rsp, 56
    .allocstack 56
    .endprolog
    fnstcw word ptr [rsp+8]
    mov word ptr [rsp+10], cx
    movsd qword ptr [rsp+16], xmm2
    movsd qword ptr [rsp+24], xmm3
    fnclex
    fldcw word ptr [rsp+10]
    fld qword ptr [rsp+16]
    cmp edx, 1
    je SubtractValue
    cmp edx, 2
    je MultiplyValue
    cmp edx, 3
    je DivideValue
    fadd qword ptr [rsp+24]
    jmp StoreBinary
SubtractValue:
    fsub qword ptr [rsp+24]
    jmp StoreBinary
MultiplyValue:
    fmul qword ptr [rsp+24]
    jmp StoreBinary
DivideValue:
    fdiv qword ptr [rsp+24]
StoreBinary:
    fstp qword ptr [rsp+32]
    fwait
    fnclex
    fldcw word ptr [rsp+8]
    movsd xmm0, qword ptr [rsp+32]
    add rsp, 56
    ret
EvaluateBinaryX87 ENDP
; 第2引数の0〜6はSQRT、SIN、COS、TAN、EXP、LN、ARCTANです。
; 7〜11はINT、ABS、LOGN、ARCSIN、ARCCOSです。
; 12〜14はCEIL、ZCUT、POWERです。
; 15〜20は角度縮約後の三角関数です。
EvaluateBuiltinX87 PROC FRAME
    sub rsp, 56
    .allocstack 56
    .endprolog
    fnstcw word ptr [rsp+8]
    mov word ptr [rsp+10], cx
    movsd qword ptr [rsp+16], xmm2
    fnclex
    fldcw word ptr [rsp+10]
    fld qword ptr [rsp+16]
    cmp edx, 15
    jae ReducedAngleValue
    cmp edx, 1
    je SineValue
    cmp edx, 2
    je CosineValue
    cmp edx, 3
    je TangentValue
    cmp edx, 4
    je ExponentialValue
    cmp edx, 5
    je LogarithmValue
    cmp edx, 6
    je ArcTangentValue
    cmp edx, 7
    je TruncateValue
    cmp edx, 8
    je AbsoluteValue
    cmp edx, 9
    je LogarithmRatioValue
    cmp edx, 12
    je CeilingValue
    cmp edx, 13
    je ZeroCutValue
    cmp edx, 14
    je PowerValue
    cmp edx, 10
    jae InverseTrigonometricValue
    fsqrt
    jmp StoreBuiltin
SineValue:
    fsin
    jmp StoreBuiltin
CosineValue:
    fcos
    jmp StoreBuiltin
TangentValue:
    fptan
    fstp st(0)
    jmp StoreBuiltin
ReducedAngleValue:
    movsd qword ptr [rsp+24], xmm3
    cmp edx, 16
    je ReducedNegativeSine
    fadd qword ptr [rsp+24]
    cmp edx, 15
    je SineValue
    cmp edx, 17
    je CosineValue
    cmp edx, 18
    je ReducedNegativeCosine
    cmp edx, 19
    je TangentValue
    ; 奇数象限のTANは、剰余のTANから負の逆数を求めます。
    fptan
    fchs
    fdivrp st(1), st(0)
    jmp StoreBuiltin
ReducedNegativeSine:
    fchs
    fsub qword ptr [rsp+24]
    jmp SineValue
ReducedNegativeCosine:
    fcos
    fchs
    jmp StoreBuiltin
ExponentialValue:
    fldl2e
    fmulp st(1), st(0)
Exp2Value:
    fld st(0)
    frndint
    fsub st(1), st(0)
    fxch st(1)
    f2xm1
    fld1
    faddp st(1), st(0)
    fscale
    fstp st(1)
    jmp StoreBuiltin
LogarithmValue:
    fldln2
    fxch st(1)
    fyl2x
    jmp StoreBuiltin
ArcTangentValue:
    fld1
    fpatan
    jmp StoreBuiltin
TruncateValue:
    ; INTは元の例外マスクを保ち、丸めを0方向、精度を拡張へ切り替えます。
    or word ptr [rsp+10], 0f00h
    fldcw word ptr [rsp+10]
    frndint
    jmp StoreBuiltin
AbsoluteValue:
    fabs
    jmp StoreBuiltin
LogarithmRatioValue:
    ; スタックにある底と第4引数の真数のlog2を求め、真数側を分子にします。
    movsd qword ptr [rsp+24], xmm3
    fld1
    fxch st(1)
    fyl2x
    fld1
    fld qword ptr [rsp+24]
    fyl2x
    fdivrp st(1), st(0)
    jmp StoreBuiltin
InverseTrigonometricValue:
    ; 原版と同じ順でsqrt((1+x)*(1-x))を求めます。
    fld1
    fadd st(0), st(1)
    fld1
    fsub st(0), st(2)
    fmulp st(1), st(0)
    fsqrt
    cmp edx, 10
    je InverseTrigonometricAngle
    ; 下方向丸めで平方根が負の0でも、ARCCOSは0〜πを返します。
    fabs
    fxch st(1)
InverseTrigonometricAngle:
    fpatan
    jmp StoreBuiltin
CeilingValue:
    ; CEILは丸めだけを正方向に切り替え、doubleの範囲を保ちます。
    and word ptr [rsp+10], 0f3ffh
    or word ptr [rsp+10], 0800h
    fldcw word ptr [rsp+10]
    frndint
    jmp StoreBuiltin
ZeroCutValue:
    ; 負数と符号付きゼロを正のゼロへそろえます。
    ftst
    fnstsw ax
    test ah, 4
    jnz StoreBuiltin
    test ah, 41h
    jz StoreBuiltin
    fstp st(0)
    fldz
    jmp StoreBuiltin
PowerValue:
    movsd qword ptr [rsp+24], xmm3
    mov rax, qword ptr [rsp+24]
    btr rax, 63
    mov r8, 43e0000000000000h
    cmp rax, r8
    jae PowerLargeIntegral
    fld qword ptr [rsp+24]
    fld st(0)
    frndint
    fucomi st(0), st(1)
    jne PowerFractional
    fistp qword ptr [rsp+40]
    fstp st(0)
    mov rax, qword ptr [rsp+40]
    test rax, rax
    jz PowerIdentity
    jns PowerIntegerInit
    ; 負指数は先に逆数を取り、途中の累乗が逆方向にあふれるのを防ぎます。
    neg rax
    fld1
    fdivrp st(1), st(0)
PowerIntegerInit:
    fld1
    fxch st(1)
PowerIntegerLoop:
    shr rax, 1
    jnc PowerNextBit
    fmul st(1), st(0)
PowerNextBit:
    test rax, rax
    jz PowerIntegerDone
    fmul st(0), st(0)
    jmp PowerIntegerLoop
PowerIntegerDone:
    fstp st(0)
    jmp StoreBuiltin
PowerIdentity:
    fstp st(0)
    fld1
    jmp StoreBuiltin
PowerLargeIntegral:
    ; 絶対値が2^63以上のdoubleの指数は偶数なので、底の絶対値で計算します。
    fabs
    fld qword ptr [rsp+24]
    jmp PowerLogarithm
PowerFractional:
    fstp st(0)
PowerLogarithm:
    fxch st(1)
    ftst
    fnstsw ax
    test ah, 40h
    jnz PowerZero
    fyl2x
    jmp Exp2Value
PowerZero:
    fstp st(0)
    fstp st(0)
    fldz
StoreBuiltin:
    fstp qword ptr [rsp+32]
    fwait
    fnclex
    fldcw word ptr [rsp+8]
    movsd xmm0, qword ptr [rsp+32]
    add rsp, 56
    ret
EvaluateBuiltinX87 ENDP
END
