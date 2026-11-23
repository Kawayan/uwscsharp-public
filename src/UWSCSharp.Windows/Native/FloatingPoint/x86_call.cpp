#include <windows.h>
#include <cstdint>

enum class ReturnKind : unsigned int
{
    Integer = 0, Single = 1, Double = 2
};

// スタックの内容は、呼出し先の引数を先頭から並べたDWORD列です。
static void Invoke(void* target, const std::uint32_t* words,
    unsigned int count, ReturnKind kind, std::uint64_t* result)
{
    __asm
    {
        mov ebx, esp
        mov ecx, count
        mov esi, words
        test ecx, ecx
        jz invoke_target
    push_argument:
        push dword ptr [esi + ecx * 4 - 4]
        dec ecx
        jnz push_argument
    invoke_target:
        call dword ptr [target]
        mov esp, ebx
        mov ecx, result
        cmp kind, 1
        je return_single
        cmp kind, 2
        je return_double
        mov [ecx], eax
        mov [ecx + 4], edx
        jmp done
    return_single:
        fstp dword ptr [ecx]
        jmp done
    return_double:
        fstp qword ptr [ecx]
    done:
        fwait
    }
}

// 浮動小数点例外は中継の呼出しエラーに変換します。
static int Filter(unsigned int code)
{
    if (code < EXCEPTION_FLT_DENORMAL_OPERAND || code > EXCEPTION_FLT_UNDERFLOW)
    {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // 例外処理中に保留中のx87例外を再発させないようにします。
    __asm fnclex
    return EXCEPTION_EXECUTE_HANDLER;
}

static unsigned int InvokePreservingState(
    void* target, const std::uint32_t* words, unsigned int count,
    ReturnKind kind, bool applyControl, std::uint16_t control, std::uint64_t* result,
    std::uint16_t* returnedControl = nullptr)
{
    if (target == nullptr || result == nullptr || count > 512
        || (count != 0 && words == nullptr) || kind > ReturnKind::Double)
    {
        return ERROR_INVALID_PARAMETER;
    }

    alignas(16) unsigned char saved[512];
    unsigned int error = 0;
    *result = 0;
    __asm fxsave saved
    __try
    {
        __try
        {
            if (applyControl)
            {
                __asm
                {
                    fninit
                    fldcw control
                }
            }
            Invoke(target, words, count, kind, result);
            if (returnedControl != nullptr)
            {
                std::uint16_t captured = 0;
                __asm fnstcw captured
                *returnedControl = captured;
            }
        }
        __except (Filter(GetExceptionCode()))
        {
            error = GetExceptionCode();
        }
    }
    __finally
    {
        // 呼出し先で変わった制御語、状態フラグ、レジスタを残しません。
        __asm fxrstor saved
    }

    return error;
}

extern "C" __declspec(dllexport) unsigned int __cdecl InvokeControlled(
    void* target, const std::uint32_t* words, unsigned int count,
    ReturnKind kind, std::uint16_t control, std::uint64_t* result)
{
    return InvokePreservingState(target, words, count, kind, true, control, result);
}

extern "C" __declspec(dllexport) unsigned int __cdecl InvokeCapturingControl(
    void* target, const std::uint32_t* words, unsigned int count,
    ReturnKind kind, int applyControl, std::uint16_t control, std::uint64_t* result,
    std::uint16_t* returnedControl)
{
    return InvokePreservingState(target, words, count, kind, applyControl != 0,
        control, result, returnedControl);
}

extern "C" __declspec(dllexport) unsigned int __cdecl InvokeCurrent(
    void* target, const std::uint32_t* words, unsigned int count,
    ReturnKind kind, std::uint64_t* result)
{
    return InvokePreservingState(target, words, count, kind, false, 0, result);
}
