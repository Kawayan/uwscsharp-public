using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// DLLが返した文字列ポインターからスクリプトの値を読み取ります。
/// </summary>
internal static class NativeDllTextReturn
{
    internal static Value Read(NativeDllPrimitive type, nint address,
        CancellationToken token, IReadOnlyList<byte[]>? buffers = null,
        IReadOnlyList<nint>? pointers = null)
        => NativeDllTextCodec.DecodeContents(type,
            NativeDllTextContents.Read(address, NativeDllTextCodec.IsWide(type),
                token, buffers, pointers));
}
