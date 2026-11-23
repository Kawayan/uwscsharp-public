namespace UWSCSharp.Windows;

/// <summary>
/// 1回の再生について開始・終了・失敗の通知順序を確認します。
/// </summary>
internal sealed class AudioHostConversation
{
    private bool started;
    internal bool IsTerminal
    {
        get;
        private set;
    }

    internal void Accept(AudioHostMessage message)
    {
        AudioHostProtocol.Validate(message);
        if (IsTerminal || message.Request is not null)
        {
            throw new InvalidDataException("音声通知の順序または内容が不正です。");
        }

        switch (message.Type)
        {
            case AudioHostMessageType.Started when !started && message.Failure is null:
                started = true;
                break;
            case AudioHostMessageType.Completed when started && message.Failure is null:
                IsTerminal = true;
                break;
            case AudioHostMessageType.Failed when message.Failure is not null:
                IsTerminal = true;
                break;
            default:
                throw new InvalidDataException("音声通知の順序または内容が不正です。");
        }
    }

    internal void CompleteRead()
    {
        if (!IsTerminal)
        {
            throw new EndOfStreamException("音声処理の終了通知より前に接続が閉じました。");
        }
    }
}
