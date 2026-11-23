using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 入れ子を含むコピー対象の要素数を制限します。
/// </summary>
internal sealed class ComValueBudget
{
    private int remaining = UwscArray.MaximumElements;

    internal void Reserve(int count)
    {
        if (count > remaining)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COM値の累積要素数が上限を超えました。"));
        }

        remaining -= count;
    }
}
