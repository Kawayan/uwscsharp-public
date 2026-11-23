namespace UWSCSharp.Windows;

/// <summary>
/// GETIDが実行元へ問い合わせるウィンドウの種類です。
/// </summary>
public enum WindowsHostWindow
{
    /// <summary>
    /// 実行元の本体ウィンドウです。
    /// </summary>
    Main,

    /// <summary>
    /// 実行元のログ表示ウィンドウです。
    /// </summary>
    Log,

    /// <summary>
    /// 実行元の吹き出しウィンドウです。
    /// </summary>
    Balloon,

    /// <summary>
    /// 実行元の第1フォームです。
    /// </summary>
    Form,

    /// <summary>
    /// 実行元の第2フォームです。
    /// </summary>
    Form2,

    /// <summary>
    /// 実行元のスケジュール条件が取得したウィンドウです。
    /// </summary>
    Schedule,

    /// <summary>
    /// 実行を停止する操作画面です。
    /// </summary>
    Stop
}
