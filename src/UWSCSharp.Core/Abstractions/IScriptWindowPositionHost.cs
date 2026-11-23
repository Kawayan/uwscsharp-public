namespace UWSCSharp.Core;

/// <summary>
/// 実行が所有する停止画面を配置する物理座標です。
/// </summary>
public sealed record ScriptWindowPosition(int X, int Y)
{
    internal static ScriptWindowPosition Read(IReadOnlyList<Value> values)
    {
        if (values.Count != 2
            || values.Any(value => value.Kind == ValueKind.Omitted))
        {
            throw new InvalidOperationException(
                "POSITIONには2つの座標を指定してください。");
        }

        return new(NumericOperations.Integer(values[0]),
            NumericOperations.Integer(values[1]));
    }
}

/// <summary>
/// 実行全体が共有する停止画面とログ画面の配置を変更します。
/// </summary>
public interface IScriptWindowPositionHost : IScriptHost
{
    /// <summary>
    /// 実行元が画面の配置変更に対応するかを返します。
    /// </summary>
    bool SupportsWindowPosition => true;

    /// <summary>
    /// 停止画面を指定位置へ、ログ画面をその上下へ配置します。
    /// 表示・非表示、最小化・最大化、入力先は変更しません。
    /// </summary>
    ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
        CancellationToken cancellationToken);
}
