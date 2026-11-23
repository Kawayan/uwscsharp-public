namespace UWSCSharp.Windows;

internal interface ISensorApartment
{
    int Initialize();

    void Pump();

    void Uninitialize();
}

/// <summary>
/// センサー専用STAのCOM初期化とmessage queueを扱います。
/// </summary>
internal sealed partial class SensorApartment : ISensorApartment
{
    internal static SensorApartment Instance { get; } = new();

    private SensorApartment()
    {
    }

    public int Initialize() => CoInitializeEx(0, 2);

    public void Pump()
    {
        // 通知が続いても、次の要求と終了状態を確認する機会を残します。
        for (int index = 0; index < 64; index++)
        {
            if (!Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1))
            {
                return;
            }

            if (message.Id == 0x0012)
            {
                throw new InvalidOperationException("センサーのmessage loopが終了しました。");
            }

            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
    }

    public void Uninitialize() => CoUninitialize();
}
