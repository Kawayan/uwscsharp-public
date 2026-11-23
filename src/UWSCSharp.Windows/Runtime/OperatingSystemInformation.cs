using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// マニフェストに依存しないWindowsの版と製品種別です。
/// </summary>
internal sealed partial record OperatingSystemInformation(
    int Major, int Minor, int Build, bool Server, bool Is64Bit)
{
    internal int Kind => (Major, Minor, Server) switch
    {
        (3, _, _) => 10,
        (4, _, _) => 11,
        (5, 0, _) => 12,
        (5, 1, _) => 13,
        (5, 2, _) => 14,
        (6, 0, false) => 20,
        (6, 0 or 1, true) => 21,
        (6, 1, false) => 22,
        (6, 2, false) => 23,
        (6, 2 or 3, true) => 24,
        (6, 3, false) => 25,
        (10, 0, false) => 30,
        (10, 0, true) => 31,
        _ => throw WindowsOperationErrors.Record(new NotSupportedException(
            "このWindowsバージョンの種別番号は未定義です。"))
    };

    internal static OperatingSystemInformation Read()
    {
        var version = new VersionInfo
        {
            Size = Marshal.SizeOf<VersionInfo>(),
            ServicePack = ""
        };
        int status = RtlGetVersion(ref version);
        if (status != 0 || version.ProductType is < 1 or > 3)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                $"Windowsのバージョンを取得できませんでした: {status}"));
        }

        return new(version.Major, version.Minor, version.Build,
            version.ProductType != 1, Environment.Is64BitOperatingSystem);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct VersionInfo
    {
        internal int Size;
        internal int Major;
        internal int Minor;
        internal int Build;
        internal int Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string ServicePack;
        internal ushort ServicePackMajor;
        internal ushort ServicePackMinor;
        internal ushort Suite;
        internal byte ProductType;
        internal byte Reserved;
    }
}
