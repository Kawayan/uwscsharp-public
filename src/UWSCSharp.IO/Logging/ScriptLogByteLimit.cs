namespace UWSCSharp.IO;

/// <summary>
/// 既定ログだけに適用するUTF-8の保存容量を表します。
/// </summary>
public sealed class ScriptLogByteLimit
{
    /// <summary>
    /// 改行を含む上限を指定します。最小値は2バイトです。
    /// </summary>
    public ScriptLogByteLimit(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 2);
        MaximumBytes = maximumBytes;
    }

    /// <summary>
    /// 保存するUTF-8の最大バイト数を返します。
    /// </summary>
    public int MaximumBytes
    {
        get;
    }
}
