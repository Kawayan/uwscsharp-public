using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

/// <summary>
/// ショートカット送信で必要な保持状態と入力経路を提供します。
/// </summary>
internal interface IShortcutKeyBackend
{
    bool IsHeld(int rawKey);

    ValueTask SendAsync(int rawKey, int mode,
        CancellationToken cancellationToken);

    Task DelayAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 修飾キーを保持し、今回の送信で残った入力だけを解放します。
/// </summary>
internal static class ShortcutKeySequence
{
    internal const string CleanupErrorsKey = "UWSCSharp.CleanupErrors";

    internal static int PhysicalKey(int raw) => raw switch
    {
        16 => 160,
        17 => 162,
        18 => 164,
        _ => raw
    };

    internal static async Task RunAsync(IReadOnlyList<int> keys,
        IShortcutKeyBackend backend, CancellationToken cancellationToken)
    {
        var initiallyHeld = new HashSet<int>();
        foreach (int key in keys.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (backend.IsHeld(key))
            {
                initiallyHeld.Add(key);
            }
        }

        var attempted = new List<int>();
        var modifiers = new HashSet<int>();
        Exception? primary = null;
        try
        {
            foreach (int key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool modifier = IsModifier(key);
                if (modifier && (!modifiers.Add(key)
                    || initiallyHeld.Contains(key)))
                {
                    continue;
                }

                await backend.DelayAsync(cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // 送信後に例外が出ても、保持済みなら後処理の対象にします。
                attempted.Add(key);
                int mode = modifier || initiallyHeld.Contains(key) ? 1 : 0;
                await backend.SendAsync(key, mode, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            var checkedKeys = new HashSet<int>();
            var failures = new List<Exception>();
            for (int index = attempted.Count - 1; index >= 0; index--)
            {
                int key = attempted[index];
                if (initiallyHeld.Contains(key) || !checkedKeys.Add(key))
                {
                    continue;
                }

                try
                {
                    if (backend.IsHeld(key))
                    {
                        await backend.SendAsync(key, 2,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            ReportCleanupFailures(primary, failures);
        }
    }

    private static bool IsModifier(int key) =>
        key is 16 or 17 or 18 or >= 160 and <= 165 or 91 or 92;

    private static void ReportCleanupFailures(Exception? primary,
        IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (primary is not null)
        {
            IEnumerable<Exception> previous = primary.Data[CleanupErrorsKey]
                is AggregateException aggregate ? aggregate.InnerExceptions
                : primary.Data[CleanupErrorsKey] is Exception existing
                    ? [existing] : [];
            primary.Data[CleanupErrorsKey] = new AggregateException(
                "ショートカットキーの解放に失敗しました。",
                previous.Concat(failures));
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("ショートカットキーの解放に失敗しました。",
            failures);
    }
}
