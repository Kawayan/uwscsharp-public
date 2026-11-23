namespace UWSCSharp.Windows;

/// <summary>
/// 一つのタッチ接点を含む注入フレームの要素です。
/// </summary>
internal readonly record struct TouchContact(
    uint Id, uint Flags, Native.Point Position);

/// <summary>
/// 実行セッションが所有するタッチ入力の送信先です。
/// </summary>
internal interface ITouchInputBackend : IDisposable
{
    void ValidatePosition(Native.Point position);

    void Send(IReadOnlyList<TouchContact> contacts);
}
