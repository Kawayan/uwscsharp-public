#include <windows.h>
#include <oaidl.h>
#include <new>

static LONG failureHits;
static LONG attributesOutstanding;
static LONG functionsOutstanding;

// 型情報の取得失敗だけを注入し、所有する参照は通常のCOM規則で解放します。
class TypeInfoProxy final : public ITypeInfo
{
    LONG references = 1;
    ITypeInfo* inner;
    int point;
    HRESULT failure;
    bool source;

public:
    TypeInfoProxy(ITypeInfo* value, int stage, HRESULT result, bool isSource)
        : inner(value), point(stage), failure(result), source(isSource)
    {
        inner->AddRef();
    }

    ~TypeInfoProxy() { inner->Release(); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_ITypeInfo) return E_NOINTERFACE;
        *value = static_cast<ITypeInfo*>(this);
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG count = InterlockedDecrement(&references);
        if (!count) delete this;
        return count;
    }

    HRESULT STDMETHODCALLTYPE GetTypeAttr(TYPEATTR** value) override
    {
        if (point == (source ? 6 : 1)) { *value = nullptr; InterlockedIncrement(&failureHits); return failure; }
        HRESULT result = inner->GetTypeAttr(value);
        if (SUCCEEDED(result) && *value) InterlockedIncrement(&attributesOutstanding);
        return result;
    }
    HRESULT STDMETHODCALLTYPE GetTypeComp(ITypeComp** value) override
    { return inner->GetTypeComp(value); }
    HRESULT STDMETHODCALLTYPE GetFuncDesc(UINT index, FUNCDESC** value) override
    {
        if (point == 7) { *value = nullptr; InterlockedIncrement(&failureHits); return failure; }
        HRESULT result = inner->GetFuncDesc(index, value);
        if (SUCCEEDED(result) && *value) InterlockedIncrement(&functionsOutstanding);
        return result;
    }
    HRESULT STDMETHODCALLTYPE GetVarDesc(UINT index, VARDESC** value) override
    { return inner->GetVarDesc(index, value); }
    HRESULT STDMETHODCALLTYPE GetNames(MEMBERID id, BSTR* names, UINT count, UINT* actual) override
    { return inner->GetNames(id, names, count, actual); }
    HRESULT STDMETHODCALLTYPE GetRefTypeOfImplType(UINT index, HREFTYPE* value) override
    {
        if (point == 3) { *value = 0; InterlockedIncrement(&failureHits); return failure; }
        return inner->GetRefTypeOfImplType(index, value);
    }
    HRESULT STDMETHODCALLTYPE GetImplTypeFlags(UINT index, INT* flags) override
    {
        if (point == 2) { *flags = 0; InterlockedIncrement(&failureHits); return failure; }
        return inner->GetImplTypeFlags(index, flags);
    }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames(LPOLESTR* names, UINT count, MEMBERID* ids) override
    { return inner->GetIDsOfNames(names, count, ids); }
    HRESULT STDMETHODCALLTYPE Invoke(PVOID instance, MEMBERID id, WORD flags, DISPPARAMS* parameters,
        VARIANT* result, EXCEPINFO* exception, UINT* argument) override
    { return inner->Invoke(instance, id, flags, parameters, result, exception, argument); }
    HRESULT STDMETHODCALLTYPE GetDocumentation(MEMBERID id, BSTR* name, BSTR* text,
        DWORD* context, BSTR* file) override
    {
        if ((point == 5 && id == MEMBERID_NIL) || (point == 8 && id != MEMBERID_NIL))
        {
            if (name) *name = nullptr;
            if (text) *text = nullptr;
            if (context) *context = 0;
            if (file) *file = nullptr;
            InterlockedIncrement(&failureHits);
            return failure;
        }
        return inner->GetDocumentation(id, name, text, context, file);
    }
    HRESULT STDMETHODCALLTYPE GetDllEntry(MEMBERID id, INVOKEKIND kind, BSTR* file,
        BSTR* name, WORD* ordinal) override
    { return inner->GetDllEntry(id, kind, file, name, ordinal); }
    HRESULT STDMETHODCALLTYPE GetRefTypeInfo(HREFTYPE reference, ITypeInfo** value) override
    {
        *value = nullptr;
        if (point == 4) { InterlockedIncrement(&failureHits); return failure; }
        ITypeInfo* next = nullptr;
        HRESULT result = inner->GetRefTypeInfo(reference, &next);
        if (FAILED(result)) return result;
        *value = new (std::nothrow) TypeInfoProxy(next, point, failure, true);
        next->Release();
        return *value ? S_OK : E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE AddressOfMember(MEMBERID id, INVOKEKIND kind, PVOID* value) override
    { return inner->AddressOfMember(id, kind, value); }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, PVOID* value) override
    { return inner->CreateInstance(outer, iid, value); }
    HRESULT STDMETHODCALLTYPE GetMops(MEMBERID id, BSTR* value) override
    { return inner->GetMops(id, value); }
    HRESULT STDMETHODCALLTYPE GetContainingTypeLib(ITypeLib** library, UINT* index) override
    { return inner->GetContainingTypeLib(library, index); }
    void STDMETHODCALLTYPE ReleaseTypeAttr(TYPEATTR* value) override
    { inner->ReleaseTypeAttr(value); InterlockedDecrement(&attributesOutstanding); }
    void STDMETHODCALLTYPE ReleaseFuncDesc(FUNCDESC* value) override
    { inner->ReleaseFuncDesc(value); InterlockedDecrement(&functionsOutstanding); }
    void STDMETHODCALLTYPE ReleaseVarDesc(VARDESC* value) override { inner->ReleaseVarDesc(value); }
};

extern "C" HRESULT __cdecl WrapTypeInfo(ITypeInfo* inner, int point, HRESULT failure, ITypeInfo** value)
{
    if (!value) return E_POINTER;
    *value = nullptr;
    if (!inner || point < 1 || point > 8 || SUCCEEDED(failure)) return E_INVALIDARG;
    *value = new (std::nothrow) TypeInfoProxy(inner, point, failure, false);
    return *value ? S_OK : E_OUTOFMEMORY;
}

extern "C" LONG __cdecl TypeInfoProxyState(int metric)
{
    LONG* value = metric == 1 ? &failureHits
        : metric == 2 ? &attributesOutstanding : &functionsOutstanding;
    return InterlockedCompareExchange(value, 0, 0);
}
