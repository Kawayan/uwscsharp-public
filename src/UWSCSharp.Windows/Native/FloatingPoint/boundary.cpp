#include <windows.h>
#include <oaidl.h>
#include <cstdint>
#include <cstring>

extern "C" double EvaluateBinaryX87(unsigned int control, unsigned int operation,
    double left, double right);
extern "C" double EvaluateBuiltinX87(unsigned int control, unsigned int operation,
    double left, double right);
extern "C" void SaveEnvironment(void* saved);
extern "C" void InitializeEnvironment();
extern "C" void RestoreEnvironment(void* saved);
extern "C" void ApplyDispatchControl(unsigned short control);
extern "C" void ClearPendingX87();
extern "C" void WaitForX87();

static int DispatchFaultFilter(unsigned int code)
{
    if (code < EXCEPTION_FLT_DENORMAL_OPERAND || code > EXCEPTION_FLT_UNDERFLOW)
    {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    ClearPendingX87();
    return EXCEPTION_EXECUTE_HANDLER;
}

extern "C" unsigned int InvokeDispatchWithControl(IDispatch* dispatch,
    DISPID id, REFIID iid, LCID locale, WORD flags, DISPPARAMS* parameters,
    VARIANT* result, EXCEPINFO* exception, UINT* argumentError,
    unsigned short control, HRESULT* status)
{
    alignas(16) unsigned char saved[512];
    unsigned int fault = 0;
    *status = E_FAIL;
    SaveEnvironment(saved);
    __try
    {
        __try
        {
            ApplyDispatchControl(control);
            *status = dispatch->Invoke(id, iid, locale, flags, parameters,
                result, exception, argumentError);
            WaitForX87();
        }
        __except (DispatchFaultFilter(GetExceptionCode()))
        {
            fault = GetExceptionCode();
        }
    }
    __finally
    {
        // COM側で生じた状態を呼出し元へ持ち越しません。
        RestoreEnvironment(saved);
    }
    return fault;
}

extern "C" unsigned int GetFloatingPointAbiVersion()
{
    // 管理側の呼出し規約や演算番号と互換性がなくなる変更で更新します。
    return 1;
}

// 所有演算内で発生する浮動小数点例外だけを捕捉します。
static int Filter(unsigned int code, EXCEPTION_POINTERS* fault, unsigned int* state)
{
    *state = fault->ContextRecord->FltSave.ControlWord
        | (static_cast<unsigned int>(fault->ContextRecord->FltSave.StatusWord) << 16);
    return code >= EXCEPTION_FLT_DENORMAL_OPERAND
        && code <= EXCEPTION_FLT_UNDERFLOW
        ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH;
}

using NativeOperation = double (*)(unsigned int, unsigned int, double, double);

extern "C" unsigned int EvaluateProtected(unsigned int control,
    unsigned int operation, double left, double right,
    double* value, unsigned int* faultState, NativeOperation execute)
{
    __declspec(align(16)) unsigned char saved[512];
    unsigned int code = 0;
    *value = 0;
    *faultState = 0;
    SaveEnvironment(saved);
    __try
    {
        __try
        {
            InitializeEnvironment();
            *value = execute(control, operation, left, right);
        }
        __except (Filter(GetExceptionCode(), GetExceptionInformation(), faultState))
        {
            code = GetExceptionCode();
        }
    }
    __finally
    {
        // 例外途中のx87スタックと状態フラグも元に戻します。
        RestoreEnvironment(saved);
    }
    return code;
}

extern "C" unsigned int EvaluateBinarySafely(unsigned int control,
    unsigned int operation, double left, double right,
    double* value, unsigned int* faultState)
{
    // 命令へ渡す前に、CWの幅と加減乗除の指定を検査します。
    constexpr unsigned int InvalidInput = 0xc000000d;
    if (control > 0xffff || operation > 3)
    {
        *value = 0;
        *faultState = 0;
        return InvalidInput;
    }
    return EvaluateProtected(control, operation, left, right,
        value, faultState, EvaluateBinaryX87);
}

extern "C" unsigned int EvaluateBuiltinSafely(unsigned int control,
    unsigned int operation, double left, double right,
    double* value, unsigned int* faultState)
{
    // 三角命令の範囲外ではC2だけが立ち、引数が結果として残るため拒否します。
    constexpr double TrigonometricLimit = 9223372036854775808.0;
    constexpr unsigned int InvalidInput = 0xc000000d;
    // 状態保存前に非正規化数やNaNを浮動小数点比較しないよう、ビットで比較します。
    constexpr std::uint64_t Magnitude = 0x7fffffffffffffff;
    constexpr std::uint64_t ReducedLimit = 0x3fe921fb54442d18; // π/4
    constexpr std::uint64_t TailLimit = 0x3ca0000000000000; // 2^-53
    std::uint64_t leftBits;
    std::uint64_t rightBits;
    std::memcpy(&leftBits, &left, sizeof(leftBits));
    std::memcpy(&rightBits, &right, sizeof(rightBits));
    if (control > 0xffff || operation > 20
        || (operation >= 1 && operation <= 3
            && (left <= -TrigonometricLimit || left >= TrigonometricLimit))
        || (operation >= 15
            && ((leftBits & Magnitude) > ReducedLimit
                || (rightBits & Magnitude) > TailLimit)))
    {
        *value = 0;
        *faultState = 0;
        return InvalidInput;
    }
    return EvaluateProtected(control, operation, left, right,
        value, faultState, EvaluateBuiltinX87);
}
