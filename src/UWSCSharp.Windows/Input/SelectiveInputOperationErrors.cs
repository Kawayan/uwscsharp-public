using System.ComponentModel;

namespace UWSCSharp.Windows;

/// <summary>
/// 公開入口で、想定した入力操作の失敗だけを識別します。
/// </summary>
internal static class SelectiveInputOperationErrors
{
    internal static bool IsExpected(Exception failure)
    {
        if (WindowsOperationErrors.Contains(failure))
        {
            return true;
        }

        if (failure is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(IsExpected);
        }

        if (failure is Win32Exception or TimeoutException)
        {
            WindowsOperationErrors.Record(failure);
            return WindowsOperationErrors.Contains(failure);
        }

        return false;
    }
}
