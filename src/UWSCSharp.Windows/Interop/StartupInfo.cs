using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    public int Size;
    public nint Reserved;
    public nint Desktop;
    public nint Title;
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
    public uint XChars;
    public uint YChars;
    public uint Fill;
    public uint Flags;
    public ushort Show;
    public ushort ReservedSize;
    public nint ReservedData;
    public nint Input;
    public nint Output;
    public nint Error;
}
