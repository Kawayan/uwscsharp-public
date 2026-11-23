namespace UWSCSharp.Windows;

/// <summary>
/// 実行時に固定した窓のハンドルと所有者です。
/// </summary>
public readonly record struct WindowsCapturedWindow
{
    /// <summary>
    /// ゼロでないハンドルと所有者を指定します。
    /// </summary>
    public WindowsCapturedWindow(nint handle, uint processId, uint threadId)
    {
        if (handle == 0 || processId == 0 || threadId == 0)
        {
            throw new ArgumentException("捕捉した窓の識別子はゼロにできません。");
        }

        Handle = handle;
        ProcessId = processId;
        ThreadId = threadId;
    }

    /// <summary>
    /// 捕捉した窓のハンドルです。
    /// </summary>
    public nint Handle
    {
        get;
    }

    /// <summary>
    /// 捕捉時の所有プロセス識別子です。
    /// </summary>
    public uint ProcessId
    {
        get;
    }

    /// <summary>
    /// 捕捉時の所有スレッド識別子です。
    /// </summary>
    public uint ThreadId
    {
        get;
    }
}
