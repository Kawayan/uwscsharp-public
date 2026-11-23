using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// COM操作の失敗箇所で生成した例外だけを識別します。
/// </summary>
internal static class ComOperationErrors
{
    private static readonly ConditionalWeakTable<Exception, object> Errors = new();

    internal static T Record<T>(T failure)
        where T : Exception
    {
        if (failure is not (OutOfMemoryException or OperationCanceledException)
            && failure.HResult != unchecked((int)0x8007000e)
            && !(failure.Data["UWSCSharp.ComInvokeHResult"] is int invokeResult
                && invokeResult == unchecked((int)0x8007000e)))
        {
            Errors.GetValue(failure, _ => new object());
        }

        return failure;
    }

    internal static bool Contains(Exception failure) =>
        Errors.TryGetValue(failure, out _);

    internal static T Validate<T>(Func<T> validation)
    {
        try
        {
            return validation();
        }
        catch (Exception failure) when (failure is ArgumentException
            or InvalidOperationException or OverflowException
            or NotSupportedException or InvalidCastException
            or COMException or InvalidOleVariantTypeException)
        {
            // 値の検証・変換だけを囲み、runtime呼出し全体は分類しません。
            Record(failure);
            throw;
        }
    }

    internal static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            // HRESULTから生成した実体だけを登録し、未知の障害を捕捉しません。
            Exception failure = Marshal.GetExceptionForHR(result)!;
            throw Record(failure);
        }
    }
}
