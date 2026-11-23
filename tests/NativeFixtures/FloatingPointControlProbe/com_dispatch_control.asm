.code
RaiseDivide PROC
    fld1
    fldz
    fdivp st(1), st(0)
    fwait
    ret
RaiseDivide ENDP
; マスク済みゼロ除算の状態とx87スタックの値を呼出し元に残します。
SeedCallerStack PROC
    fld1
    fldz
    fdivp st(1), st(0)
    fwait
    ret
SeedCallerStack ENDP

; 検証用コールバックの前後で、呼出し元が指定したx87制御語を設定します。
ApplyFixtureX87Control PROC
    mov word ptr [rsp+8], cx
    fldcw word ptr [rsp+8]
    ret
ApplyFixtureX87Control ENDP
END
