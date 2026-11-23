namespace UWSCSharp.Core;

/// <summary>
/// 停止画面の表示と、省略した軸を維持する物理座標の指定です。
/// </summary>
public sealed record ScriptStopForm(bool Visible, int? X = null, int? Y = null);

/// <summary>
/// 実行全体が共有する停止画面への要求を受け取ります。
/// </summary>
public interface IScriptStopFormHost : IScriptHost
{
    /// <summary>
    /// 実行元が停止画面に対応しているかを返します。
    /// </summary>
    bool SupportsStopForm => true;

    /// <summary>
    /// 停止画面の表示・非表示と位置を変更します。
    /// </summary>
    ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken cancellationToken);
}
