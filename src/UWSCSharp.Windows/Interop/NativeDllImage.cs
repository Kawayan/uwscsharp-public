using System.Reflection.PortableExecutable;

namespace UWSCSharp.Windows;

/// <summary>
/// DLLのPEヘッダーからネイティブx86のイメージを識別します。
/// </summary>
internal static class NativeDllImage
{
    internal static bool IsNativeX86Dll(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            PEHeaders headers = reader.PEHeaders;
            PEHeader? header = headers.PEHeader;
            return header is not null
                && header.Magic == PEMagic.PE32
                && header.CorHeaderTableDirectory.Size == 0
                && headers.CoffHeader.Machine == Machine.I386
                && (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or BadImageFormatException)
        {
            // 形式が判定できないときは呼出し元のロードエラーを維持します。
            return false;
        }
    }
}
