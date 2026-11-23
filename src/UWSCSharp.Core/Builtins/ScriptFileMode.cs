namespace UWSCSharp.Core;

/// <summary>
/// FOPENのモードと組合せのマスクです。
/// </summary>
[Flags]
internal enum ScriptFileMode
{
    Exists = 1,
    Read = 2,
    Write = 4,
    WriteAnsi = 8,
    WriteUtf8 = 16,
    WriteUtf8Bom = 32,
    WriteUtf16 = 64,
    NoCr = 128,
    Tab = 256,
    Exclusive = 512,
    WriteMask = Write | WriteAnsi | WriteUtf8 | WriteUtf8Bom | WriteUtf16,
    AccessMask = Read | WriteMask,
    SupportedMask = AccessMask | NoCr | Tab | Exclusive
}
