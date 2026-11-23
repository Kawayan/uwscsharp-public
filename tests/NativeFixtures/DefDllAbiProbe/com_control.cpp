#include <atomic>
#include <cstdint>
#include <limits>
#include <new>
#include <windows.h>
#include <oaidl.h>
#include <ocidl.h>
#include <oleauto.h>
#include <olectl.h>

extern "C" std::uint32_t ReadX87Control();
extern "C" std::uint32_t ReadSseControl();
#if defined(_M_IX86)
extern "C" int __stdcall ProbeX87Divide(int* marker);
#else
extern "C" void RaiseDivide();
#endif

static constexpr GUID probeClass = {
    0x671d0356, 0x3b9a, 0x4bfa, {0x91, 0x9b, 0xe1, 0x72, 0x53, 0x21, 0x0d, 0xc8}
};

static constexpr GUID probeMethods = {
    0x41d408c1, 0x6277, 0x42b1, {0x9b, 0xa6, 0xb3, 0x72, 0x27, 0x8f, 0xf7, 0x64}
};

static constexpr GUID probeEvents = {
    0x7ac1bf69, 0x37b8, 0x4b31, {0xae, 0x66, 0xd2, 0x1e, 0x1b, 0x6f, 0xa4, 0x0d}
};

// 登録した検証用DLLのCOM呼出しとイベント前後で制御値を読みます。
class ControlProbe final : public IDispatch, public IProvideClassInfo,
    public IConnectionPointContainer, public IConnectionPoint
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        *result = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IDispatch)
            || iid == probeMethods)
        {
            *result = static_cast<IDispatch*>(this);
        }
        else if (iid == __uuidof(IProvideClassInfo))
        {
            *result = static_cast<IProvideClassInfo*>(this);
        }
        else if (iid == __uuidof(IConnectionPointContainer))
        {
            *result = static_cast<IConnectionPointContainer*>(this);
        }
        else if (iid == __uuidof(IConnectionPoint))
        {
            *result = static_cast<IConnectionPoint*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG count = --references;
        if (count == 0)
        {
            delete this;
        }
        return count;
    }

    HRESULT STDMETHODCALLTYPE GetClassInfo(ITypeInfo** info) override
    {
        return ReadTypeInfo(probeClass, info);
    }

    HRESULT STDMETHODCALLTYPE EnumConnectionPoints(IEnumConnectionPoints**) override
    {
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE FindConnectionPoint(REFIID iid,
        IConnectionPoint** point) override
    {
        *point = nullptr;
        if (iid != probeEvents)
        {
            return CONNECT_E_NOCONNECTION;
        }
        *point = static_cast<IConnectionPoint*>(this);
        AddRef();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetConnectionInterface(IID* iid) override
    {
        *iid = probeEvents;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetConnectionPointContainer(
        IConnectionPointContainer** container) override
    {
        *container = static_cast<IConnectionPointContainer*>(this);
        AddRef();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Advise(IUnknown* unknown, DWORD* cookie) override
    {
        *cookie = 0;
        if (sink != nullptr)
        {
            return CONNECT_E_ADVISELIMIT;
        }
        const HRESULT status = unknown->QueryInterface(probeEvents,
            reinterpret_cast<void**>(&sink));
        if (FAILED(status))
        {
            return CONNECT_E_CANNOTCONNECT;
        }
        *cookie = 1;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Unadvise(DWORD cookie) override
    {
        if (cookie != 1 || sink == nullptr)
        {
            return CONNECT_E_NOCONNECTION;
        }
        IDispatch* connected = sink;
        sink = nullptr;
        connected->Release();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE EnumConnections(IEnumConnections**) override
    {
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE GetTypeInfoCount(UINT* count) override
    {
        *count = 1;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetTypeInfo(UINT index, LCID, ITypeInfo** info) override
    {
        return index == 0 ? ReadTypeInfo(probeMethods, info) : DISP_E_BADINDEX;
    }

    HRESULT STDMETHODCALLTYPE GetIDsOfNames(REFIID, LPOLESTR* names, UINT count,
        LCID, DISPID* ids) override
    {
        if (count != 1)
        {
            return DISP_E_UNKNOWNNAME;
        }
        constexpr const wchar_t* members[] = {
            L"ReadControl", L"Divide", L"ControlStamp", L"ReadAssignment",
            L"ReadAssignedValue", L"IndexedStamp", L"ReadGetIndex", L"ReadPutIndex",
            L"Fire", L"ReadEventBefore", L"ReadEventAfter", L"ReadCurrency", L"ReadDecimal",
            L"ReadCurrencyFraction", L"ReadDecimalFraction", L"ReadNegativeCurrencyFraction",
            L"ReadNegativeDecimalFraction", L"ReadLargeDecimal", L"ReadMaximumCurrency", L"ReadMinimumCurrency",
            L"ReadMaximumDecimal", L"ReadMinimumDecimal",
            L"ReadSmallestPositiveDecimal", L"ReadSmallestNegativeDecimal",
            L"ReadMaximumUnsignedInteger", L"ReadBoundaryUnsignedInteger",
            L"ReadPositiveSingle", L"ReadNegativeSingle",
            L"ReadMaximumSingle", L"ReadSmallestSingle", L"ReadSingleNaN",
            L"ReadSingleInfinity", L"ReadNegativeSingleInfinity",
            L"ReadDoubleNaN", L"ReadDoubleInfinity", L"ReadNegativeDoubleInfinity"
        };
        for (DISPID index = 0; index < static_cast<DISPID>(ARRAYSIZE(members)); ++index)
        {
            if (_wcsicmp(names[0], members[index]) == 0)
            {
                ids[0] = index + 1;
                return S_OK;
            }
        }
        return DISP_E_UNKNOWNNAME;
    }

    HRESULT STDMETHODCALLTYPE Invoke(DISPID id, REFIID, LCID, WORD flags,
        DISPPARAMS* args, VARIANT* result, EXCEPINFO*, UINT*) override
    {
        const bool indexed = id == 6;
        const bool property = id == 3 || indexed;
        if (property && (flags & DISPATCH_PROPERTYPUT) != 0)
        {
            return RecordAssignment(args, indexed);
        }
        if (id < 1 || id > 36
            || (flags & (DISPATCH_METHOD | DISPATCH_PROPERTYGET)) == 0
            || (property && (flags & DISPATCH_PROPERTYGET) == 0))
        {
            return DISP_E_MEMBERNOTFOUND;
        }
        if (args->cArgs != (indexed ? 1u : 0u) || args->cNamedArgs != 0)
        {
            return DISP_E_BADPARAMCOUNT;
        }
        if (indexed && !ReadNumber(args->rgvarg[0], getIndex))
        {
            return DISP_E_TYPEMISMATCH;
        }
        if (id >= 12)
        {
            if (result != nullptr)
            {
                if (id == 12 || id == 14 || id == 16 || id == 19 || id == 20)
                {
                    result->vt = VT_CY;
                    switch (id)
                    {
                    case 12: result->cyVal.int64 = 15000; break;
                    case 14: result->cyVal.int64 = 1000; break;
                    case 16: result->cyVal.int64 = -1000; break;
                    case 19: result->cyVal.int64 = 9223372036854775807LL; break;
                    case 20: result->cyVal.int64 = -9223372036854775807LL - 1; break;
                    }
                }
                else if (id >= 34)
                {
                    result->vt = VT_R8;
                    switch (id)
                    {
                    case 34: result->dblVal = std::numeric_limits<double>::quiet_NaN(); break;
                    case 35: result->dblVal = std::numeric_limits<double>::infinity(); break;
                    case 36: result->dblVal = -std::numeric_limits<double>::infinity(); break;
                    }
                }
                else if (id >= 27)
                {
                    result->vt = VT_R4;
                    switch (id)
                    {
                    case 27: result->fltVal = 0.1f; break;
                    case 28: result->fltVal = -0.1f; break;
                    case 29: result->fltVal = (std::numeric_limits<float>::max)(); break;
                    case 30: result->fltVal = std::numeric_limits<float>::denorm_min(); break;
                    case 31: result->fltVal = std::numeric_limits<float>::quiet_NaN(); break;
                    case 32: result->fltVal = std::numeric_limits<float>::infinity(); break;
                    case 33: result->fltVal = -std::numeric_limits<float>::infinity(); break;
                    }
                }
                else if (id >= 25)
                {
                    result->vt = VT_UI8;
                    result->ullVal = id == 25 ? 0xffffffffffffffffULL : 9007199254740993ULL;
                }
                else
                {
                    result->decVal = {};
                    result->decVal.scale = id >= 23 ? 28 : id == 18 || id >= 21 ? 0 : 1;
                    result->decVal.sign = id == 17 || id == 22 || id == 24 ? DECIMAL_NEG : 0;
                    if (id == 21 || id == 22)
                    {
                        result->decVal.Hi32 = 0xffffffff;
                        result->decVal.Lo64 = 0xffffffffffffffffULL;
                    }
                    else
                    {
                        result->decVal.Lo64 = id == 18 ? 9007199254740993ULL : id == 13 ? 15 : 1;
                    }
                    result->vt = VT_DECIMAL;
                }
            }
            return S_OK;
        }

        double value;
        if (id == 1 || property)
        {
            value = ReadControl();
        }
        else if (id == 4)
        {
            value = assignmentControl;
        }
        else if (id == 5)
        {
            value = assignedValue;
        }
        else if (id == 7)
        {
            value = getIndex;
        }
        else if (id == 8)
        {
            value = putIndex;
        }
        else if (id == 9)
        {
            return Fire(result);
        }
        else if (id == 10)
        {
            value = eventBefore;
        }
        else if (id == 11)
        {
            value = eventAfter;
        }
        else
        {
#if defined(_M_IX86)
            int marker = 0;
            value = ProbeX87Divide(&marker);
#else
            RaiseDivide();
            value = 31;
#endif
        }
        if (result != nullptr)
        {
            result->vt = VT_R8;
            result->dblVal = value;
        }
        return S_OK;
    }

private:
    static HRESULT ReadTypeInfo(REFGUID guid, ITypeInfo** info)
    {
        *info = nullptr;
        HMODULE module = nullptr;
        wchar_t path[MAX_PATH];
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&probeClass), &module))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        DWORD length = GetModuleFileNameW(module, path, ARRAYSIZE(path));
        if (length == 0 || length == ARRAYSIZE(path))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        ITypeLib* library = nullptr;
        HRESULT status = LoadTypeLibEx(path, REGKIND_NONE, &library);
        if (SUCCEEDED(status))
        {
            status = library->GetTypeInfoOfGuid(guid, info);
            library->Release();
        }
        return status;
    }

    ~ControlProbe()
    {
        if (sink != nullptr)
        {
            sink->Release();
        }
    }

    HRESULT Fire(VARIANT* result)
    {
        IDispatch* connected = sink;
        if (result != nullptr)
        {
            result->vt = VT_I4;
            result->lVal = connected == nullptr ? 0 : 1;
        }
        if (connected == nullptr)
        {
            return S_OK;
        }

        // コールバック中に購読解除しても通知先を保持します。
        connected->AddRef();
        DISPPARAMS parameters{};
        const IID empty{};
        eventBefore = ReadControl();
        const HRESULT status = connected->Invoke(1, empty, 0,
            DISPATCH_METHOD, &parameters, nullptr, nullptr, nullptr);
        eventAfter = ReadControl();
        connected->Release();
        return status;
    }

    static double ReadControl()
    {
        return ReadX87Control() * 65536.0 + ReadSseControl();
    }

    static bool ReadNumber(const VARIANT& argument, double& value)
    {
        switch (argument.vt)
        {
        case VT_I2:
            value = argument.iVal;
            return true;
        case VT_I4:
            value = argument.lVal;
            return true;
        case VT_R8:
            value = argument.dblVal;
            return true;
        default:
            return false;
        }
    }

    HRESULT RecordAssignment(const DISPPARAMS* args, bool indexed)
    {
        if (args->cArgs != (indexed ? 2u : 1u) || args->cNamedArgs != 1
            || args->rgdispidNamedArgs[0] != DISPID_PROPERTYPUT)
        {
            return DISP_E_BADPARAMCOUNT;
        }

        double value;
        double index = 0;
        if (!ReadNumber(args->rgvarg[0], value)
            || (indexed && !ReadNumber(args->rgvarg[1], index)))
        {
            return DISP_E_TYPEMISMATCH;
        }

        assignedValue = value;
        putIndex = index;
        assignmentControl = ReadControl();
        return S_OK;
    }

    IDispatch* sink = nullptr;
    double eventBefore = 0;
    double eventAfter = 0;
    double getIndex = 0;
    double putIndex = 0;
    double assignmentControl = 0;
    double assignedValue = 0;
    std::atomic<ULONG> references{1};
};

class ProbeFactory final : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory))
        {
            return E_NOINTERFACE;
        }
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG count = --references;
        if (count == 0)
        {
            delete this;
        }
        return count;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid,
        void** result) override
    {
        *result = nullptr;
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }
        auto* instance = new (std::nothrow) ControlProbe();
        if (instance == nullptr)
        {
            return E_OUTOFMEMORY;
        }
        const HRESULT status = instance->QueryInterface(iid, result);
        instance->Release();
        return status;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

private:
    std::atomic<ULONG> references{1};
};

#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:DllGetClassObject=_GetProbeClassObject@12,PRIVATE")
#else
#pragma comment(linker, "/EXPORT:DllGetClassObject=GetProbeClassObject,PRIVATE")
#endif

extern "C" HRESULT WINAPI GetProbeClassObject(
    REFCLSID clsid, REFIID iid, void** result)
{
    *result = nullptr;
    if (clsid != probeClass)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    auto* factory = new (std::nothrow) ProbeFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    const HRESULT status = factory->QueryInterface(iid, result);
    factory->Release();
    return status;
}
