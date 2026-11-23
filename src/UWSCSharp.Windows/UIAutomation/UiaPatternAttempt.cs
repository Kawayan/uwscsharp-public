using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// patternの未提供と、提供された操作の失敗を分けます。
/// </summary>
internal static class UiaPatternAttempt
{
    internal static (bool Supported, bool Success) Run<T>(Func<object?> acquire,
        Action<object> own, Func<T, bool> action, CancellationToken token)
        where T : class
    {
        token.ThrowIfCancellationRequested();
        object? pattern;
        try
        {
            pattern = WindowsOperationErrors.Provider(acquire);
        }
        catch (Exception failure) when (
            failure is COMException && failure.HResult is
                unchecked((int)0x80040204) or unchecked((int)0x80004002)
            || failure is InvalidCastException
                && failure.HResult == unchecked((int)0x80004002))
        {
            return (false, false);
        }

        if (pattern is null)
        {
            return (false, false);
        }

        own(pattern);
        token.ThrowIfCancellationRequested();
        return pattern is T typed ? (true, action(typed)) : (false, false);
    }
}
