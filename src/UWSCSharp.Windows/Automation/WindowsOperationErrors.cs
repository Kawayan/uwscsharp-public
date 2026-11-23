using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace UWSCSharp.Windows;

/// <summary>
/// Windows操作の失敗箇所で生成した例外を識別します。
/// </summary>
internal static class WindowsOperationErrors
{
    private static readonly ConditionalWeakTable<Exception, object> Errors = new();

    internal static T Record<T>(T failure)
        where T : Exception
    {
        if (failure is not (OperationCanceledException or OutOfMemoryException)
            && failure.HResult != unchecked((int)0x8007000e)
            && !(failure is Win32Exception native
                && native.NativeErrorCode is 8 or 14 or 1450 or 1455))
        {
            Errors.GetValue(failure, _ => new object());
        }

        return failure;
    }

    internal static bool Contains(Exception failure) =>
        Errors.TryGetValue(failure, out _);

    internal static T Provider<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception failure) when (failure is not (
            OperationCanceledException or OutOfMemoryException
            or StackOverflowException or AccessViolationException
            or InvalidComObjectException or NullReferenceException
            or IndexOutOfRangeException or TypeLoadException
            or TypeInitializationException or BadImageFormatException
            or DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException or SEHException or InvalidProgramException
            or System.Reflection.TargetInvocationException))
        {
            // providerの直接呼出しだけを囲み、callbackや解放は囲みません。
            Record(failure);
            throw;
        }
    }

    internal static void Provider(Action operation) => Provider(() =>
    {
        operation();
        return true;
    });

    internal static T ProviderReference<T>(Func<T> operation)
        where T : class
    {
        T value = Provider(operation);
        return value ?? throw Record(new InvalidOperationException(
            "providerが必要な参照を返しませんでした。"));
    }

    internal static T FileAccess<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            // 実際のファイル操作だけを囲み、backend呼出し全体は分類しません。
            Record(failure);
            throw;
        }
    }

    internal static void FileAccess(Action action) => FileAccess(() =>
    {
        action();
        return true;
    });

    internal static T Calculate<T>(Func<T> calculation)
    {
        try
        {
            return calculation();
        }
        catch (OverflowException failure)
        {
            Record(failure);
            throw;
        }
    }

    internal static int Int32(long value)
    {
        try
        {
            return checked((int)value);
        }
        catch (OverflowException failure)
        {
            Record(failure);
            throw;
        }
    }

    internal static int Add(int left, int right) => Int32((long)left + right);

    internal static int Subtract(int left, int right) => Int32((long)left - right);

    internal static int Increment(ref int value)
    {
        value = Add(value, 1);
        return value;
    }

    internal static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Exception failure = Marshal.GetExceptionForHR(result)!;
            throw Record(failure);
        }
    }
}
