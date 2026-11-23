namespace UWSCSharp.Windows;

internal static class NativeControlOperation
{
    internal const string RestoreErrorsKey = "UWSCSharp.ControlRestoreErrors";

    internal static Task<T> RunAsync<T>(nint root,
        IListSelectionPlatform platform, Func<CancellationToken, T> operation,
        CancellationToken token, TimeSpan? timeout = null)
    {
        bool callerOwnsWindows = platform.WindowThread(root) == platform.CurrentThread();
        if (callerOwnsWindows)
        {
            Native.EnumChildWindows(root, (child, _) =>
            {
                callerOwnsWindows &= platform.WindowThread(child) == platform.CurrentThread();
                return callerOwnsWindows;
            }, 0);
        }

        return callerOwnsWindows
            ? Task.FromResult(Execute(root, platform, operation, token))
            : NativeControlWorker.RunAsync(active => Execute(root, platform, operation, active), token, timeout);
    }

    internal static T Execute<T>(nint root,
        IListSelectionPlatform platform, Func<CancellationToken, T> operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint saved = platform.IsIconic(root) || !platform.IsVisible(root)
            ? platform.ForegroundWindow() : 0;
        Exception? primary = null;
        try
        {
            return operation(token);
        }
        catch (Exception failure) when (PreservePrimary(failure, ref primary))
        {
            throw;
        }
        finally
        {
            if (saved != 0)
            {
                // 同期messageの完了後に、取消状態にかかわらず復元を要求します。
                try
                {
                    platform.Activate(saved, CancellationToken.None);
                }
                catch (Exception failure) when (primary is not null)
                {
                    IEnumerable<Exception> previous =
                        primary.Data[RestoreErrorsKey] is AggregateException errors
                            ? errors.InnerExceptions : [];
                    primary.Data[RestoreErrorsKey] = new AggregateException(
                        "controlの前面復元に失敗しました。", previous.Append(failure));
                }
            }
        }
    }

    private static bool PreservePrimary(Exception failure,
        ref Exception? primary)
    {
        // 再送出せず、入れ子の復元でも元の例外伝播を維持します。
        primary = failure;
        return false;
    }
}
