namespace UWSCSharp.Windows;

internal interface ISensorManagerFactory
{
    int Create(out nint manager);
}

/// <summary>
/// 初期化済みの所有COM apartmentでmanagerの所有参照を作成します。
/// </summary>
internal sealed partial class SensorManagerFactory : ISensorManagerFactory
{
    internal static SensorManagerFactory Instance { get; } = new();
    internal static readonly Guid ClassId = new("77a1c827-fcd2-4689-8915-9d613cc5fa3e");
    internal static readonly Guid InterfaceId = new("bd77db67-45a8-42dc-8d00-6dcf15f8377a");

    private SensorManagerFactory()
    {
    }

    public int Create(out nint manager) =>
        CoCreateInstance(in ClassId, 0, 1, in InterfaceId, out manager);
}
