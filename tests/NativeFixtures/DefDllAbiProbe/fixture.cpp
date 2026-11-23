#include <cstdint>
#include <cstddef>
#include <intrin.h>
#include <cstdio>
#include <cstring>
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

// 所有する検証用DLL。外部画面・入力・ファイルを操作しません。
#define EXPORT extern "C" __declspec(dllexport)

struct Natural
{
    std::uint8_t tag;
    double value;
    std::uint32_t count;
};

#pragma pack(push, 1)
struct Packed
{
    std::uint8_t tag;
    double value;
    std::uint32_t count;
};
#pragma pack(pop)

static_assert(sizeof(int) == 4 && sizeof(long) == 4);
static_assert(sizeof(Natural) == 24 && offsetof(Natural, value) == 8);
static_assert(sizeof(Packed) == 13 && offsetof(Packed, value) == 1);

// 呼出し先に見えるx87制御語を、設定を変更せずに読みます。
EXPORT std::uint32_t ReadX87Control()
{
#if defined(_M_IX86)
    std::uint16_t control = 0;
    __asm fnstcw control
    return control;
#else
    alignas(16) unsigned char state[512];
    _fxsave(state);
    return static_cast<std::uint32_t>(state[0])
        | (static_cast<std::uint32_t>(state[1]) << 8);
#endif
}

#if defined(_M_X64)
extern "C" void ApplyFixtureX87Control(std::uint16_t control);
#endif

// 通知を発火する間だけ制御語を適用し、呼出し元の設定に戻します。
EXPORT int InvokeWithX87Control(
    std::uint16_t control, int (__cdecl* callback)())
{
    std::uint16_t saved = static_cast<std::uint16_t>(ReadX87Control());
    __try
    {
#if defined(_M_IX86)
        __asm fldcw control
#else
        ApplyFixtureX87Control(control);
#endif
        return callback();
    }
    __finally
    {
#if defined(_M_IX86)
        __asm fldcw saved
#else
        ApplyFixtureX87Control(saved);
#endif
    }
}

// SSEの制御ビットを読み、演算で変わる状態フラグを除きます。
EXPORT std::uint32_t ReadSseControl()
{
    return _mm_getcsr() & 0xffc0u;
}

// x87のゼロ除算を発生させ、例外を捕捉した呼出し元からVAR書戻しを観測します。
EXPORT int __stdcall ProbeX87Divide(int* marker)
{
#if defined(_M_IX86)
    *marker = 81;
    __asm
    {
        fld1
        fldz
        fdivp st(1), st(0)
        fstp st(0)
        fwait
    }

    *marker = 82;
    return 31;
#else
    (void)marker;
    return -1;
#endif
}

// x87で負数の平方根を計算し、無効演算時のVAR書戻しを観測します。
EXPORT int __stdcall ProbeX87InvalidOperation(int* marker)
{
#if defined(_M_IX86)
    *marker = 81;
    __asm
    {
        fld1
        fchs
        fsqrt
        fstp st(0)
        fwait
    }

    *marker = 82;
    return 31;
#else
    (void)marker;
    return -1;
#endif
}

static volatile LONG x87Arrivals = 0;

// 2本の呼出しが重なるまで待ち、前後の制御語を記録します。
EXPORT int __stdcall ObserveConcurrentX87(int* entered, int* released)
{
    *entered = static_cast<int>(ReadX87Control());
    InterlockedIncrement(&x87Arrivals);
    const ULONGLONG deadline = GetTickCount64() + 3000;
    while (InterlockedCompareExchange(&x87Arrivals, 0, 0) < 2)
    {
        if (GetTickCount64() >= deadline)
        {
            return -1;
        }
        Sleep(1);
    }

    *released = static_cast<int>(ReadX87Control());
    return 1;
}

EXPORT std::uint32_t EchoUnsigned(std::uint32_t value) { return value; }
EXPORT std::int32_t EchoSigned(std::int32_t value) { return value; }
EXPORT std::uint8_t EchoByte(std::uint8_t value) { return value; }
EXPORT std::uint16_t EchoWord(std::uint16_t value) { return value; }
EXPORT std::int64_t EchoLongLong(std::int64_t value) { return value; }
EXPORT std::uintptr_t EchoPointer(std::uintptr_t value) { return value; }
EXPORT int __stdcall ObserveHandlePair(std::uintptr_t* first, std::uintptr_t* second)
{
    *first += 10;
    *second += 20;
    return 1;
}

EXPORT int __stdcall ObserveHandleArray(std::uintptr_t* values)
{
    return ObserveHandlePair(values, values + 1);
}
EXPORT double Mixed(int a, double b, int c, float d, int e, double f)
{
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f;
}
EXPORT float FloatResult(float value) { return value + 0.5f; }
EXPORT void WriteReferences(int* integer, double* number)
{
    *integer = -123456789;
    *number = 19.25;
}
#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:InspectExtendedArgument=_InspectExtendedArgument@16")
#pragma comment(linker, "/EXPORT:ReturnExtendedProbe=_ReturnExtendedProbe@0")
#pragma comment(linker, "/EXPORT:ReturnExtendedIntegerProbe=_ReturnExtendedIntegerProbe@4")
#pragma comment(linker, "/EXPORT:ReadExtendedIntegerParity=_ReadExtendedIntegerParity@8")
#pragma comment(linker, "/EXPORT:InspectExtendedIntegerProbe=_InspectExtendedIntegerProbe@16")
#pragma comment(linker, "/EXPORT:UpdateExtendedReference=_UpdateExtendedReference@8")
#pragma comment(linker, "/EXPORT:WriteExtendedMidpoint=_WriteExtendedMidpoint@8")
#pragma comment(linker, "/EXPORT:WriteExtendedMidpointWithRounding=_WriteExtendedMidpointWithRounding@12")
#pragma comment(linker, "/EXPORT:WriteExtendedBeforeDivision=_WriteExtendedBeforeDivision@8")
#pragma comment(linker, "/EXPORT:WriteExtendedFieldsBeforeDivision=_WriteExtendedFieldsBeforeDivision@8")
#pragma comment(linker, "/EXPORT:WriteExtendedDoubleBoundary=_WriteExtendedDoubleBoundary@8")
#pragma comment(linker, "/EXPORT:WriteMixedExtendedArray=_WriteMixedExtendedArray@12")
#pragma comment(linker, "/EXPORT:WriteMixedExtendedArrayField=_WriteMixedExtendedArrayField@4")
#pragma comment(linker, "/EXPORT:WriteMixedExtendedReferenceFields=_WriteMixedExtendedReferenceFields@4")
#pragma comment(linker, "/EXPORT:ObserveExtendedReferenceInput=_ObserveExtendedReferenceInput@4")
#pragma comment(linker, "/EXPORT:ObserveExtendedPair=_ObserveExtendedPair@8")
#pragma comment(linker, "/EXPORT:ObserveExtendedTriple=_ObserveExtendedTriple@8")
#pragma comment(linker, "/EXPORT:ObserveExtendedValueInput=_ObserveExtendedValueInput@12")
#pragma comment(linker, "/EXPORT:UpdateExtendedArray=_UpdateExtendedArray@8")
#pragma comment(linker, "/EXPORT:UpdateExtendedArrayPair=_UpdateExtendedArrayPair@4")
#pragma comment(linker, "/EXPORT:UpdateAliasedExtendedArrays=_UpdateAliasedExtendedArrays@8")
#pragma comment(linker, "/EXPORT:UpdateExtendedArrayField=_UpdateExtendedArrayField@12")
#pragma comment(linker, "/EXPORT:UpdateExtendedField=_UpdateExtendedField@12")
#pragma comment(linker, "/EXPORT:ExtendedReferenceCalls=_ExtendedReferenceCalls@0")

static const unsigned char extendedProbeResult[10] =
    { 1, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f };
static volatile LONG extendedReferenceCalls = 0;

EXPORT int __stdcall ExtendedReferenceCalls()
{
    return InterlockedCompareExchange(&extendedReferenceCalls, 0, 0);
}

EXPORT int __stdcall UpdateExtendedReference(void* pointer, int sign)
{
    InterlockedIncrement(&extendedReferenceCalls);
    if (pointer == nullptr)
    {
        return 3;
    }

    const unsigned char expected[] = {
        0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff,
        static_cast<unsigned char>(sign < 0 ? 0xbf : 0x3f) };
    unsigned char input[sizeof(expected)] = {};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), pointer, input, sizeof(input), &copied)
        || memcmp(input, expected, sizeof(expected)) != 0)
    {
        return -1;
    }

    const unsigned char changed[] = { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0x40 };
    return WriteProcessMemory(GetCurrentProcess(), pointer, changed,
        sizeof(changed), &copied) ? 7 : -2;
}

EXPORT int __stdcall WriteExtendedMidpoint(void* pointer, int sign)
{
    unsigned short control = 0;
    __asm fnstcw control
    const unsigned char midpoint[] = {
        0, static_cast<unsigned char>(sign == 2 || sign == -2 ? 12 : 4),
        0, 0, 0, 0, 0, 0x80, 0xff,
        static_cast<unsigned char>(sign < 0 ? 0xbf : 0x3f)
    };
    SIZE_T copied = 0;
    return WriteProcessMemory(GetCurrentProcess(), pointer, midpoint,
        sizeof(midpoint), &copied) ? 7 + 10 * ((control >> 10) & 3) : 0;
}

EXPORT int __stdcall WriteExtendedMidpointWithRounding(void* pointer, int sign, int mode)
{
    unsigned short control = 0;
    __asm fnstcw control
    control = static_cast<unsigned short>((control & ~0x0c00) | ((mode & 3) << 10));
    const bool clearZeroDivideMask = (mode & 4) != 0;
    if (clearZeroDivideMask)
    {
        control &= static_cast<unsigned short>(~4);
    }
    __asm fldcw control
    const int result = WriteExtendedMidpoint(pointer, sign);
    __asm fnstcw control
    return clearZeroDivideMask && (control & 4) == 0 ? result + 100 : result;
}

EXPORT int __stdcall WriteExtendedBeforeDivision(void* pointer, int mode)
{
    const int result = WriteExtendedMidpointWithRounding(pointer, 1, mode);
    int marker = 7;
    return result == 0 ? 0 : ProbeX87Divide(&marker);
}

EXPORT int __stdcall WriteExtendedDoubleBoundary(void* pointer, int kind)
{
    const int magnitude = kind < 0 ? -kind : kind;
    const unsigned short exponent = static_cast<unsigned short>(
        (magnitude == 1 ? 0x3bcc : magnitude == 2 ? 0x43ff : 0x7fff)
        | (kind < 0 ? 0x8000 : 0));
    const unsigned char value[] = {
        0, 0, 0, 0, 0, 0, 0,
        static_cast<unsigned char>(magnitude == 3 ? 0xc0 : 0x80),
        static_cast<unsigned char>(exponent),
        static_cast<unsigned char>(exponent >> 8)
    };
    unsigned short control = 0;
    __asm fnstcw control
    SIZE_T copied = 0;
    InterlockedIncrement(&extendedReferenceCalls);
    return WriteProcessMemory(GetCurrentProcess(), pointer, value,
        sizeof(value), &copied) ? 7 + 10 * ((control >> 10) & 3) : 0;
}

static const unsigned char mixedExtendedValues[][10] = {
    { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f },
    { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x7f },
    { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0xbf }
};

EXPORT int __stdcall WriteMixedExtendedArray(int* before, void* array, int* after)
{
    const int first = 11;
    const int last = 99;
    HANDLE process = GetCurrentProcess();
    SIZE_T copied = 0;
    InterlockedIncrement(&extendedReferenceCalls);
    return WriteProcessMemory(process, before, &first, sizeof(first), &copied)
        && WriteProcessMemory(process, array, mixedExtendedValues, sizeof(mixedExtendedValues), &copied)
        && WriteProcessMemory(process, after, &last, sizeof(last), &copied) ? 7 : 0;
}

EXPORT int __stdcall WriteMixedExtendedArrayField(void* root)
{
    void* fields[3] = {};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), root, fields, sizeof(fields), &copied))
    {
        return 0;
    }

    return WriteMixedExtendedArray(
        static_cast<int*>(fields[0]), fields[1], static_cast<int*>(fields[2]));
}

EXPORT int __stdcall WriteMixedExtendedReferenceFields(void* root)
{
    void* fields[3] = {};
    HANDLE process = GetCurrentProcess();
    SIZE_T copied = 0;
    InterlockedIncrement(&extendedReferenceCalls);
    if (!ReadProcessMemory(process, root, fields, sizeof(fields), &copied))
    {
        return 0;
    }

    for (int index = 0; index < 3; ++index)
    {
        if (!WriteProcessMemory(process, fields[index], mixedExtendedValues[index],
            sizeof(mixedExtendedValues[index]), &copied))
        {
            return 0;
        }
    }
    return 7;
}

EXPORT int __stdcall ObserveExtendedReferenceInput(void* pointer)
{
    InterlockedIncrement(&extendedReferenceCalls);
    const unsigned char expected[][10] = {
        { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0x3f },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0xbf },
        { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f },
        { 0x00, 0xd0, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0x3f },
        { 0xcd, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0x3f },
        { 0x00, 0xd0, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0xbf },
        { 0xcd, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0xbf },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x34, 0x40 },
        { 0, 4, 0, 0, 0, 0, 0, 0x80, 0x34, 0x40 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3e, 0x40 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3e, 0xc0 },
        { 0, 0xf8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x3d, 0x40 },
        { 0, 0xc8, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0x3f },
        { 0, 0xc8, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xcc, 0xfb, 0xbf },
        { 0, 0x68, 0x19, 0xe2, 0x58, 0x17, 0xb7, 0xd1, 0x30, 0x40 },
        { 0, 0x60, 0x19, 0xe2, 0x58, 0x17, 0xb7, 0xd1, 0x30, 0x40 },
        { 0, 0x68, 0x19, 0xe2, 0x58, 0x17, 0xb7, 0xd1, 0x30, 0xc0 },
        { 0, 0x60, 0x19, 0xe2, 0x58, 0x17, 0xb7, 0xd1, 0x30, 0xc0 },
        { 0, 8, 0, 0, 0, 0, 0, 0x80, 0x34, 0x40 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x5f, 0x40 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x5f, 0xc0 },
        { 0, 0xf8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x5e, 0x40 },
        { 0, 0xf8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x5e, 0xc0 },
        { 0, 0xc8, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0x3f },
        { 0, 0xc8, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0xbf },
        { 0, 0xd0, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0x3f },
        { 0, 0xd0, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0xbf },
        { 0, 0xc0, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0x3f },
        { 0, 0xc0, 0, 0x83, 0xf2, 0xb5, 0x87, 0xfd, 0xa1, 0xbf },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x3f, 0x40 },
        { 0, 0xf8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x3e, 0x40 },
        { 0, 0, 0, 0, 0, 0xcd, 0xcc, 0xcc, 0xfb, 0x3f },
        { 0, 0, 0, 0, 0, 0xcd, 0xcc, 0xcc, 0xfb, 0xbf },
        { 0, 0, 0, 0, 0, 0xff, 0xff, 0xff, 0x7e, 0x40 },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0x6a, 0x3f },
        { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x7f },
        { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0xff },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0x7f },
        { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0xff }
    };
    unsigned char input[10] = {};
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (!ReadProcessMemory(process, pointer, input, sizeof(input), &copied))
    {
        return -1;
    }

    for (int index = 0; index < static_cast<int>(ARRAYSIZE(expected)); ++index)
    {
        if (std::memcmp(input, expected[index], sizeof(input)) == 0)
        {
            const unsigned char changed[] = { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0x40 };
            return WriteProcessMemory(process, pointer, changed, sizeof(changed), &copied)
                ? index : -2;
        }
    }

    return -3;
}

EXPORT __declspec(naked) int __stdcall ObserveExtendedValueInput(int, int, int)
{
    __asm
    {
        lea eax, [esp+4]
        push eax
        call ObserveExtendedReferenceInput
        ret 12
    }
}

template<int Count>
static int ObserveExtendedElements(void* root, int shape)
{
    void* fields[Count] = { root };
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (shape == 1)
    {
        if (!ReadProcessMemory(process, root, fields, sizeof(fields), &copied))
        {
            return -20;
        }
    }
    else
    {
        if (shape == 2
            && !ReadProcessMemory(process, root, fields, sizeof(fields[0]), &copied))
        {
            return -20;
        }
        if (fields[0] == nullptr)
        {
            return -20;
        }
        for (int index = 1; index < Count; ++index)
        {
            fields[index] = static_cast<unsigned char*>(fields[0]) + index * 10;
        }
    }

    int combined = 0;
    bool failed = false;
    for (void* field : fields)
    {
        const int code = ObserveExtendedReferenceInput(field);
        failed |= code < 0;
        combined = combined * 100 + code;
    }
    return failed ? -21 : combined;
}

EXPORT int __stdcall ObserveExtendedPair(void* root, int shape)
{
    return ObserveExtendedElements<2>(root, shape);
}

EXPORT int __stdcall ObserveExtendedTriple(void* root, int shape)
{
    return ObserveExtendedElements<3>(root, shape);
}

EXPORT int __stdcall UpdateExtendedArray(void* pointer, int count)
{
    InterlockedIncrement(&extendedReferenceCalls);
    if (pointer == nullptr)
    {
        return 3;
    }
    if (count != 1 && count != 2)
    {
        return -1;
    }

    constexpr SIZE_T elementSize = 10;
    const SIZE_T size = elementSize * static_cast<SIZE_T>(count);
    const unsigned char expected[] = {
        0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f,
        0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0xbf };
    unsigned char input[sizeof(expected)] = {};
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (!ReadProcessMemory(process, pointer, input, size, &copied)
        || memcmp(input, expected, size) != 0)
    {
        return -2;
    }

    const unsigned char changed[] = {
        0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0x40,
        0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0xc0 };
    return WriteProcessMemory(process, pointer, changed, size, &copied) ? 7 : -3;
}

EXPORT int __stdcall UpdateExtendedArrayPair(void* root)
{
    void* fields[2] = {};
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (!ReadProcessMemory(process, root, fields, sizeof(fields), &copied)
        || fields[0] == fields[1]
        || ObserveExtendedReferenceInput(fields[0]) != 1
        || UpdateExtendedArray(fields[1], 2) != 7)
    {
        return -1;
    }

    return 17;
}

EXPORT int __stdcall UpdateAliasedExtendedArrays(void* root, int child)
{
    void* fields[2] = {};
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    const unsigned char negative[] = { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0xc0 };
    if (!ReadProcessMemory(process, root, fields, sizeof(fields), &copied))
    {
        return -1;
    }

    if (child >= 0 && child < 2
        && !ReadProcessMemory(process, fields[child], &fields[child],
            sizeof(fields[child]), &copied))
    {
        return -1;
    }

    if (fields[0] == fields[1]
        || UpdateExtendedArray(fields[0], 2) != 7
        || ObserveExtendedReferenceInput(fields[1]) != 3
        || !WriteProcessMemory(process, fields[1], negative, sizeof(negative), &copied))
    {
        return -1;
    }

    return 17;
}

EXPORT int __stdcall WriteExtendedFieldsBeforeDivision(void* root, int mode)
{
    void* fields[3] = {};
    const int marker = 99;
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (!ReadProcessMemory(process, root, fields, sizeof(fields), &copied)
        || UpdateExtendedArray(fields[1], 2) != 7
        || !WriteProcessMemory(process, fields[2], &marker, sizeof(marker), &copied))
    {
        return 0;
    }

    return WriteExtendedBeforeDivision(fields[0], mode);
}

EXPORT int __stdcall UpdateExtendedArrayField(void* root, int count, int packed)
{
    if (root == nullptr)
    {
        return -1;
    }

    auto* bytes = static_cast<unsigned char*>(root);
    const int offset = packed ? 1 : 4;
    void* elements = nullptr;
    int tail = 0;
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (*bytes != 7
        || !ReadProcessMemory(process, bytes + offset, &elements, sizeof(elements), &copied)
        || !ReadProcessMemory(process, bytes + offset + sizeof(elements),
            &tail, sizeof(tail), &copied)
        || tail != 0x12345678)
    {
        return -2;
    }

    return UpdateExtendedArray(elements, count);
}

EXPORT int __stdcall UpdateExtendedField(void* root, int reference, int packed)
{
    if (root == nullptr)
    {
        return -1;
    }

    HANDLE process = GetCurrentProcess();
    SIZE_T copied = 0;
    auto* bytes = static_cast<unsigned char*>(root);
    unsigned char tag = 0;
    if (!ReadProcessMemory(process, bytes, &tag, sizeof(tag), &copied) || tag != 7)
    {
        return -2;
    }

    const unsigned char expected[] = { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f };
    const unsigned char changed[] = { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0x40 };
    const int begin = reference ? (packed ? 1 : 4) : 1;
    const int end = reference ? begin : 12;
    for (int offset = begin; offset <= end; ++offset)
    {
        void* field = bytes + offset;
        if (reference && !ReadProcessMemory(process, field, &field, sizeof(field), &copied))
        {
            return -3;
        }

        unsigned char value[sizeof(expected)] = {};
        if (!ReadProcessMemory(process, field, value, sizeof(value), &copied)
            || memcmp(value, expected, sizeof(expected)) != 0)
        {
            continue;
        }

        const int after = offset + (reference ? 4 : 10);
        for (int tail = after; tail <= after + 3; ++tail)
        {
            int number = 0;
            if (ReadProcessMemory(process, bytes + tail, &number, sizeof(number), &copied)
                && number == 0x12345678)
            {
                return WriteProcessMemory(process, field, changed, sizeof(changed), &copied)
                    ? offset * 100 + tail : -4;
            }
        }

        return -5;
    }

    return -6;
}

// x86の引数領域から80ビット値と後続のINTを読みます。
EXPORT __declspec(naked) int __stdcall InspectExtendedArgument(int, int, int, int)
{
    __asm
    {
        xor eax, eax
        cmp dword ptr [esp+4], 0
        jne finished
        cmp dword ptr [esp+8], 0xc0000000
        jne finished
        movzx ecx, word ptr [esp+12]
        cmp ecx, 0x3fff
        je positive
        cmp ecx, 0xbfff
        jne finished
        cmp dword ptr [esp+16], 72
        jne finished
        mov eax, 72
        jmp finished
    positive:
        cmp dword ptr [esp+16], 71
        jne finished
        mov eax, 71
    finished:
        ret 16
    }
}

EXPORT __declspec(naked) void __stdcall ReturnExtendedProbe()
{
    __asm
    {
        fld tbyte ptr [extendedProbeResult]
        ret
    }
}

EXPORT std::int64_t __stdcall ReturnExtendedIntegerProbe(int sign)
{
    if (sign == 2)
    {
        return INT64_MAX;
    }
    if (sign == -2)
    {
        return INT64_MIN;
    }
    return sign < 0 ? -9007199254740993LL : 9007199254740993LL;
}

EXPORT int __stdcall ReadExtendedIntegerParity(std::int64_t value)
{
    return static_cast<int>(value % 2);
}

// 2^53付近と符号付き64ビット整数の境界で仮数の下位32ビットを返します。
EXPORT __declspec(naked) int __stdcall InspectExtendedIntegerProbe(int, int, int, int)
{
    __asm
    {
        mov eax, -1
        movzx ecx, word ptr [esp+12]
        cmp ecx, 0x403d
        je belowMaximum
        cmp dword ptr [esp+8], 0x80000000
        jne finished
        cmp ecx, 0x4034
        je positive
        cmp ecx, 0x403e
        je maximum
        cmp ecx, 0xc03e
        je minimum
        cmp ecx, 0xc034
        jne finished
        cmp dword ptr [esp+16], 72
        jne finished
        jmp matched
    positive:
        cmp dword ptr [esp+16], 71
        jne finished
        jmp matched
    maximum:
        cmp dword ptr [esp+16], 73
        jne finished
        jmp matched
    minimum:
        cmp dword ptr [esp+16], 74
        jne finished
        jmp matched
    belowMaximum:
        cmp dword ptr [esp+16], 73
        jne finished
        cmp dword ptr [esp+8], 0xffffffff
        jne finished
    matched:
        mov eax, dword ptr [esp+4]
    finished:
        ret 16
    }
}

#pragma comment(linker, "/EXPORT:ObserveConcurrentX87=_ObserveConcurrentX87@8")
#pragma comment(linker, "/EXPORT:ProbeX87Divide=_ProbeX87Divide@4")
#pragma comment(linker, "/EXPORT:ProbeX87InvalidOperation=_ProbeX87InvalidOperation@4")
#pragma comment(linker, "/EXPORT:ObserveReferencePair=_ObserveReferencePair@8")
#pragma comment(linker, "/EXPORT:ObserveHandlePair=_ObserveHandlePair@8")
#pragma comment(linker, "/EXPORT:ObserveHandleArray=_ObserveHandleArray@4")
#pragma comment(linker, "/EXPORT:InspectHandleText=_InspectHandleText@4")
#pragma comment(linker, "/EXPORT:InspectHandleTextStorage=_InspectHandleTextStorage@4")
#pragma comment(linker, "/EXPORT:InspectHandleTextLayout=_InspectHandleTextLayout@12")
#pragma comment(linker, "/EXPORT:InspectHandleAnsiCodePage=_InspectHandleAnsiCodePage@4")
#pragma comment(linker, "/EXPORT:InspectHandleAnsiCodePageField=_InspectHandleAnsiCodePageField@4")
#pragma comment(linker, "/EXPORT:InspectHandleTextIndirectStorage=_InspectHandleTextIndirectStorage@4")
#pragma comment(linker, "/EXPORT:ObserveTypedReferencePair=_ObserveTypedReferencePair@8")
#pragma comment(linker, "/EXPORT:ObserveTypedReferenceFields=_ObserveTypedReferenceFields@8")
#pragma comment(linker, "/EXPORT:ObserveMixedReferenceFields=_ObserveMixedReferenceFields@12")
#pragma comment(linker, "/EXPORT:ObserveIntArray=_ObserveIntArray@8")
#pragma comment(linker, "/EXPORT:ObserveNatural=_ObserveNatural@8")
#pragma comment(linker, "/EXPORT:ObservePacked=_ObservePacked@4")
#pragma comment(linker, "/EXPORT:ObserveNested=_ObserveNested@8")
#pragma comment(linker, "/EXPORT:ObservePackedNested=_ObservePackedNested@8")
#pragma comment(linker, "/EXPORT:ObserveAnsiText=_ObserveAnsiText@8")
#pragma comment(linker, "/EXPORT:ObserveWideText=_ObserveWideText@8")
#pragma comment(linker, "/EXPORT:ReadCharUnit=_ReadCharUnit@4")
#pragma comment(linker, "/EXPORT:ReadWcharUnit=_ReadWcharUnit@4")
#pragma comment(linker, "/EXPORT:MakeCharUnit=_MakeCharUnit@4")
#pragma comment(linker, "/EXPORT:MakeWcharUnit=_MakeWcharUnit@4")
#pragma comment(linker, "/EXPORT:WriteCharFields=_WriteCharFields@4")
#pragma comment(linker, "/EXPORT:ReturnAnsiText=_ReturnAnsiText@4")
#pragma comment(linker, "/EXPORT:ReturnWideText=_ReturnWideText@4")
#pragma comment(linker, "/EXPORT:ReturnAnsiTail=_ReturnAnsiTail@4")
#pragma comment(linker, "/EXPORT:ReturnWideTail=_ReturnWideTail@4")
#pragma comment(linker, "/EXPORT:ObserveInlineText=_ObserveInlineText@12")
#pragma comment(linker, "/EXPORT:ObserveReferencedText=_ObserveReferencedText@16")
#pragma comment(linker, "/EXPORT:ObserveNestedReferencedText=_ObserveNestedReferencedText@16")
#pragma comment(linker, "/EXPORT:ObserveSiblingReferencedText=_ObserveSiblingReferencedText@8")
#pragma comment(linker, "/EXPORT:ObserveTwoReferencedTextFields=_ObserveTwoReferencedTextFields@8")
#pragma comment(linker, "/EXPORT:ObserveAliasedTextFields=_ObserveAliasedTextFields@8")
#pragma comment(linker, "/EXPORT:ObserveReversedAlias=_ObserveReversedAlias@8")
#pragma comment(linker, "/EXPORT:ObserveTextAndTail=_ObserveTextAndTail@16")
#pragma comment(linker, "/EXPORT:ObserveTextAndThreeTails=_ObserveTextAndThreeTails@24")
#pragma comment(linker, "/EXPORT:WaitTextCopyback=_WaitTextCopyback@20")
#pragma comment(linker, "/EXPORT:WaitOtherText=_WaitOtherText@20")
#pragma comment(linker, "/EXPORT:ObserveArrayField=_ObserveArrayField@20")
#pragma comment(linker, "/EXPORT:NumericFieldCallCount=_NumericFieldCallCount@0")
#pragma comment(linker, "/EXPORT:InspectStructureTree=_InspectStructureTree@12")
#pragma comment(linker, "/EXPORT:InspectStructureRoots=_InspectStructureRoots@20")
#pragma comment(linker, "/EXPORT:InspectStructureRootBytes=_InspectStructureRootBytes@16")
#pragma comment(linker, "/EXPORT:InspectStructureBytes=_InspectStructureBytes@12")
#pragma comment(linker, "/EXPORT:InspectMixedStructureBytes=_InspectMixedStructureBytes@12")
#pragma comment(linker, "/EXPORT:InspectMixedStructureWideBytes=_InspectMixedStructureWideBytes@12")
#pragma comment(linker, "/EXPORT:InspectMixedStructureSupplementaryBytes=_InspectMixedStructureSupplementaryBytes@12")
#pragma comment(linker, "/EXPORT:InspectMixedStructureAnsiBytes=_InspectMixedStructureAnsiBytes@16")
#pragma comment(linker, "/EXPORT:InspectMixedStructureTextTail=_InspectMixedStructureTextTail@16")
#pragma comment(linker, "/EXPORT:InspectMixedStructureTextPointer=_InspectMixedStructureTextPointer@12")
#pragma comment(linker, "/EXPORT:InspectMixedStructureArrayPointer=_InspectMixedStructureArrayPointer@16")
#pragma comment(linker, "/EXPORT:InspectMixedStructureExtendedArray=_InspectMixedStructureExtendedArray@16")
#pragma comment(linker, "/EXPORT:InspectMixedStructureChildPointer=_InspectMixedStructureChildPointer@16")
#pragma comment(linker, "/EXPORT:InspectMixedStructureTrailingChild=_InspectMixedStructureTrailingChild@16")
#pragma comment(linker, "/EXPORT:StructureProbeCallCount=_StructureProbeCallCount@0")
#pragma comment(linker, "/EXPORT:ArgumentProbeCallCount=_ArgumentProbeCallCount@0")
#pragma comment(linker, "/EXPORT:ArgumentBlockCallCount=_ArgumentBlockCallCount@0")
#pragma comment(linker, "/EXPORT:InspectBlockFields=_InspectBlockFields@8")
#pragma comment(linker, "/EXPORT:InspectBlockInt512=_InspectBlockInt512@2048")
#pragma comment(linker, "/EXPORT:InspectBlockInt513=_InspectBlockInt513@2052")
#pragma comment(linker, "/EXPORT:InspectBlockFloat512=_InspectBlockFloat512@2048")
#pragma comment(linker, "/EXPORT:InspectBlockFloat513=_InspectBlockFloat513@2052")
#pragma comment(linker, "/EXPORT:InspectBlockDouble256=_InspectBlockDouble256@2048")
#pragma comment(linker, "/EXPORT:InspectBlockDouble257=_InspectBlockDouble257@2056")
#pragma comment(linker, "/EXPORT:InspectBlockLongLong256=_InspectBlockLongLong256@2048")
#pragma comment(linker, "/EXPORT:InspectBlockLongLong257=_InspectBlockLongLong257@2056")
#pragma comment(linker, "/EXPORT:InspectBlockByte512=_InspectBlockByte512@2048")
#pragma comment(linker, "/EXPORT:InspectBlockByte513=_InspectBlockByte513@2052")
#pragma comment(linker, "/EXPORT:InspectBlockWord512=_InspectBlockWord512@2048")
#pragma comment(linker, "/EXPORT:InspectBlockWord513=_InspectBlockWord513@2052")
#pragma comment(linker, "/EXPORT:InspectBlockTailDouble512=_InspectBlockTailDouble512@2052")
#pragma comment(linker, "/EXPORT:InspectBlockTailDouble511=_InspectBlockTailDouble511@2048")
#pragma comment(linker, "/EXPORT:InspectBlockTailPointer512=_InspectBlockTailPointer512@2048")
#pragma comment(linker, "/EXPORT:InspectBlockTailPointer513=_InspectBlockTailPointer513@2052")
#pragma comment(linker, "/EXPORT:SetArgumentElement=_SetArgumentElement@4")
#pragma comment(linker, "/EXPORT:InspectArgumentFields=_InspectArgumentFields@12")
#pragma comment(linker, "/EXPORT:InspectNullableReferences=_InspectNullableReferences@16")
#pragma comment(linker, "/EXPORT:InspectNullableReferenceFields=_InspectNullableReferenceFields@8")
#pragma comment(linker, "/EXPORT:NullableReferenceProbeState=_NullableReferenceProbeState@0")
#pragma comment(linker, "/EXPORT:InspectScalarArray=_InspectScalarArray@4")
#pragma comment(linker, "/EXPORT:InspectScalarArrayField=_InspectScalarArrayField@4")
#pragma comment(linker, "/EXPORT:InspectScalarArrayValue=_InspectScalarArrayValue@4")
#pragma comment(linker, "/EXPORT:InspectScalarArrayValueField=_InspectScalarArrayValueField@4")
#pragma comment(linker, "/EXPORT:InspectArrayOrderFirst=_InspectArrayOrderFirst@12")
#pragma comment(linker, "/EXPORT:InspectArrayOrderMiddle=_InspectArrayOrderMiddle@12")
#pragma comment(linker, "/EXPORT:InspectArrayOrderLast=_InspectArrayOrderLast@12")
#pragma comment(linker, "/EXPORT:InspectArrayOrderFields=_InspectArrayOrderFields@12")
#pragma comment(linker, "/EXPORT:ScalarArrayProbeState=_ScalarArrayProbeState@0")
#pragma comment(linker, "/EXPORT:ScalarArrayProbeValue=_ScalarArrayProbeValue@0")
#pragma comment(linker, "/EXPORT:InspectReferenceArgs20=_InspectReferenceArgs20@80")
#pragma comment(linker, "/EXPORT:InspectTailReferenceArgs20=_InspectTailReferenceArgs20@80")
#pragma comment(linker, "/EXPORT:InspectReferenceArgs21=_InspectReferenceArgs21@84")
#pragma comment(linker, "/EXPORT:InspectTailReferenceArgs21=_InspectTailReferenceArgs21@84")
#pragma comment(linker, "/EXPORT:InspectValueArgs40=_InspectValueArgs40@160")
#pragma comment(linker, "/EXPORT:InspectReferenceArgs40=_InspectReferenceArgs40@160")
#pragma comment(linker, "/EXPORT:InspectTextArgs40=_InspectTextArgs40@160")
#pragma comment(linker, "/EXPORT:InspectTailReferenceArgs40=_InspectTailReferenceArgs40@160")
#pragma comment(linker, "/EXPORT:InspectTailTextArgs40=_InspectTailTextArgs40@160")
#pragma comment(linker, "/EXPORT:InspectValueArgs41=_InspectValueArgs41@164")
#pragma comment(linker, "/EXPORT:InspectReferenceArgs41=_InspectReferenceArgs41@164")
#pragma comment(linker, "/EXPORT:InspectTextArgs41=_InspectTextArgs41@164")
#pragma comment(linker, "/EXPORT:InspectTailReferenceArgs41=_InspectTailReferenceArgs41@164")
#pragma comment(linker, "/EXPORT:InspectTailTextArgs41=_InspectTailTextArgs41@164")
#endif
EXPORT int __stdcall ObserveReferencePair(int* first, int* second)
{
    *first += 10;
    const int observed = *second;
    *second += 20;
    return observed + (first == second ? 1000 : 0);
}

EXPORT int __stdcall ObserveIntArray(int* values, int length)
{
    if (length == 0)
    {
        return values == nullptr ? 0 : 100;
    }

    int observed = 0;
    for (int index = 0; index < length; ++index)
    {
        observed = 10 * observed + values[index];
        values[index] += 10;
    }

    return observed;
}
EXPORT void WriteNatural(Natural* value)
{
    value->tag = 7;
    value->value = 12.5;
    value->count = UINT32_MAX;
}
EXPORT void WritePacked(Packed* value)
{
    value->tag = 9;
    value->value = -2.25;
    value->count = 123;
}

EXPORT int __stdcall ObserveNatural(Natural* value, int marker)
{
    if (value->tag != 3 || value->value != 1.25 || value->count != 5)
    {
        return -1;
    }

    WriteNatural(value);
    return marker;
}

EXPORT int __stdcall ObservePacked(Packed* value)
{
    if (value->tag != 3 || value->value != 1.25 || value->count != 5)
    {
        return -1;
    }

    WritePacked(value);
    return 42;
}

template<typename Child>
struct StructureParent
{
    std::uint8_t tag;
    Child* child;
    std::uint16_t tail;
};

static_assert(offsetof(StructureParent<Natural>, child) == sizeof(void*));
static_assert(offsetof(StructureParent<Packed>, child) == sizeof(void*));

template<typename Child>
int ObserveChild(StructureParent<Child>* root, int replace)
{
    // 親の後続フィールドも合う場合にだけ子ポインターを使います。
    if (root->tag != 2 || root->tail != 19 || root->child == nullptr)
    {
        return -1;
    }

    Child* child = root->child;
    if (child->tag != 3 || child->value != 1.25 || child->count != 5)
    {
        return -2;
    }

    root->tag = 4;
    root->tail = 29;
    child->tag = 7;
    child->value = -2.25;
    child->count = UINT32_MAX;
    if (replace != 0)
    {
        static Child replacement{90, 91.5, 92};
        root->child = &replacement;
    }

    return 42;
}

EXPORT int __stdcall ObserveNested(StructureParent<Natural>* root, int replace)
{
    return ObserveChild(root, replace);
}

EXPORT int __stdcall ObservePackedNested(StructureParent<Packed>* root,
    int replace)
{
    return ObserveChild(root, replace);
}
EXPORT void WriteStrings(char* ansi, wchar_t* wide)
{
    ansi[0] = 'A'; ansi[1] = 'B'; ansi[2] = 0;
    wide[0] = L'\u65e5'; wide[1] = L'\u672c'; wide[2] = 0;
}

template<typename Unit>
int ObserveText(Unit* value, int write)
{
    if (value == nullptr)
    {
        return -1;
    }

    int length = 0;
    while (value[length] != 0)
    {
        ++length;
    }

    if (write != 0 && length >= 5)
    {
        value[0] = 'A';
        value[1] = 0;
        value[2] = 'C';
    }

    return length * 65536 + static_cast<std::uint16_t>(value[0]);
}

EXPORT int __stdcall ObserveAnsiText(unsigned char* value, int write)
{
    return ObserveText(value, write);
}

EXPORT int __stdcall ObserveWideText(wchar_t* value, int write)
{
    return ObserveText(value, write);
}

EXPORT const unsigned char* __stdcall ReturnAnsiText(int mode)
{
    static const unsigned char text[]{'A', 0, 'B', 0};
    static const unsigned char raw[]{0x93, 0};
    return mode == 0 ? text : mode == 1 ? raw : nullptr;
}

EXPORT const wchar_t* __stdcall ReturnWideText(int mode)
{
    static const wchar_t text[]{0x65e5, 0, 0x672c, 0};
    static const wchar_t raw[]{0xd83d, 0};
    return mode == 0 ? text : mode == 1 ? raw : nullptr;
}

EXPORT const char* __stdcall ReturnAnsiTail(const char* text)
{
    return text + 1;
}

EXPORT const wchar_t* __stdcall ReturnWideTail(const wchar_t* text)
{
    return text + 1;
}

EXPORT const char* __stdcall ObserveInlineText(unsigned char* bytes, int count,
    int writeOffset)
{
    static thread_local char result[65];
    static constexpr char digits[] = "0123456789ABCDEF";
    if (count < 0 || count > 32)
    {
        return nullptr;
    }

    for (int index = 0; index < count; ++index)
    {
        unsigned char value = bytes[index];
        result[index * 2] = digits[value >> 4];
        result[index * 2 + 1] = digits[value & 0xf];
    }

    result[count * 2] = 0;
    if (writeOffset >= 0 && writeOffset <= count - 3)
    {
        bytes[writeOffset] = 'X';
        bytes[writeOffset + 1] = 0;
        bytes[writeOffset + 2] = 'Z';
    }

    return result;
}

template<typename T>
const char* ObserveArrayFieldValues(unsigned char* bytes, int offset,
    int count, int mode)
{
    static thread_local char result[160];
    T* values = nullptr;
    std::memcpy(&values, bytes + offset, sizeof(values));
    int tail = bytes[offset + sizeof(values)];
    if (values == nullptr)
    {
        std::snprintf(result, sizeof(result), "%u,%d,null", bytes[0], tail);
        return result;
    }
    if (count == 0 || count == 1)
    {
        T value{};
        SIZE_T read = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), values, &value,
            sizeof(value), &read) || read != sizeof(value))
        {
            return "unreadable-scalar";
        }
        std::snprintf(result, sizeof(result), "%u,%d,scalar,%.17g", bytes[0], tail,
            static_cast<double>(value));
        if (mode != 0)
        {
            T changed = static_cast<T>(101);
            SIZE_T written = 0;
            if (!WriteProcessMemory(GetCurrentProcess(), values, &changed,
                sizeof(changed), &written) || written != sizeof(changed))
            {
                return "unwritable-scalar";
            }
        }
        return result;
    }
    if (count < 3 || count > 32) { return "invalid-count"; }
    T original[3]{};
    SIZE_T read = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), values, original,
        sizeof(original), &read) || read != sizeof(original))
    {
        return "unreadable";
    }
    std::snprintf(result, sizeof(result), "%u,%d,%d,%.17g,%.17g,%.17g",
        bytes[0], tail, count, static_cast<double>(original[0]),
        static_cast<double>(original[1]), static_cast<double>(original[2]));
    if (mode != 0)
    {
        values[0] = static_cast<T>(101);
        values[1] = static_cast<T>(202);
        values[2] = static_cast<T>(203);
        if (mode == 2)
        {
            static T replacement[]{31, 32, 33};
            T* pointer = replacement;
            std::memcpy(bytes + offset, &pointer, sizeof(pointer));
        }
    }
    return result;
}

static volatile LONG numericFieldCalls = 0;

// 型エラーになる条件でも、呼出し先への到達を独立して確認します。
EXPORT int __stdcall NumericFieldCallCount()
{
    return static_cast<int>(InterlockedCompareExchange(&numericFieldCalls, 0, 0));
}

// 配列の先頭3要素と後続フィールドを観測し、所有する領域だけを書き換えます。
EXPORT const char* __stdcall ObserveArrayField(unsigned char* bytes, int offset,
    int kind, int count, int mode)
{
    InterlockedIncrement(&numericFieldCalls);
    switch (kind)
    {
    case 1: return ObserveArrayFieldValues<std::uint8_t>(bytes, offset, count, mode);
    case 2: return ObserveArrayFieldValues<std::uint16_t>(bytes, offset, count, mode);
    case 3: return ObserveArrayFieldValues<std::int32_t>(bytes, offset, count, mode);
    case 4: return ObserveArrayFieldValues<std::uint32_t>(bytes, offset, count, mode);
    case 5: return ObserveArrayFieldValues<std::int64_t>(bytes, offset, count, mode);
    case 6: return ObserveArrayFieldValues<float>(bytes, offset, count, mode);
    case 7: return ObserveArrayFieldValues<double>(bytes, offset, count, mode);
    default: return "invalid-kind";
    }
}

// 不明な配置の観測でも、所有プロセスの読取り可能な範囲だけを参照します。
EXPORT const char* __stdcall ObserveReferencedText(unsigned char* bytes,
    int offset, int wide, int mode)
{
    static thread_local char result[160];
    void* pointer = nullptr;
    std::memcpy(&pointer, bytes + offset, sizeof(pointer));
    int length = pointer == nullptr ? -1 : 0;
    unsigned int first = 0;
    int unit = wide != 0 ? 2 : 1;
    if (pointer != nullptr)
    {
        for (; length < 32; ++length)
        {
            std::uint16_t value = 0;
            SIZE_T read = 0;
            if (!ReadProcessMemory(GetCurrentProcess(),
                static_cast<unsigned char*>(pointer) + length * unit,
                &value, unit, &read) || read != static_cast<SIZE_T>(unit))
            {
                length = -2;
                break;
            }
            if (length == 0) { first = value; }
            if (value == 0) { break; }
        }
    }
    std::snprintf(result, sizeof(result), "%u,%u,%d,%u",
        bytes[0], bytes[offset + sizeof(pointer)], length, first);
    if (mode != 0 && length >= 3)
    {
        static unsigned char ansi[]{'R', 0, 'S', 0};
        static wchar_t unicode[]{L'R', 0, L'S', 0};
        if (wide != 0)
        {
            auto text = static_cast<wchar_t*>(pointer);
            text[0] = L'X'; text[1] = 0; text[2] = L'Z';
        }
        else
        {
            auto text = static_cast<unsigned char*>(pointer);
            text[0] = 'X'; text[1] = 0; text[2] = 'Z';
        }
        if (mode == 2)
        {
            void* replacement = wide != 0 ? static_cast<void*>(unicode)
                : static_cast<void*>(ansi);
            std::memcpy(bytes + offset, &replacement, sizeof(replacement));
        }
    }
    return result;
}

// 親の前後の値を確認してから、子構造体の文字列フィールドを観測します。
EXPORT const char* __stdcall ObserveNestedReferencedText(
    StructureParent<unsigned char>* root, int offset, int wide, int mode)
{
    if (root->tag != 2 || root->tail != 19 || root->child == nullptr
        || (offset != 1 && offset != static_cast<int>(sizeof(void*))))
    {
        return "invalid-parent";
    }

    unsigned char childHeader[2 + sizeof(void*) * 2]{};
    SIZE_T copied = 0;
    const SIZE_T size = static_cast<SIZE_T>(offset) + sizeof(void*) + 1;
    if (!ReadProcessMemory(GetCurrentProcess(), root->child, childHeader, size, &copied)
        || copied != size || childHeader[0] != 7)
    {
        return "invalid-child";
    }

    return ObserveReferencedText(root->child, offset, wide, mode);
}

struct ReferencedTextSiblings
{
    std::uint8_t tag;
    unsigned char* ansi;
    unsigned char* wide;
    std::uint16_t tail;
};

// 2個の子構造体にあるANSI・UTF-16の文字列を別々に書き換えます。
EXPORT int __stdcall ObserveSiblingReferencedText(
    ReferencedTextSiblings* root, int offset)
{
    if (root->tag != 2 || root->tail != 19 || root->ansi == root->wide
        || root->ansi == nullptr || root->wide == nullptr
        || (offset != 1 && offset != static_cast<int>(sizeof(void*))))
    {
        return -1;
    }

    unsigned char childHeader[2 + sizeof(void*) * 2]{};
    SIZE_T copied = 0;
    const SIZE_T size = static_cast<SIZE_T>(offset) + sizeof(void*) + 1;
    if (!ReadProcessMemory(GetCurrentProcess(), root->ansi, childHeader, size, &copied)
        || copied != size || childHeader[0] != 7
        || !ReadProcessMemory(GetCurrentProcess(), root->wide, childHeader, size, &copied)
        || copied != size || childHeader[0] != 8)
    {
        return -4;
    }

    if (std::strcmp(ObserveReferencedText(root->ansi, offset, 0, 1),
        "7,42,5,97") != 0)
    {
        return -2;
    }

    return std::strcmp(ObserveReferencedText(root->wide, offset, 1, 2),
        "8,43,5,118") == 0 ? 1 : -3;
}

// 同じ子構造体内の2つの文字列ポインターを、間の数値と合わせて観測します。
static int ObserveTwoReferencedTextFieldsCore(
    StructureParent<unsigned char>* root, int offset, const char* expectedWide,
    wchar_t wideReplacement, bool wideFirst = false)
{
    if (root->tag != 2 || root->tail != 19 || root->child == nullptr
        || (offset != 1 && offset != static_cast<int>(sizeof(void*))))
    {
        return -1;
    }

    const auto middle = static_cast<SIZE_T>(offset) + sizeof(void*);
    const auto size = 2 * middle + 1;
    unsigned char header[4 * sizeof(void*) + 1]{};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), root->child, header, size, &copied)
        || copied != size || header[0] != 7 || header[middle] != 8)
    {
        return -2;
    }

    void* ansi = nullptr;
    void* wide = nullptr;
    auto* ansiField = root->child + (wideFirst ? middle : 0);
    auto* wideField = root->child + (wideFirst ? 0 : middle);
    std::memcpy(&ansi, header + offset + (wideFirst ? middle : 0), sizeof(ansi));
    std::memcpy(&wide, header + offset + (wideFirst ? 0 : middle), sizeof(wide));
    if (ansi == nullptr || wide == nullptr || ansi == wide)
    {
        return -5;
    }

    if (std::strcmp(ObserveReferencedText(ansiField, offset, 0, 1),
        wideFirst ? "8,43,5,97" : "7,8,5,97") != 0)
    {
        return -3;
    }

    if (std::strcmp(ObserveReferencedText(wideField, offset, 1, 2),
        expectedWide) != 0)
    {
        return -4;
    }

    std::memcpy(wide, &wideReplacement, sizeof(wideReplacement));
    return 1;
}

EXPORT int __stdcall ObserveTwoReferencedTextFields(
    StructureParent<unsigned char>* root, int offset)
{
    return ObserveTwoReferencedTextFieldsCore(root, offset, "8,43,5,118", L'X');
}

EXPORT int __stdcall ObserveAliasedTextFields(
    StructureParent<unsigned char>* root, int offset)
{
    return ObserveTwoReferencedTextFieldsCore(root, offset, "8,43,5,97", L'Y');
}

EXPORT int __stdcall ObserveReversedAlias(
    StructureParent<unsigned char>* root, int offset)
{
    return ObserveTwoReferencedTextFieldsCore(root, offset, "7,8,5,97", L'Y', true);
}

EXPORT int __stdcall ObserveTextAndTail(
    StructureParent<unsigned char>* root, int offset, int wideFirst, int* tail)
{
    const int result = wideFirst
        ? ObserveReversedAlias(root, offset)
        : ObserveAliasedTextFields(root, offset);
    if (result == 1)
    {
        *tail = 109;
    }
    return result;
}

EXPORT int __stdcall ObserveTextAndThreeTails(
    StructureParent<unsigned char>* root, int offset, int wideFirst,
    int* first, int* second, int* last)
{
    const int result = ObserveTextAndTail(root, offset, wideFirst, first);
    if (result == 1)
    {
        *second = 113;
        *last = 127;
    }
    return result;
}

EXPORT int __stdcall WaitTextCopyback(
    StructureParent<unsigned char>* root, int offset, int wideFirst,
    const wchar_t* entered, const wchar_t* released)
{
    const int result = wideFirst
        ? ObserveReversedAlias(root, offset)
        : ObserveAliasedTextFields(root, offset);
    if (result != 1)
    {
        return result;
    }

    HANDLE marker = CreateFileW(entered, GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (marker == INVALID_HANDLE_VALUE)
    {
        return -6;
    }
    CloseHandle(marker);
    const ULONGLONG deadline = GetTickCount64() + 3000;
    while (GetTickCount64() < deadline)
    {
        if (GetFileAttributesW(released) != INVALID_FILE_ATTRIBUTES)
        {
            return result;
        }
        Sleep(10);
    }
    return -7;
}

EXPORT int __stdcall WaitOtherText(
    StructureParent<unsigned char>* root, int offset, int wideFirst,
    const wchar_t* entered, const wchar_t* released)
{
    return WaitTextCopyback(root, offset, wideFirst, entered, released);
}

EXPORT int __stdcall ReadCharUnit(std::uint8_t value) { return value; }
EXPORT int __stdcall ReadWcharUnit(std::uint16_t value) { return value; }
EXPORT std::uint8_t __stdcall MakeCharUnit(int value)
{
    return static_cast<std::uint8_t>(value);
}
EXPORT std::uint16_t __stdcall MakeWcharUnit(int value)
{
    return static_cast<std::uint16_t>(value);
}

struct CharFields
{
    std::uint8_t narrow;
    std::uint16_t wide;
};

static_assert(offsetof(CharFields, wide) == 2);

EXPORT int __stdcall WriteCharFields(CharFields* fields)
{
    if (fields->narrow != 'A' || fields->wide != 0x65e5)
    {
        return 0;
    }

    fields->narrow = 'B';
    fields->wide = 0x672c;
    return 1;
}
EXPORT void IncrementArray(int* values, int length)
{
    for (int index = 0; index < length; ++index) { ++values[index]; }
}
EXPORT int NextCounter()
{
    static int counter = 0;
    return ++counter;
}

// callback内で所有者を破棄しても、この関数が戻るまでDLLを保持する検査です。
EXPORT int CallThenReturn(void (*callback)())
{
    callback();
    return 42;
}

// callbackで終了要求を受けた後も、呼出し中の領域へ書き込めることを確認します。
EXPORT void CallbackThenWrite(void (*callback)(), int* value)
{
    callback();
    *value = 42;
}

// 参照領域2個とDLL内の戻り値を、結果読取りまで保持する検査です。
EXPORT const char* CallbackWriteAndReturn(void (*callback)(), int* first, int* second)
{
    callback();
    *first = 12;
    *second = 30;
    return "held";
}

#pragma pack(push, 1)
struct NestedRoot
{
    std::uint8_t tag;
    Natural* child;
    std::uint32_t* alias;
};
#pragma pack(pop)

static_assert(offsetof(NestedRoot, child) == 1);
static_assert(sizeof(NestedRoot) == 1 + 2 * sizeof(void*));

// 子構造体とそのフィールドへの別名参照を、callback後も使用します。
EXPORT const char* CallbackWriteNested(void (*callback)(), NestedRoot* root)
{
    callback();
    root->child->value += root->tag;
    *root->alias += root->child->tag;
    ++root->child->tag;
    ++root->tag;
    return "nested-held";
}

static volatile LONG structureProbeCalls = 0;

// 変換中の拒否と、呼出し先へ到達した後の結果を分けて観測します。
EXPORT int __stdcall StructureProbeCallCount()
{
    return static_cast<int>(InterlockedCompareExchange(&structureProbeCalls, 0, 0));
}

// 所有する構造体の鎖または兄弟の葉を読み、照合できた値だけを書き換えます。
EXPORT int __stdcall InspectStructureTree(void* supplied, int depth, int children)
{
    InterlockedIncrement(&structureProbeCalls);
    SIZE_T copied = 0;
    void* root = supplied;
    for (int level = 1; level < depth; ++level)
    {
        if (!ReadProcessMemory(GetCurrentProcess(), root, &root, sizeof(root), &copied))
        {
            return -1;
        }
    }
    int total = 0;
    for (int index = 0; index < (children ? children : 1); ++index)
    {
        void* leaf = root;
        if (children && !ReadProcessMemory(GetCurrentProcess(),
            static_cast<void**>(root) + index, &leaf, sizeof(leaf), &copied))
        {
            return -2;
        }
        int value = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), leaf, &value, sizeof(value), &copied)
            || value != 11)
        {
            return -3;
        }
        total += value;
        value = 101 + index;
        if (!WriteProcessMemory(GetCurrentProcess(), leaf, &value, sizeof(value), &copied))
        {
            return -4;
        }
    }
    return total;
}

EXPORT int __stdcall InspectStructureRoots(
    void* first, void* second, void* third, void* fourth, int childMask)
{
    InterlockedIncrement(&structureProbeCalls);
    void* roots[] = { first, second, third, fourth };
    HANDLE process = GetCurrentProcess();
    SIZE_T copied = 0;
    for (int index = 0; index < 4; ++index)
    {
        if ((childMask & (1 << index)) != 0
            && !ReadProcessMemory(process, roots[index], &roots[index],
                sizeof(roots[index]), &copied))
        {
            return -1;
        }
        int value = 0;
        if (!ReadProcessMemory(process, roots[index], &value, sizeof(value), &copied)
            || value != 11 + index)
        {
            return -2;
        }
    }
    for (int index = 0; index < 4; ++index)
    {
        const int value = 101 + index;
        if (!WriteProcessMemory(process, roots[index], &value, sizeof(value), &copied))
        {
            return -3;
        }
    }
    return 50;
}

EXPORT int __stdcall InspectStructureRootBytes(
    void* first, void* second, int firstLength, int secondLength)
{
    InterlockedIncrement(&structureProbeCalls);
    unsigned char* roots[] = {
        static_cast<unsigned char*>(first), static_cast<unsigned char*>(second) };
    const int lengths[] = { firstLength, secondLength };
    HANDLE process = GetCurrentProcess();
    SIZE_T copied = 0;
    for (int index = 0; index < 2; ++index)
    {
        const int length = lengths[index];
        unsigned char* bytes = roots[index];
        unsigned char start = 0;
        unsigned char end = 0;
        if (length < 1 || length > 2048
            || !ReadProcessMemory(process, bytes, &start, 1, &copied)
            || !ReadProcessMemory(process, bytes + length - 1, &end, 1, &copied)
            || start != 'a' + index || end != start)
        {
            return -1;
        }
    }
    for (int index = 0; index < 2; ++index)
    {
        const unsigned char changed = static_cast<unsigned char>('Y' + index);
        if (!WriteProcessMemory(process, roots[index] + lengths[index] - 1,
                &changed, 1, &copied))
        {
            return -2;
        }
    }
    return firstLength + secondLength;
}

// インライン文字列の両端を照合し、末尾だけを書き換えます。
EXPORT int __stdcall InspectStructureBytes(void* root, int length, int children)
{
    InterlockedIncrement(&structureProbeCalls);
    SIZE_T copied = 0;
    for (int index = 0; index < (children ? children : 1); ++index)
    {
        void* leaf = root;
        if (children && !ReadProcessMemory(GetCurrentProcess(),
            static_cast<void**>(root) + index, &leaf, sizeof(leaf), &copied))
        {
            return -1;
        }
        auto bytes = static_cast<unsigned char*>(leaf);
        unsigned char first = 0;
        unsigned char last = 0;
        if (length < 1 || length > 4096
            || !ReadProcessMemory(GetCurrentProcess(), bytes, &first, 1, &copied)
            || !ReadProcessMemory(GetCurrentProcess(), bytes + length - 1, &last, 1, &copied)
            || first != 'a' || last != 'a')
        {
            return -2;
        }
        last = 'Z';
        if (!WriteProcessMemory(GetCurrentProcess(), bytes + length - 1, &last, 1, &copied))
        {
            return -3;
        }
    }
    return length * (children ? children : 1);
}

static bool UpdateMixedStructureText(HANDLE process, void* referenced)
{
    char referenceText[4] = {};
    const char changed = 'Z';
    SIZE_T copied = 0;
    return ReadProcessMemory(process, referenced, referenceText,
               sizeof(referenceText), &copied)
        && memcmp(referenceText, "ref", sizeof(referenceText)) == 0
        && WriteProcessMemory(process, referenced, &changed, sizeof(changed), &copied);
}

static bool UpdateMixedStructureArray(HANDLE process, void* referenced, int count)
{
    if (count != 2 && count != 513)
    {
        return false;
    }

    auto* last = static_cast<int*>(referenced) + count - 1;
    int firstValue = 0;
    int lastValue = 0;
    SIZE_T copied = 0;
    const int firstResult = 111;
    const int lastResult = 119;
    return ReadProcessMemory(process, referenced, &firstValue, sizeof(firstValue), &copied)
        && ReadProcessMemory(process, last, &lastValue, sizeof(lastValue), &copied)
        && firstValue == 11 && lastValue == 19
        && WriteProcessMemory(process, referenced, &firstResult, sizeof(firstResult), &copied)
        && WriteProcessMemory(process, last, &lastResult, sizeof(lastResult), &copied);
}

template<class UpdateReference>
static int InspectMixedStructurePointer(void* root, int length, int packed,
    bool alignPackedPointer, UpdateReference updateReference, bool textFirst = false,
    int maxStructureBytes = 2048)
{
    InterlockedIncrement(&structureProbeCalls);
    if (length < 1 || length > 2048)
    {
        return -1;
    }

    const int pointerWidth = static_cast<int>(sizeof(void*));
    const int leadingPointerOffset = packed && !alignPackedPointer ? 1 : pointerWidth;
    const int textOffset = textFirst ? (packed ? 1 : 4)
        : leadingPointerOffset + pointerWidth;
    const int textEnd = textOffset + length;
    const int pointerOffset = textFirst ? (textEnd + pointerWidth - 1) & ~(pointerWidth - 1)
        : leadingPointerOffset;
    const int fieldsEnd = textFirst ? pointerOffset + pointerWidth : textEnd;
    const int numberOffset = packed ? fieldsEnd : (fieldsEnd + 3) & ~3;
    if (numberOffset + static_cast<int>(sizeof(int)) > maxStructureBytes)
    {
        return -1;
    }

    auto* bytes = static_cast<unsigned char*>(root);
    void* referenced = nullptr;
    char inlineEnds[2] = {};
    unsigned char tag = 0;
    int number = 0;
    SIZE_T copied = 0;
    HANDLE process = GetCurrentProcess();
    if (!ReadProcessMemory(process, bytes, &tag, sizeof(tag), &copied)
        || !ReadProcessMemory(process, bytes + pointerOffset,
            &referenced, sizeof(referenced), &copied)
        || !ReadProcessMemory(process, bytes + textOffset, inlineEnds, 1, &copied)
        || !ReadProcessMemory(process, bytes + textEnd - 1, inlineEnds + 1, 1, &copied)
        || !ReadProcessMemory(process, bytes + numberOffset, &number, sizeof(number), &copied)
        || tag != 7 || number != 19 || referenced == nullptr
        || inlineEnds[0] != 'a' || inlineEnds[1] != 'a')
    {
        return -2;
    }

    const char changed = 'Z';
    tag = 9;
    number = 109;
    if (!updateReference(process, referenced)
        || !WriteProcessMemory(process, bytes, &tag, sizeof(tag), &copied)
        || !WriteProcessMemory(process, bytes + textEnd - 1, &changed, 1, &copied)
        || !WriteProcessMemory(process, bytes + numberOffset, &number, sizeof(number), &copied))
    {
        return -3;
    }
    return length;
}

EXPORT int __stdcall InspectMixedStructureTextPointer(void* root, int length, int packed)
{
    return InspectMixedStructurePointer(root, length, packed, false, UpdateMixedStructureText);
}

EXPORT int __stdcall InspectMixedStructureArrayPointer(
    void* root, int length, int packed, int count)
{
    return InspectMixedStructurePointer(root, length, packed, false,
        [count](HANDLE process, void* referenced)
        {
            return UpdateMixedStructureArray(process, referenced, count);
        });
}

EXPORT int __stdcall InspectMixedStructureExtendedArray(
    void* root, int length, int packed, int count)
{
    return InspectMixedStructurePointer(root, length, packed, false,
        [count](HANDLE process, void* referenced)
        {
            if (count != 204 && count != 205 && count != 206
                && count != 512 && count != 1024)
            {
                return false;
            }

            const unsigned char expectedFirst[] =
                { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0x3f };
            const unsigned char expectedLast[] =
                { 0, 0, 0, 0, 0, 0, 0, 0xc0, 0xff, 0xbf };
            const unsigned char changedFirst[] =
                { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0x40 };
            const unsigned char changedLast[] =
                { 0, 0, 0, 0, 0, 0, 0, 0xb0, 0, 0xc0 };
            auto* last = static_cast<unsigned char*>(referenced)
                + (count - 1) * sizeof(expectedFirst);
            unsigned char firstValue[sizeof(expectedFirst)] = {};
            unsigned char lastValue[sizeof(expectedLast)] = {};
            SIZE_T copied = 0;
            return ReadProcessMemory(process, referenced, firstValue, sizeof(firstValue), &copied)
                && ReadProcessMemory(process, last, lastValue, sizeof(lastValue), &copied)
                && std::memcmp(firstValue, expectedFirst, sizeof(firstValue)) == 0
                && std::memcmp(lastValue, expectedLast, sizeof(lastValue)) == 0
                && WriteProcessMemory(process, referenced, changedFirst, sizeof(changedFirst), &copied)
                && WriteProcessMemory(process, last, changedLast, sizeof(changedLast), &copied);
        }, false, 2049);
}

static int InspectMixedStructureChildPointerCore(
    void* root, int length, int packed, int childLength, bool textFirst)
{
    return InspectMixedStructurePointer(root, length, packed, true,
        [childLength](HANDLE process, void* referenced)
        {
            if (childLength < 1 || childLength > 2048)
            {
                return false;
            }
            auto* bytes = static_cast<unsigned char*>(referenced);
            unsigned char first = 0;
            unsigned char last = 0;
            const unsigned char changed = 'Y';
            SIZE_T copied = 0;
            return ReadProcessMemory(process, bytes, &first, 1, &copied)
                && ReadProcessMemory(process, bytes + childLength - 1, &last, 1, &copied)
                && first == 'b' && last == 'b'
                && WriteProcessMemory(process, bytes + childLength - 1, &changed, 1, &copied);
        }, textFirst);
}

EXPORT int __stdcall InspectMixedStructureChildPointer(
    void* root, int length, int packed, int childLength)
{
    return InspectMixedStructureChildPointerCore(root, length, packed, childLength, false);
}

EXPORT int __stdcall InspectMixedStructureTrailingChild(
    void* root, int length, int packed, int childLength)
{
    return InspectMixedStructureChildPointerCore(root, length, packed, childLength, true);
}

static int InspectMixedStructureBytesCore(void* root, int length, int packed,
    bool wide, int firstAnsiBytes = -1, bool supplementary = false)
{
    if (length < (supplementary ? 4 : 1) || length > 2044)
    {
        return -1;
    }

    auto* bytes = static_cast<unsigned char*>(root);
    const int textOffset = packed ? 1 : 4;
    const int textEnd = textOffset + length;
    const int numberOffset = packed ? textEnd : (textEnd + 3) & ~3;
    SIZE_T copied = 0;
    unsigned char tag = 0;
    int number = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), bytes, &tag, sizeof(tag), &copied)
        || !ReadProcessMemory(GetCurrentProcess(), bytes + numberOffset,
            &number, sizeof(number), &copied) || tag != 7 || number != 19)
    {
        return -2;
    }

    if (wide || firstAnsiBytes >= 0)
    {
        InterlockedIncrement(&structureProbeCalls);
        unsigned char first[4] = {};
        unsigned char last = 0;
        const int expectedFirst = supplementary ? 0xd842 : wide ? 'a' : firstAnsiBytes;
        const int expectedLast = supplementary ? (length % 2 == 0 ? 0x65 : 0xe5)
            : (wide && length % 2 == 0 ? 0 : 'a');
        const int firstSize = supplementary ? 4 : 2;
        if (!ReadProcessMemory(GetCurrentProcess(), bytes + textOffset,
                first, firstSize, &copied)
            || !ReadProcessMemory(GetCurrentProcess(), bytes + textEnd - 1,
                &last, sizeof(last), &copied)
            || (first[0] | (first[1] << 8)) != expectedFirst || last != expectedLast
            || (supplementary && (first[2] | (first[3] << 8)) != 0xdfb7))
        {
            return -3;
        }

        const unsigned char replacement = 'Z';
        const int replacementOffset = wide ? textOffset + ((length - 1) & ~1) : textEnd - 1;
        if (!WriteProcessMemory(GetCurrentProcess(), bytes + replacementOffset,
                &replacement, sizeof(replacement), &copied))
        {
            return -3;
        }
    }
    else if (InspectStructureBytes(bytes + textOffset, length, 0) != length)
    {
        return -3;
    }

    tag = 9;
    number = 109;
    if (!WriteProcessMemory(GetCurrentProcess(), bytes, &tag, sizeof(tag), &copied)
        || !WriteProcessMemory(GetCurrentProcess(), bytes + numberOffset,
            &number, sizeof(number), &copied))
    {
        return -4;
    }
    return length;
}

EXPORT int __stdcall InspectMixedStructureBytes(void* root, int length, int packed)
{
    return InspectMixedStructureBytesCore(root, length, packed, false);
}

EXPORT int __stdcall InspectMixedStructureWideBytes(void* root, int length, int packed)
{
    return InspectMixedStructureBytesCore(root, length, packed, true);
}

EXPORT int __stdcall InspectMixedStructureSupplementaryBytes(
    void* root, int length, int packed)
{
    return InspectMixedStructureBytesCore(root, length, packed, true, -1, true);
}

EXPORT int __stdcall InspectMixedStructureAnsiBytes(
    void* root, int length, int packed, int firstAnsiBytes)
{
    return InspectMixedStructureBytesCore(root, length, packed, false, firstAnsiBytes);
}

EXPORT int __stdcall InspectMixedStructureTextTail(
    void* root, int length, int packed, int hasNullTail)
{
    const int result = InspectMixedStructureBytesCore(root, length, packed, false);
    if (result != length || !hasNullTail)
    {
        return result;
    }

    const int textEnd = (packed ? 1 : 4) + length;
    const int numberOffset = packed ? textEnd : (textEnd + 3) & ~3;
    const auto* tail = static_cast<const unsigned char*>(root) + numberOffset + sizeof(int);
    int nullValue = -1;
    SIZE_T copied = 0;
    return ReadProcessMemory(GetCurrentProcess(), tail, &nullValue,
        sizeof(nullValue), &copied) && nullValue == 0 ? result : -5;
}

static volatile LONG argumentProbeCalls = 0;
static int argumentElement = 3;
static volatile LONG nullableReferenceState = -1;
static volatile LONG scalarArrayState = -1;
static double scalarArrayValue = 0;

EXPORT int __stdcall ArgumentProbeCallCount()
{
    return static_cast<int>(InterlockedCompareExchange(&argumentProbeCalls, 0, 0));
}

EXPORT void __stdcall SetArgumentElement(int kind)
{
    argumentElement = kind;
    InterlockedExchange(&scalarArrayState, -1);
    scalarArrayValue = 0;
}

template<typename T>
bool ReadOrWriteArgumentNumber(void* pointer, double& number, bool write)
{
    SIZE_T copied = 0;
    T value = static_cast<T>(number);
    if (write)
    {
        return WriteProcessMemory(GetCurrentProcess(), pointer, &value, sizeof(value), &copied) != 0;
    }
    if (!ReadProcessMemory(GetCurrentProcess(), pointer, &value, sizeof(value), &copied))
    {
        return false;
    }
    number = static_cast<double>(value);
    return true;
}

bool ArgumentNumber(void* pointer, double& number, bool write)
{
    switch (argumentElement)
    {
    case 1: return ReadOrWriteArgumentNumber<std::uint8_t>(pointer, number, write);
    case 2: return ReadOrWriteArgumentNumber<std::uint16_t>(pointer, number, write);
    case 3: return ReadOrWriteArgumentNumber<std::int32_t>(pointer, number, write);
    case 4: return ReadOrWriteArgumentNumber<std::uint32_t>(pointer, number, write);
    case 5: return ReadOrWriteArgumentNumber<std::int64_t>(pointer, number, write);
    case 6: return ReadOrWriteArgumentNumber<float>(pointer, number, write);
    case 7: return ReadOrWriteArgumentNumber<double>(pointer, number, write);
    default: return false;
    }
}

EXPORT int __stdcall ObserveTypedReferencePair(void* first, void* second)
{
    InterlockedIncrement(&argumentProbeCalls);
    double firstValue = 0;
    double secondValue = 0;
    if (!ArgumentNumber(first, firstValue, false)
        || !ArgumentNumber(second, secondValue, false))
    {
        return -1;
    }
    if ((firstValue != 3 && firstValue != 5)
        || (secondValue != 3 && secondValue != 5))
    {
        return -2;
    }
    firstValue += 10;
    if (!ArgumentNumber(first, firstValue, true))
    {
        return -3;
    }
    double observed = 0;
    if (!ArgumentNumber(second, observed, false)
        || (observed != 3 && observed != 5 && observed != 13 && observed != 15))
    {
        return -4;
    }
    double updated = observed + 20;
    if (!ArgumentNumber(second, updated, true))
    {
        return -5;
    }
    return static_cast<int>(observed) + (first == second ? 1000 : 0);
}

// ポインターの前後の整数を検査し、通常配置とPACKを区別します。
static int ReadTypedReferenceFields(void* root, int packed, void* (&pointers)[2])
{
    std::size_t offset = 0;
    for (int index = 0; index < 5; ++index)
    {
        bool isPointer = index % 2 != 0;
        std::size_t size = isPointer ? sizeof(void*) : sizeof(int);
        std::size_t alignment = packed != 0 ? 1 : size;
        offset = (offset + alignment - 1) / alignment * alignment;
        SIZE_T copied = 0;
        const void* source = static_cast<const std::uint8_t*>(root) + offset;
        if (isPointer)
        {
            if (!ReadProcessMemory(GetCurrentProcess(), source,
                &pointers[index / 2], size, &copied) || copied != size)
            {
                return -6;
            }
        }
        else
        {
            int number = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), source,
                &number, size, &copied) || copied != size || number != 10 + index)
            {
                return -7;
            }
        }
        offset += size;
    }
    return 0;
}

EXPORT int __stdcall ObserveTypedReferenceFields(void* root, int packed)
{
    void* pointers[2] = {};
    const int result = ReadTypedReferenceFields(root, packed, pointers);
    return result == 0 ? ObserveTypedReferencePair(pointers[0], pointers[1]) : result;
}

EXPORT int __stdcall ObserveMixedReferenceFields(void* root, int packed, int doubleFirst)
{
    void* pointers[2] = {};
    const int result = ReadTypedReferenceFields(root, packed, pointers);
    if (result != 0)
    {
        return result;
    }

    InterlockedIncrement(&argumentProbeCalls);
    void* integer = pointers[doubleFirst ? 1 : 0];
    void* floating = pointers[doubleFirst ? 0 : 1];
    double integerValue = 0;
    double floatingValue = 0;
    if (!ReadOrWriteArgumentNumber<std::int32_t>(integer, integerValue, false)
        || !ReadOrWriteArgumentNumber<double>(floating, floatingValue, false)
        || integerValue != (doubleFirst ? 5 : 3)
        || floatingValue != (doubleFirst ? 3.5 : 5.5))
    {
        return -1;
    }

    integerValue += doubleFirst ? 20 : 10;
    floatingValue += doubleFirst ? 10 : 20;
    if (!ReadOrWriteArgumentNumber<std::int32_t>(integer, integerValue, true)
        || !ReadOrWriteArgumentNumber<double>(floating, floatingValue, true))
    {
        return -2;
    }
    return 5;
}

EXPORT int __stdcall ScalarArrayProbeState()
{
    return static_cast<int>(InterlockedCompareExchange(&scalarArrayState, 0, 0));
}

EXPORT double __stdcall ScalarArrayProbeValue()
{
    return scalarArrayValue;
}

// 単体・配列の先頭1要素だけを読み書きし、NULLや無効な領域には書き込みません。
int UpdateScalarArray(void* pointer)
{
    if (pointer == nullptr)
    {
        InterlockedExchange(&scalarArrayState, 0);
        return -1;
    }
    InterlockedExchange(&scalarArrayState, 1);
    double number = 0;
    if (!ArgumentNumber(pointer, number, false))
    {
        return -2;
    }
    scalarArrayValue = number;
    InterlockedExchangeAdd(&scalarArrayState, 16);
    number += 10;
    if (!ArgumentNumber(pointer, number, true))
    {
        return -3;
    }
    InterlockedExchangeAdd(&scalarArrayState, 256);
    return static_cast<int>(scalarArrayValue);
}

EXPORT int __stdcall InspectScalarArray(void* pointer)
{
    InterlockedIncrement(&argumentProbeCalls);
    return UpdateScalarArray(pointer);
}

EXPORT int __stdcall InspectScalarArrayField(void* root)
{
    InterlockedIncrement(&argumentProbeCalls);
    void* pointer = nullptr;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), root, &pointer, sizeof(pointer), &copied))
    {
        return -4;
    }
    return UpdateScalarArray(pointer);
}

// 入力が大きな値でも加算や整数化をせず、固定の安全な値だけを書きます。
int UpdateScalarArrayValue(void* pointer)
{
    if (pointer == nullptr)
    {
        InterlockedExchange(&scalarArrayState, 0);
        return -1;
    }
    InterlockedExchange(&scalarArrayState, 1);
    double number = 0;
    if (!ArgumentNumber(pointer, number, false))
    {
        return -2;
    }
    scalarArrayValue = number;
    InterlockedExchangeAdd(&scalarArrayState, 16);
    number = 21;
    if (!ArgumentNumber(pointer, number, true))
    {
        return -3;
    }
    InterlockedExchangeAdd(&scalarArrayState, 256);
    return 42;
}

EXPORT int __stdcall InspectScalarArrayValue(void* pointer)
{
    InterlockedIncrement(&argumentProbeCalls);
    return UpdateScalarArrayValue(pointer);
}

EXPORT int __stdcall InspectScalarArrayValueField(void* root)
{
    InterlockedIncrement(&argumentProbeCalls);
    void* pointer = nullptr;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), root, &pointer, sizeof(pointer), &copied))
    {
        return -4;
    }
    return UpdateScalarArrayValue(pointer);
}

EXPORT int __stdcall InspectArrayOrderFirst(void* pointer, int second, int third)
{
    InterlockedIncrement(&argumentProbeCalls);
    return second == 11 && third == 12 ? UpdateScalarArrayValue(pointer) : -5;
}

EXPORT int __stdcall InspectArrayOrderMiddle(int first, void* pointer, int third)
{
    InterlockedIncrement(&argumentProbeCalls);
    return first == 10 && third == 12 ? UpdateScalarArrayValue(pointer) : -5;
}

EXPORT int __stdcall InspectArrayOrderLast(int first, int second, void* pointer)
{
    InterlockedIncrement(&argumentProbeCalls);
    return first == 10 && second == 11 ? UpdateScalarArrayValue(pointer) : -5;
}

// 型幅とPACKに応じて1つの配列ポインターと前後の整数を読みます。
EXPORT int __stdcall InspectArrayOrderFields(void* root, int position, int packed)
{
    InterlockedIncrement(&argumentProbeCalls);
    if (position < 0 || position > 2)
    {
        return -6;
    }
    std::size_t offset = 0;
    void* pointer = nullptr;
    for (int index = 0; index < 3; ++index)
    {
        std::size_t size = index == position ? sizeof(pointer) : sizeof(int);
        std::size_t alignment = packed != 0 ? 1 : size;
        offset = (offset + alignment - 1) / alignment * alignment;
        SIZE_T copied = 0;
        void* source = static_cast<std::uint8_t*>(root) + offset;
        if (index == position)
        {
            if (!ReadProcessMemory(GetCurrentProcess(), source, &pointer, sizeof(pointer), &copied))
            {
                return -4;
            }
        }
        else
        {
            int number = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), source, &number, sizeof(number), &copied))
            {
                return -4;
            }
            if (number != 10 + index)
            {
                return -5;
            }
        }
        offset += size;
    }
    return UpdateScalarArrayValue(pointer);
}

EXPORT int __stdcall NullableReferenceProbeState()
{
    return static_cast<int>(InterlockedCompareExchange(&nullableReferenceState, 0, 0));
}

// NULLの位置を検査し、非NULLの数値領域だけへ書き込みます。
int UpdateNullableReferences(void* const* pointers, int nullMask)
{
    int observedMask = 0;
    for (int index = 0; index < 3; ++index)
    {
        if (pointers[index] == nullptr)
        {
            observedMask |= 1 << index;
        }
    }
    InterlockedExchange(&nullableReferenceState, observedMask);
    for (int index = 0; index < 3; ++index)
    {
        if ((pointers[index] == nullptr) != ((nullMask & (1 << index)) != 0))
        {
            return -30;
        }
        if (pointers[index] != nullptr)
        {
            double number = 0;
            if (!ArgumentNumber(pointers[index], number, false) || number != index + 1)
            {
                return -31;
            }
        }
    }
    for (int index = 0; index < 3; ++index)
    {
        if (pointers[index] != nullptr)
        {
            double number = index + 101;
            if (!ArgumentNumber(pointers[index], number, true))
            {
                return -32;
            }
            InterlockedExchangeAdd(&nullableReferenceState, 16);
        }
    }
    InterlockedExchangeAdd(&nullableReferenceState, 256);
    return 42;
}

EXPORT int __stdcall InspectNullableReferences(void* first, void* second, void* third, int nullMask)
{
    InterlockedIncrement(&argumentProbeCalls);
    void* pointers[] = {first, second, third};
    return UpdateNullableReferences(pointers, nullMask);
}

EXPORT int __stdcall InspectNullableReferenceFields(void* root, int nullMask)
{
    InterlockedIncrement(&argumentProbeCalls);
    InterlockedExchange(&nullableReferenceState, -1);
    void* pointers[3] = {};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), root, pointers, sizeof(pointers), &copied))
    {
        return -33;
    }
    return UpdateNullableReferences(pointers, nullMask);
}

// 各値を照合してから、参照領域だけに変更を加えます。
int InspectArgumentSlots(const std::uintptr_t* slots, int count, int mode)
{
#if defined(_M_IX86)
    if ((argumentElement == 8 || argumentElement == 9) && mode == 2)
    {
        for (int index = 0; index < count - 1; ++index)
        {
            if (slots[index] != static_cast<std::uintptr_t>(index + 1))
            {
                return -22;
            }
        }

        auto* tail = reinterpret_cast<void*>(slots[count - 1]);
        return argumentElement == 8 ? UpdateExtendedArray(tail, 2)
            : UpdateExtendedReference(tail, 1);
    }
#endif
    // NULL境界の観測では、先行ポインターと末尾のNULLだけを検査します。
    if (argumentElement == 0)
    {
        for (int index = 0; index < count - 1; ++index)
        {
            if (slots[index] == 0)
            {
                return -20;
            }
        }
        return slots[count - 1] == 0 ? count : -21;
    }
    int total = 0;
    SIZE_T copied = 0;
    for (int index = 0; index < count; ++index)
    {
        bool text = mode == 3 || (mode == 4 && index == count - 1);
        bool reference = mode == 1 || (mode == 2 && index == count - 1);
        if (text)
        {
            unsigned char value[3] = {};
            if (!ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(slots[index]), value, 3, &copied)
                || std::memcmp(value, "abc", 3) != 0)
            {
                return -1;
            }
            ++total;
        }
        else
        {
            double value = static_cast<int>(slots[index]);
            if (reference && !ArgumentNumber(reinterpret_cast<void*>(slots[index]), value, false))
            {
                return -2;
            }
            if (value != index + 1)
            {
                return -3;
            }
            total += static_cast<int>(value);
        }
    }
    for (int index = 0; index < count; ++index)
    {
        bool text = mode == 3 || (mode == 4 && index == count - 1);
        bool reference = mode == 1 || (mode == 2 && index == count - 1);
        if (text)
        {
            unsigned char value = 'Z';
            if (!WriteProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(slots[index]), &value, 1, &copied))
            {
                return -4;
            }
        }
        else if (reference)
        {
            double value = index + 101;
            if (!ArgumentNumber(reinterpret_cast<void*>(slots[index]), value, true))
            {
                return -5;
            }
        }
    }
    return total;
}

// 構造体のフィールド数と、スクリプト引数の位置を外側引数から切り分けます。
EXPORT int __stdcall InspectArgumentFields(void* root, int count, int kind)
{
    InterlockedIncrement(&argumentProbeCalls);
    if (count < 1 || count > 64)
    {
        return -10;
    }
    std::uintptr_t slots[64] = {};
    SIZE_T copied = 0;
    for (int index = 0; index < count; ++index)
    {
        auto bytes = static_cast<unsigned char*>(root);
        if (kind == 0)
        {
            int value = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), bytes + index * 4, &value, 4, &copied))
            {
                return -11;
            }
            slots[index] = static_cast<std::uintptr_t>(value);
        }
        else if (kind == 4)
        {
            slots[index] = reinterpret_cast<std::uintptr_t>(bytes + index * 4);
        }
        else if (!ReadProcessMemory(GetCurrentProcess(), bytes + index * sizeof(void*), slots + index, sizeof(void*), &copied))
        {
            return -12;
        }
    }
    int result = InspectArgumentSlots(slots, count, kind == 4 ? 3 : (kind == 2 ? 1 : kind == 5 ? 2 : kind));
    if (kind == 0 && result > 0)
    {
        for (int index = 0; index < count; ++index)
        {
            int value = index + 101;
            if (!WriteProcessMemory(GetCurrentProcess(), static_cast<unsigned char*>(root) + index * 4, &value, 4, &copied))
            {
                return -13;
            }
        }
    }
    return result;
}

EXPORT int __stdcall InspectReferenceArgs20(
    void* a0, void* a1, void* a2, void* a3, void* a4,
    void* a5, void* a6, void* a7, void* a8, void* a9,
    void* a10, void* a11, void* a12, void* a13, void* a14,
    void* a15, void* a16, void* a17, void* a18, void* a19)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19)
    };
    return InspectArgumentSlots(slots, 20, 1);
}

EXPORT int __stdcall InspectTailReferenceArgs20(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, void* a19)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19)
    };
    return InspectArgumentSlots(slots, 20, 2);
}

EXPORT int __stdcall InspectReferenceArgs21(
    void* a0, void* a1, void* a2, void* a3, void* a4,
    void* a5, void* a6, void* a7, void* a8, void* a9,
    void* a10, void* a11, void* a12, void* a13, void* a14,
    void* a15, void* a16, void* a17, void* a18, void* a19,
    void* a20)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20)
    };
    return InspectArgumentSlots(slots, 21, 1);
}

EXPORT int __stdcall InspectTailReferenceArgs21(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    void* a20)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20)
    };
    return InspectArgumentSlots(slots, 21, 2);
}

EXPORT int __stdcall InspectValueArgs40(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, int a39)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        static_cast<std::uintptr_t>(a39)
    };
    return InspectArgumentSlots(slots, 40, 0);
}

EXPORT int __stdcall InspectReferenceArgs40(
    void* a0, void* a1, void* a2, void* a3, void* a4,
    void* a5, void* a6, void* a7, void* a8, void* a9,
    void* a10, void* a11, void* a12, void* a13, void* a14,
    void* a15, void* a16, void* a17, void* a18, void* a19,
    void* a20, void* a21, void* a22, void* a23, void* a24,
    void* a25, void* a26, void* a27, void* a28, void* a29,
    void* a30, void* a31, void* a32, void* a33, void* a34,
    void* a35, void* a36, void* a37, void* a38, void* a39)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20),
        reinterpret_cast<std::uintptr_t>(a21), reinterpret_cast<std::uintptr_t>(a22), reinterpret_cast<std::uintptr_t>(a23),
        reinterpret_cast<std::uintptr_t>(a24), reinterpret_cast<std::uintptr_t>(a25), reinterpret_cast<std::uintptr_t>(a26),
        reinterpret_cast<std::uintptr_t>(a27), reinterpret_cast<std::uintptr_t>(a28), reinterpret_cast<std::uintptr_t>(a29),
        reinterpret_cast<std::uintptr_t>(a30), reinterpret_cast<std::uintptr_t>(a31), reinterpret_cast<std::uintptr_t>(a32),
        reinterpret_cast<std::uintptr_t>(a33), reinterpret_cast<std::uintptr_t>(a34), reinterpret_cast<std::uintptr_t>(a35),
        reinterpret_cast<std::uintptr_t>(a36), reinterpret_cast<std::uintptr_t>(a37), reinterpret_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39)
    };
    return InspectArgumentSlots(slots, 40, 1);
}

EXPORT int __stdcall InspectTextArgs40(
    char* a0, char* a1, char* a2, char* a3, char* a4,
    char* a5, char* a6, char* a7, char* a8, char* a9,
    char* a10, char* a11, char* a12, char* a13, char* a14,
    char* a15, char* a16, char* a17, char* a18, char* a19,
    char* a20, char* a21, char* a22, char* a23, char* a24,
    char* a25, char* a26, char* a27, char* a28, char* a29,
    char* a30, char* a31, char* a32, char* a33, char* a34,
    char* a35, char* a36, char* a37, char* a38, char* a39)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20),
        reinterpret_cast<std::uintptr_t>(a21), reinterpret_cast<std::uintptr_t>(a22), reinterpret_cast<std::uintptr_t>(a23),
        reinterpret_cast<std::uintptr_t>(a24), reinterpret_cast<std::uintptr_t>(a25), reinterpret_cast<std::uintptr_t>(a26),
        reinterpret_cast<std::uintptr_t>(a27), reinterpret_cast<std::uintptr_t>(a28), reinterpret_cast<std::uintptr_t>(a29),
        reinterpret_cast<std::uintptr_t>(a30), reinterpret_cast<std::uintptr_t>(a31), reinterpret_cast<std::uintptr_t>(a32),
        reinterpret_cast<std::uintptr_t>(a33), reinterpret_cast<std::uintptr_t>(a34), reinterpret_cast<std::uintptr_t>(a35),
        reinterpret_cast<std::uintptr_t>(a36), reinterpret_cast<std::uintptr_t>(a37), reinterpret_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39)
    };
    return InspectArgumentSlots(slots, 40, 3);
}

EXPORT int __stdcall InspectTailReferenceArgs40(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, void* a39)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39)
    };
    return InspectArgumentSlots(slots, 40, 2);
}

EXPORT int __stdcall InspectTailTextArgs40(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, char* a39)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39)
    };
    return InspectArgumentSlots(slots, 40, 4);
}

EXPORT int __stdcall InspectValueArgs41(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, int a39,
    int a40)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        static_cast<std::uintptr_t>(a39), static_cast<std::uintptr_t>(a40)
    };
    return InspectArgumentSlots(slots, 41, 0);
}

EXPORT int __stdcall InspectReferenceArgs41(
    void* a0, void* a1, void* a2, void* a3, void* a4,
    void* a5, void* a6, void* a7, void* a8, void* a9,
    void* a10, void* a11, void* a12, void* a13, void* a14,
    void* a15, void* a16, void* a17, void* a18, void* a19,
    void* a20, void* a21, void* a22, void* a23, void* a24,
    void* a25, void* a26, void* a27, void* a28, void* a29,
    void* a30, void* a31, void* a32, void* a33, void* a34,
    void* a35, void* a36, void* a37, void* a38, void* a39,
    void* a40)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20),
        reinterpret_cast<std::uintptr_t>(a21), reinterpret_cast<std::uintptr_t>(a22), reinterpret_cast<std::uintptr_t>(a23),
        reinterpret_cast<std::uintptr_t>(a24), reinterpret_cast<std::uintptr_t>(a25), reinterpret_cast<std::uintptr_t>(a26),
        reinterpret_cast<std::uintptr_t>(a27), reinterpret_cast<std::uintptr_t>(a28), reinterpret_cast<std::uintptr_t>(a29),
        reinterpret_cast<std::uintptr_t>(a30), reinterpret_cast<std::uintptr_t>(a31), reinterpret_cast<std::uintptr_t>(a32),
        reinterpret_cast<std::uintptr_t>(a33), reinterpret_cast<std::uintptr_t>(a34), reinterpret_cast<std::uintptr_t>(a35),
        reinterpret_cast<std::uintptr_t>(a36), reinterpret_cast<std::uintptr_t>(a37), reinterpret_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39), reinterpret_cast<std::uintptr_t>(a40)
    };
    return InspectArgumentSlots(slots, 41, 1);
}

EXPORT int __stdcall InspectTextArgs41(
    char* a0, char* a1, char* a2, char* a3, char* a4,
    char* a5, char* a6, char* a7, char* a8, char* a9,
    char* a10, char* a11, char* a12, char* a13, char* a14,
    char* a15, char* a16, char* a17, char* a18, char* a19,
    char* a20, char* a21, char* a22, char* a23, char* a24,
    char* a25, char* a26, char* a27, char* a28, char* a29,
    char* a30, char* a31, char* a32, char* a33, char* a34,
    char* a35, char* a36, char* a37, char* a38, char* a39,
    char* a40)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        reinterpret_cast<std::uintptr_t>(a0), reinterpret_cast<std::uintptr_t>(a1), reinterpret_cast<std::uintptr_t>(a2),
        reinterpret_cast<std::uintptr_t>(a3), reinterpret_cast<std::uintptr_t>(a4), reinterpret_cast<std::uintptr_t>(a5),
        reinterpret_cast<std::uintptr_t>(a6), reinterpret_cast<std::uintptr_t>(a7), reinterpret_cast<std::uintptr_t>(a8),
        reinterpret_cast<std::uintptr_t>(a9), reinterpret_cast<std::uintptr_t>(a10), reinterpret_cast<std::uintptr_t>(a11),
        reinterpret_cast<std::uintptr_t>(a12), reinterpret_cast<std::uintptr_t>(a13), reinterpret_cast<std::uintptr_t>(a14),
        reinterpret_cast<std::uintptr_t>(a15), reinterpret_cast<std::uintptr_t>(a16), reinterpret_cast<std::uintptr_t>(a17),
        reinterpret_cast<std::uintptr_t>(a18), reinterpret_cast<std::uintptr_t>(a19), reinterpret_cast<std::uintptr_t>(a20),
        reinterpret_cast<std::uintptr_t>(a21), reinterpret_cast<std::uintptr_t>(a22), reinterpret_cast<std::uintptr_t>(a23),
        reinterpret_cast<std::uintptr_t>(a24), reinterpret_cast<std::uintptr_t>(a25), reinterpret_cast<std::uintptr_t>(a26),
        reinterpret_cast<std::uintptr_t>(a27), reinterpret_cast<std::uintptr_t>(a28), reinterpret_cast<std::uintptr_t>(a29),
        reinterpret_cast<std::uintptr_t>(a30), reinterpret_cast<std::uintptr_t>(a31), reinterpret_cast<std::uintptr_t>(a32),
        reinterpret_cast<std::uintptr_t>(a33), reinterpret_cast<std::uintptr_t>(a34), reinterpret_cast<std::uintptr_t>(a35),
        reinterpret_cast<std::uintptr_t>(a36), reinterpret_cast<std::uintptr_t>(a37), reinterpret_cast<std::uintptr_t>(a38),
        reinterpret_cast<std::uintptr_t>(a39), reinterpret_cast<std::uintptr_t>(a40)
    };
    return InspectArgumentSlots(slots, 41, 3);
}

EXPORT int __stdcall InspectTailReferenceArgs41(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, int a39,
    void* a40)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        static_cast<std::uintptr_t>(a39), reinterpret_cast<std::uintptr_t>(a40)
    };
    return InspectArgumentSlots(slots, 41, 2);
}

EXPORT int __stdcall InspectTailTextArgs41(
    int a0, int a1, int a2, int a3, int a4,
    int a5, int a6, int a7, int a8, int a9,
    int a10, int a11, int a12, int a13, int a14,
    int a15, int a16, int a17, int a18, int a19,
    int a20, int a21, int a22, int a23, int a24,
    int a25, int a26, int a27, int a28, int a29,
    int a30, int a31, int a32, int a33, int a34,
    int a35, int a36, int a37, int a38, int a39,
    char* a40)
{
    InterlockedIncrement(&argumentProbeCalls);
    std::uintptr_t slots[] = {
        static_cast<std::uintptr_t>(a0), static_cast<std::uintptr_t>(a1), static_cast<std::uintptr_t>(a2),
        static_cast<std::uintptr_t>(a3), static_cast<std::uintptr_t>(a4), static_cast<std::uintptr_t>(a5),
        static_cast<std::uintptr_t>(a6), static_cast<std::uintptr_t>(a7), static_cast<std::uintptr_t>(a8),
        static_cast<std::uintptr_t>(a9), static_cast<std::uintptr_t>(a10), static_cast<std::uintptr_t>(a11),
        static_cast<std::uintptr_t>(a12), static_cast<std::uintptr_t>(a13), static_cast<std::uintptr_t>(a14),
        static_cast<std::uintptr_t>(a15), static_cast<std::uintptr_t>(a16), static_cast<std::uintptr_t>(a17),
        static_cast<std::uintptr_t>(a18), static_cast<std::uintptr_t>(a19), static_cast<std::uintptr_t>(a20),
        static_cast<std::uintptr_t>(a21), static_cast<std::uintptr_t>(a22), static_cast<std::uintptr_t>(a23),
        static_cast<std::uintptr_t>(a24), static_cast<std::uintptr_t>(a25), static_cast<std::uintptr_t>(a26),
        static_cast<std::uintptr_t>(a27), static_cast<std::uintptr_t>(a28), static_cast<std::uintptr_t>(a29),
        static_cast<std::uintptr_t>(a30), static_cast<std::uintptr_t>(a31), static_cast<std::uintptr_t>(a32),
        static_cast<std::uintptr_t>(a33), static_cast<std::uintptr_t>(a34), static_cast<std::uintptr_t>(a35),
        static_cast<std::uintptr_t>(a36), static_cast<std::uintptr_t>(a37), static_cast<std::uintptr_t>(a38),
        static_cast<std::uintptr_t>(a39), reinterpret_cast<std::uintptr_t>(a40)
    };
    return InspectArgumentSlots(slots, 41, 4);
}

static volatile LONG argumentBlockCalls = 0;

EXPORT int __stdcall ArgumentBlockCallCount()
{
    return static_cast<int>(InterlockedCompareExchange(&argumentBlockCalls, 0, 0));
}

// 引数ごとの値を検査し、容量の境界で欠落や並替えがないことを確認します。
int InspectArgumentBlockValues(const double* values, int count, double fraction)
{
    for (int index = 0; index < count; ++index)
    {
        if (values[index] != index % 251 + 1 + fraction)
        {
            return -1;
        }
    }
    return count;
}

#define BLOCK_INDICES_256(X) \
    X(0), X(1), X(2), X(3), X(4), X(5), X(6), X(7), \
    X(8), X(9), X(10), X(11), X(12), X(13), X(14), X(15), \
    X(16), X(17), X(18), X(19), X(20), X(21), X(22), X(23), \
    X(24), X(25), X(26), X(27), X(28), X(29), X(30), X(31), \
    X(32), X(33), X(34), X(35), X(36), X(37), X(38), X(39), \
    X(40), X(41), X(42), X(43), X(44), X(45), X(46), X(47), \
    X(48), X(49), X(50), X(51), X(52), X(53), X(54), X(55), \
    X(56), X(57), X(58), X(59), X(60), X(61), X(62), X(63), \
    X(64), X(65), X(66), X(67), X(68), X(69), X(70), X(71), \
    X(72), X(73), X(74), X(75), X(76), X(77), X(78), X(79), \
    X(80), X(81), X(82), X(83), X(84), X(85), X(86), X(87), \
    X(88), X(89), X(90), X(91), X(92), X(93), X(94), X(95), \
    X(96), X(97), X(98), X(99), X(100), X(101), X(102), X(103), \
    X(104), X(105), X(106), X(107), X(108), X(109), X(110), X(111), \
    X(112), X(113), X(114), X(115), X(116), X(117), X(118), X(119), \
    X(120), X(121), X(122), X(123), X(124), X(125), X(126), X(127), \
    X(128), X(129), X(130), X(131), X(132), X(133), X(134), X(135), \
    X(136), X(137), X(138), X(139), X(140), X(141), X(142), X(143), \
    X(144), X(145), X(146), X(147), X(148), X(149), X(150), X(151), \
    X(152), X(153), X(154), X(155), X(156), X(157), X(158), X(159), \
    X(160), X(161), X(162), X(163), X(164), X(165), X(166), X(167), \
    X(168), X(169), X(170), X(171), X(172), X(173), X(174), X(175), \
    X(176), X(177), X(178), X(179), X(180), X(181), X(182), X(183), \
    X(184), X(185), X(186), X(187), X(188), X(189), X(190), X(191), \
    X(192), X(193), X(194), X(195), X(196), X(197), X(198), X(199), \
    X(200), X(201), X(202), X(203), X(204), X(205), X(206), X(207), \
    X(208), X(209), X(210), X(211), X(212), X(213), X(214), X(215), \
    X(216), X(217), X(218), X(219), X(220), X(221), X(222), X(223), \
    X(224), X(225), X(226), X(227), X(228), X(229), X(230), X(231), \
    X(232), X(233), X(234), X(235), X(236), X(237), X(238), X(239), \
    X(240), X(241), X(242), X(243), X(244), X(245), X(246), X(247), \
    X(248), X(249), X(250), X(251), X(252), X(253), X(254), X(255)
#define BLOCK_INDICES_257(X) BLOCK_INDICES_256(X), X(256)

#define BLOCK_INDICES_512(X) \
    X(0), X(1), X(2), X(3), X(4), X(5), X(6), X(7), \
    X(8), X(9), X(10), X(11), X(12), X(13), X(14), X(15), \
    X(16), X(17), X(18), X(19), X(20), X(21), X(22), X(23), \
    X(24), X(25), X(26), X(27), X(28), X(29), X(30), X(31), \
    X(32), X(33), X(34), X(35), X(36), X(37), X(38), X(39), \
    X(40), X(41), X(42), X(43), X(44), X(45), X(46), X(47), \
    X(48), X(49), X(50), X(51), X(52), X(53), X(54), X(55), \
    X(56), X(57), X(58), X(59), X(60), X(61), X(62), X(63), \
    X(64), X(65), X(66), X(67), X(68), X(69), X(70), X(71), \
    X(72), X(73), X(74), X(75), X(76), X(77), X(78), X(79), \
    X(80), X(81), X(82), X(83), X(84), X(85), X(86), X(87), \
    X(88), X(89), X(90), X(91), X(92), X(93), X(94), X(95), \
    X(96), X(97), X(98), X(99), X(100), X(101), X(102), X(103), \
    X(104), X(105), X(106), X(107), X(108), X(109), X(110), X(111), \
    X(112), X(113), X(114), X(115), X(116), X(117), X(118), X(119), \
    X(120), X(121), X(122), X(123), X(124), X(125), X(126), X(127), \
    X(128), X(129), X(130), X(131), X(132), X(133), X(134), X(135), \
    X(136), X(137), X(138), X(139), X(140), X(141), X(142), X(143), \
    X(144), X(145), X(146), X(147), X(148), X(149), X(150), X(151), \
    X(152), X(153), X(154), X(155), X(156), X(157), X(158), X(159), \
    X(160), X(161), X(162), X(163), X(164), X(165), X(166), X(167), \
    X(168), X(169), X(170), X(171), X(172), X(173), X(174), X(175), \
    X(176), X(177), X(178), X(179), X(180), X(181), X(182), X(183), \
    X(184), X(185), X(186), X(187), X(188), X(189), X(190), X(191), \
    X(192), X(193), X(194), X(195), X(196), X(197), X(198), X(199), \
    X(200), X(201), X(202), X(203), X(204), X(205), X(206), X(207), \
    X(208), X(209), X(210), X(211), X(212), X(213), X(214), X(215), \
    X(216), X(217), X(218), X(219), X(220), X(221), X(222), X(223), \
    X(224), X(225), X(226), X(227), X(228), X(229), X(230), X(231), \
    X(232), X(233), X(234), X(235), X(236), X(237), X(238), X(239), \
    X(240), X(241), X(242), X(243), X(244), X(245), X(246), X(247), \
    X(248), X(249), X(250), X(251), X(252), X(253), X(254), X(255), \
    X(256), X(257), X(258), X(259), X(260), X(261), X(262), X(263), \
    X(264), X(265), X(266), X(267), X(268), X(269), X(270), X(271), \
    X(272), X(273), X(274), X(275), X(276), X(277), X(278), X(279), \
    X(280), X(281), X(282), X(283), X(284), X(285), X(286), X(287), \
    X(288), X(289), X(290), X(291), X(292), X(293), X(294), X(295), \
    X(296), X(297), X(298), X(299), X(300), X(301), X(302), X(303), \
    X(304), X(305), X(306), X(307), X(308), X(309), X(310), X(311), \
    X(312), X(313), X(314), X(315), X(316), X(317), X(318), X(319), \
    X(320), X(321), X(322), X(323), X(324), X(325), X(326), X(327), \
    X(328), X(329), X(330), X(331), X(332), X(333), X(334), X(335), \
    X(336), X(337), X(338), X(339), X(340), X(341), X(342), X(343), \
    X(344), X(345), X(346), X(347), X(348), X(349), X(350), X(351), \
    X(352), X(353), X(354), X(355), X(356), X(357), X(358), X(359), \
    X(360), X(361), X(362), X(363), X(364), X(365), X(366), X(367), \
    X(368), X(369), X(370), X(371), X(372), X(373), X(374), X(375), \
    X(376), X(377), X(378), X(379), X(380), X(381), X(382), X(383), \
    X(384), X(385), X(386), X(387), X(388), X(389), X(390), X(391), \
    X(392), X(393), X(394), X(395), X(396), X(397), X(398), X(399), \
    X(400), X(401), X(402), X(403), X(404), X(405), X(406), X(407), \
    X(408), X(409), X(410), X(411), X(412), X(413), X(414), X(415), \
    X(416), X(417), X(418), X(419), X(420), X(421), X(422), X(423), \
    X(424), X(425), X(426), X(427), X(428), X(429), X(430), X(431), \
    X(432), X(433), X(434), X(435), X(436), X(437), X(438), X(439), \
    X(440), X(441), X(442), X(443), X(444), X(445), X(446), X(447), \
    X(448), X(449), X(450), X(451), X(452), X(453), X(454), X(455), \
    X(456), X(457), X(458), X(459), X(460), X(461), X(462), X(463), \
    X(464), X(465), X(466), X(467), X(468), X(469), X(470), X(471), \
    X(472), X(473), X(474), X(475), X(476), X(477), X(478), X(479), \
    X(480), X(481), X(482), X(483), X(484), X(485), X(486), X(487), \
    X(488), X(489), X(490), X(491), X(492), X(493), X(494), X(495), \
    X(496), X(497), X(498), X(499), X(500), X(501), X(502), X(503), \
    X(504), X(505), X(506), X(507), X(508), X(509), X(510), X(511)
#define BLOCK_INDICES_513(X) BLOCK_INDICES_512(X), X(512)

#define BLOCK_INT_PARAMETER(i) int a##i
#define BLOCK_FLOAT_PARAMETER(i) float a##i
#define BLOCK_DOUBLE_PARAMETER(i) double a##i
#define BLOCK_LONGLONG_PARAMETER(i) std::int64_t a##i
#define BLOCK_BYTE_PARAMETER(i) std::uint8_t a##i
#define BLOCK_WORD_PARAMETER(i) std::uint16_t a##i
#define BLOCK_DOUBLE_VALUE(i) static_cast<double>(a##i)

EXPORT int __stdcall InspectBlockInt512(BLOCK_INDICES_512(BLOCK_INT_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_512(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 512, 0);
}

EXPORT int __stdcall InspectBlockInt513(BLOCK_INDICES_513(BLOCK_INT_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_513(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 513, 0);
}

EXPORT int __stdcall InspectBlockFloat512(BLOCK_INDICES_512(BLOCK_FLOAT_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_512(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 512, 0.25);
}

EXPORT int __stdcall InspectBlockFloat513(BLOCK_INDICES_513(BLOCK_FLOAT_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_513(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 513, 0.25);
}

EXPORT int __stdcall InspectBlockDouble256(BLOCK_INDICES_256(BLOCK_DOUBLE_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_256(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 256, 0.25);
}

EXPORT int __stdcall InspectBlockDouble257(BLOCK_INDICES_257(BLOCK_DOUBLE_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_257(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 257, 0.25);
}

EXPORT int __stdcall InspectBlockLongLong256(BLOCK_INDICES_256(BLOCK_LONGLONG_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_256(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 256, 0);
}

EXPORT int __stdcall InspectBlockLongLong257(BLOCK_INDICES_257(BLOCK_LONGLONG_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_257(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 257, 0);
}

EXPORT int __stdcall InspectBlockByte512(BLOCK_INDICES_512(BLOCK_BYTE_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_512(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 512, 0);
}

EXPORT int __stdcall InspectBlockByte513(BLOCK_INDICES_513(BLOCK_BYTE_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_513(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 513, 0);
}

EXPORT int __stdcall InspectBlockWord512(BLOCK_INDICES_512(BLOCK_WORD_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_512(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 512, 0);
}

EXPORT int __stdcall InspectBlockWord513(BLOCK_INDICES_513(BLOCK_WORD_PARAMETER))
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_513(BLOCK_DOUBLE_VALUE) };
    return InspectArgumentBlockValues(values, 513, 0);
}

#define BLOCK_INDICES_510(X) \
    X(0), X(1), X(2), X(3), X(4), X(5), X(6), X(7), \
    X(8), X(9), X(10), X(11), X(12), X(13), X(14), X(15), \
    X(16), X(17), X(18), X(19), X(20), X(21), X(22), X(23), \
    X(24), X(25), X(26), X(27), X(28), X(29), X(30), X(31), \
    X(32), X(33), X(34), X(35), X(36), X(37), X(38), X(39), \
    X(40), X(41), X(42), X(43), X(44), X(45), X(46), X(47), \
    X(48), X(49), X(50), X(51), X(52), X(53), X(54), X(55), \
    X(56), X(57), X(58), X(59), X(60), X(61), X(62), X(63), \
    X(64), X(65), X(66), X(67), X(68), X(69), X(70), X(71), \
    X(72), X(73), X(74), X(75), X(76), X(77), X(78), X(79), \
    X(80), X(81), X(82), X(83), X(84), X(85), X(86), X(87), \
    X(88), X(89), X(90), X(91), X(92), X(93), X(94), X(95), \
    X(96), X(97), X(98), X(99), X(100), X(101), X(102), X(103), \
    X(104), X(105), X(106), X(107), X(108), X(109), X(110), X(111), \
    X(112), X(113), X(114), X(115), X(116), X(117), X(118), X(119), \
    X(120), X(121), X(122), X(123), X(124), X(125), X(126), X(127), \
    X(128), X(129), X(130), X(131), X(132), X(133), X(134), X(135), \
    X(136), X(137), X(138), X(139), X(140), X(141), X(142), X(143), \
    X(144), X(145), X(146), X(147), X(148), X(149), X(150), X(151), \
    X(152), X(153), X(154), X(155), X(156), X(157), X(158), X(159), \
    X(160), X(161), X(162), X(163), X(164), X(165), X(166), X(167), \
    X(168), X(169), X(170), X(171), X(172), X(173), X(174), X(175), \
    X(176), X(177), X(178), X(179), X(180), X(181), X(182), X(183), \
    X(184), X(185), X(186), X(187), X(188), X(189), X(190), X(191), \
    X(192), X(193), X(194), X(195), X(196), X(197), X(198), X(199), \
    X(200), X(201), X(202), X(203), X(204), X(205), X(206), X(207), \
    X(208), X(209), X(210), X(211), X(212), X(213), X(214), X(215), \
    X(216), X(217), X(218), X(219), X(220), X(221), X(222), X(223), \
    X(224), X(225), X(226), X(227), X(228), X(229), X(230), X(231), \
    X(232), X(233), X(234), X(235), X(236), X(237), X(238), X(239), \
    X(240), X(241), X(242), X(243), X(244), X(245), X(246), X(247), \
    X(248), X(249), X(250), X(251), X(252), X(253), X(254), X(255), \
    X(256), X(257), X(258), X(259), X(260), X(261), X(262), X(263), \
    X(264), X(265), X(266), X(267), X(268), X(269), X(270), X(271), \
    X(272), X(273), X(274), X(275), X(276), X(277), X(278), X(279), \
    X(280), X(281), X(282), X(283), X(284), X(285), X(286), X(287), \
    X(288), X(289), X(290), X(291), X(292), X(293), X(294), X(295), \
    X(296), X(297), X(298), X(299), X(300), X(301), X(302), X(303), \
    X(304), X(305), X(306), X(307), X(308), X(309), X(310), X(311), \
    X(312), X(313), X(314), X(315), X(316), X(317), X(318), X(319), \
    X(320), X(321), X(322), X(323), X(324), X(325), X(326), X(327), \
    X(328), X(329), X(330), X(331), X(332), X(333), X(334), X(335), \
    X(336), X(337), X(338), X(339), X(340), X(341), X(342), X(343), \
    X(344), X(345), X(346), X(347), X(348), X(349), X(350), X(351), \
    X(352), X(353), X(354), X(355), X(356), X(357), X(358), X(359), \
    X(360), X(361), X(362), X(363), X(364), X(365), X(366), X(367), \
    X(368), X(369), X(370), X(371), X(372), X(373), X(374), X(375), \
    X(376), X(377), X(378), X(379), X(380), X(381), X(382), X(383), \
    X(384), X(385), X(386), X(387), X(388), X(389), X(390), X(391), \
    X(392), X(393), X(394), X(395), X(396), X(397), X(398), X(399), \
    X(400), X(401), X(402), X(403), X(404), X(405), X(406), X(407), \
    X(408), X(409), X(410), X(411), X(412), X(413), X(414), X(415), \
    X(416), X(417), X(418), X(419), X(420), X(421), X(422), X(423), \
    X(424), X(425), X(426), X(427), X(428), X(429), X(430), X(431), \
    X(432), X(433), X(434), X(435), X(436), X(437), X(438), X(439), \
    X(440), X(441), X(442), X(443), X(444), X(445), X(446), X(447), \
    X(448), X(449), X(450), X(451), X(452), X(453), X(454), X(455), \
    X(456), X(457), X(458), X(459), X(460), X(461), X(462), X(463), \
    X(464), X(465), X(466), X(467), X(468), X(469), X(470), X(471), \
    X(472), X(473), X(474), X(475), X(476), X(477), X(478), X(479), \
    X(480), X(481), X(482), X(483), X(484), X(485), X(486), X(487), \
    X(488), X(489), X(490), X(491), X(492), X(493), X(494), X(495), \
    X(496), X(497), X(498), X(499), X(500), X(501), X(502), X(503), \
    X(504), X(505), X(506), X(507), X(508), X(509)
#define BLOCK_INDICES_511(X) BLOCK_INDICES_510(X), X(510)

EXPORT int __stdcall InspectBlockTailDouble512(
    BLOCK_INDICES_511(BLOCK_INT_PARAMETER), double tail)
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_511(BLOCK_DOUBLE_VALUE) };
    if (InspectArgumentBlockValues(values, 511, 0) != 511)
    {
        return -1;
    }
    return tail == 511 % 251 + 1.25 ? 512 : -2;
}

EXPORT int __stdcall InspectBlockTailPointer512(
    BLOCK_INDICES_511(BLOCK_INT_PARAMETER), void* tail)
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_511(BLOCK_DOUBLE_VALUE) };
    if (InspectArgumentBlockValues(values, 511, 0) != 511)
    {
        return -1;
    }
    int value = 0;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), tail, &value, sizeof(value), &copied))
    {
        return -2;
    }
    return value == 511 % 251 + 1 ? 512 : -3;
}

EXPORT int __stdcall InspectBlockTailPointer513(
    BLOCK_INDICES_512(BLOCK_INT_PARAMETER), void* tail)
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_512(BLOCK_DOUBLE_VALUE) };
    if (InspectArgumentBlockValues(values, 512, 0) != 512)
    {
        return -1;
    }
    int value = 0;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), tail, &value, sizeof(value), &copied))
    {
        return -2;
    }
    return value == 512 % 251 + 1 ? 513 : -3;
}
EXPORT int __stdcall InspectBlockTailDouble511(
    BLOCK_INDICES_510(BLOCK_INT_PARAMETER), double tail)
{
    InterlockedIncrement(&argumentBlockCalls);
    double values[] = { BLOCK_INDICES_510(BLOCK_DOUBLE_VALUE) };
    if (InspectArgumentBlockValues(values, 510, 0) != 510)
    {
        return -1;
    }
    return tail == 510 % 251 + 1.25 ? 511 : -2;
}

#undef BLOCK_INT_PARAMETER
#undef BLOCK_FLOAT_PARAMETER
#undef BLOCK_DOUBLE_PARAMETER
#undef BLOCK_LONGLONG_PARAMETER
#undef BLOCK_BYTE_PARAMETER
#undef BLOCK_WORD_PARAMETER
#undef BLOCK_DOUBLE_VALUE
#undef BLOCK_INDICES_256
#undef BLOCK_INDICES_257
#undef BLOCK_INDICES_511
#undef BLOCK_INDICES_512
#undef BLOCK_INDICES_513
#undef BLOCK_INDICES_510

// 外側の引数数と構造体内のフィールド数を分けて検証します。
EXPORT int __stdcall InspectBlockFields(void* root, int count)
{
    InterlockedIncrement(&argumentBlockCalls);
    if (count < 1 || count > 512)
    {
        return -1;
    }
    double values[512] = {};
    SIZE_T copied = 0;
    for (int index = 0; index < count; ++index)
    {
        int value = 0;
        if (!ReadProcessMemory(GetCurrentProcess(),
            static_cast<int*>(root) + index, &value, sizeof(value), &copied))
        {
            return -2;
        }
        values[index] = value;
    }
    return InspectArgumentBlockValues(values, count, 0);
}

EXPORT int __stdcall InspectHandleText(std::uintptr_t value)
{
    if (value == 0)
    {
        return -1;
    }
    unsigned char bytes[3] = {};
    SIZE_T read = 0;
    const auto address = reinterpret_cast<const void*>(value);
    if (!ReadProcessMemory(GetCurrentProcess(), address, bytes, 1, &read))
    {
        return -2;
    }
    if (bytes[0] == 0)
    {
        return 3;
    }
    if (!ReadProcessMemory(GetCurrentProcess(), address, bytes, sizeof(bytes), &read))
    {
        return -2;
    }
    if (bytes[0] == 'A' && bytes[1] == 'B' && bytes[2] == 0)
    {
        return 1;
    }
    if (bytes[0] == 'A' && bytes[1] == 0 && bytes[2] == 'B')
    {
        return 2;
    }
    return bytes[0] == '4' && bytes[1] == '2' && bytes[2] == 0 ? 4 : 0;
}

// 検証用のハンドル領域を1要素だけ読み、書戻しを識別する値に置き換えます。
EXPORT int __stdcall InspectHandleTextStorage(std::uintptr_t* storage)
{
    std::uintptr_t value = 0;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), storage, &value, sizeof(value), &copied))
    {
        return -3;
    }
    const int observed = value == 42 ? 5 : InspectHandleText(value);
    constexpr std::uintptr_t replacement = 123;
    if (!WriteProcessMemory(GetCurrentProcess(), storage, &replacement,
        sizeof(replacement), &copied))
    {
        return -4;
    }
    return observed;
}

// 構造体に格納された参照先のハンドル領域を検査します。
EXPORT int __stdcall InspectHandleTextIndirectStorage(std::uintptr_t** field)
{
    std::uintptr_t* storage = nullptr;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), field, &storage, sizeof(storage), &copied))
    {
        return -5;
    }
    return InspectHandleTextStorage(storage);
}

// ハンドルの前後と子構造体の前後に別の値を置き、配置と書戻しを検査します。
struct HandleTextNatural
{
    std::uint8_t tag;
    std::uintptr_t value;
    std::uint16_t tail;
};

#pragma pack(push, 1)
struct HandleTextPacked
{
    std::uint8_t tag;
    std::uintptr_t value;
    std::uint16_t tail;
};
#pragma pack(pop)

template<typename Fields>
int InspectHandleTextFields(Fields* fields)
{
    if (fields->tag != 3 || fields->tail != 5)
    {
        return -6;
    }
    const int observed = InspectHandleText(fields->value);
    fields->tag = 7;
    fields->value = 123;
    fields->tail = 11;
    return observed;
}

template<typename Fields>
int InspectHandleTextRoot(void* storage, bool nested)
{
    if (!nested)
    {
        return InspectHandleTextFields(static_cast<Fields*>(storage));
    }
    auto* root = static_cast<StructureParent<Fields>*>(storage);
    if (root->tag != 2 || root->tail != 19 || root->child == nullptr)
    {
        return -7;
    }
    root->tag = 4;
    root->tail = 29;
    return InspectHandleTextFields(root->child);
}

EXPORT int __stdcall InspectHandleTextLayout(void* storage, int packed, int nested)
{
    return packed != 0 ? InspectHandleTextRoot<HandleTextPacked>(storage, nested != 0)
        : InspectHandleTextRoot<HandleTextNatural>(storage, nested != 0);
}

// WindowsのANSI変換結果と、DLLが受け取った文字列を終端まで比較します。
EXPORT int __stdcall InspectHandleAnsiCodePage(std::uintptr_t value)
{
    char expected[32] = {};
    const int length = WideCharToMultiByte(CP_ACP, 0, L"\u00e9\u65e5", -1,
        expected, static_cast<int>(sizeof(expected)), nullptr, nullptr);
    if (length == 0)
    {
        return -1;
    }
    char actual[32] = {};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(value),
        actual, static_cast<SIZE_T>(length), &copied)
        || copied != static_cast<SIZE_T>(length))
    {
        return -2;
    }
    return std::memcmp(expected, actual, static_cast<std::size_t>(length)) == 0 ? 1 : 0;
}

EXPORT int __stdcall InspectHandleAnsiCodePageField(std::uintptr_t* field)
{
    return InspectHandleAnsiCodePage(*field);
}
