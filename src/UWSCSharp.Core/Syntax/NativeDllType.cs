namespace UWSCSharp.Core;

/// <summary>
/// DLL宣言の型名を、ABIへの変換前の区別を保って表します。
/// </summary>
internal enum NativeDllPrimitive
{
    Int,
    Long,
    Bool,
    Uint,
    Hwnd,
    String,
    Wstring,
    Float,
    Double,
    Word,
    Dword,
    Byte,
    Char,
    Pchar,
    Wchar,
    Pwchar,
    Boolean,
    Longlong,
    SafeArray,
    Extended,
    Variant,
    Hmenu,
    Pbyte,
    Void
}

/// <summary>
/// VARと配列の指定を独立して保持します。
/// </summary>
internal abstract record NativeDllType(bool IsByReference, bool IsArray);

internal sealed record NativeDllScalarType(
    NativeDllPrimitive Primitive,
    bool IsByReference = false,
    bool IsArray = false) : NativeDllType(IsByReference, IsArray);

internal sealed record NativeDllStructureType : NativeDllType
{
    internal NativeDllStructureType(IEnumerable<NativeDllType> fields,
        bool isByReference = false, bool isArray = false)
        : base(isByReference, isArray)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = Array.AsReadOnly(fields.ToArray());
    }

    internal IReadOnlyList<NativeDllType> Fields
    {
        get;
    }
}
