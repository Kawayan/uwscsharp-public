using System.IO;
using UWSCSharp.Core;
using static UWSCSharp.Wpf.MainWindow.ScriptHotkeyVerificationAccess;

namespace UWSCSharp.Wpf;

internal static class AsyncKeyboardGuardVerification
{
    // 実際のDispatcherで、送信待機中もUIが動き、後続操作が待つことを確認します。
    internal static async Task RunAsync(MainWindow window)
    {
        var access = new MainWindow.ScriptHotkeyVerificationAccess(window);
        if (access.IsPlaying)
        {
            throw new InvalidOperationException("別の実行が残っています。");
        }

        using var generation = new CancellationTokenSource();
        access.SetCancellation(generation);
        IScriptStopFormOptionsSession? options = null;
        try
        {
            options = access.CreateStopOptions(generation.Token);
            await VerifyCoreAsync(window, access, options);
        }
        finally
        {
            try
            {
                if (options is not null)
                {
                    await options.DisposeAsync();
                }
            }
            finally
            {
                access.SetCancellation(null);
            }
        }
    }

    private static async Task VerifyCoreAsync(MainWindow window,
        MainWindow.ScriptHotkeyVerificationAccess access,
        IScriptStopFormOptionsSession options)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondRan = false;
        bool settingRan = false;
        Task first = access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey, async () =>
        {
            entered.SetResult();
            await resume.Task;
        });
        Task? second = null;
        Task? setting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey, () =>
            {
                secondRan = true;
                return Task.CompletedTask;
            });
            setting = Task.Run(async () =>
            {
                await options.ApplyAsync(ScriptStopFormOption.DisableStopHotkey, default);
                await options.DisposeAsync();
                settingRan = true;
            });
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (first.IsCompleted || secondRan || settingRan || access.IsHotkeyRegistered(2))
                {
                    throw new InvalidOperationException("送信待機中の順序または解除状態が異なります。");
                }
            });
        }
        finally
        {
            resume.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null)
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (setting is not null)
            {
                await setting.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        var failure = new IOException("検証用送信失敗");
        try
        {
            await access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey, async () =>
            {
                await Task.Yield();
                throw failure;
            });
            throw new InvalidOperationException("送信失敗が通知されませんでした。");
        }
        catch (IOException error) when (ReferenceEquals(error, failure))
        {
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey,
                () => Task.FromCanceled(cancelled.Token));
            throw new InvalidOperationException("取消が通知されませんでした。");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == cancelled.Token)
        {
        }

        bool competitor = false;
        using var releaseCompetitor = new ManualResetEventSlim();
        Task? competitorTask = null;
        bool restoreFailed = false;
        try
        {
            try
            {
                await access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey, async () =>
                {
                    await Task.Yield();
                    var ready = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    competitorTask = Task.Run(() =>
                    {
                        bool registered = RegisterThreadHotkey(0x5f31,
                            access.Settings.Modifiers, (uint)access.Settings.PauseKey);
                        ready.SetResult(registered);
                        if (registered)
                        {
                            try
                            {
                                if (!releaseCompetitor.Wait(TimeSpan.FromSeconds(5)))
                                {
                                    throw new TimeoutException("競合登録の解放待機が期限を超えました。");
                                }
                            }
                            finally
                            {
                                if (!UnregisterThreadHotkey(0x5f31))
                                {
                                    throw new IOException("検証用の競合登録を解除できませんでした。");
                                }
                            }
                        }
                    });
                    competitor = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (!competitor)
                    {
                        throw new IOException("復元競合の準備に失敗しました。");
                    }
                });
            }
            catch (InvalidOperationException) when (competitor)
            {
                restoreFailed = true;
            }
        }
        finally
        {
            releaseCompetitor.Set();
            if (competitorTask is not null)
            {
                await competitorTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        if (!restoreFailed || access.IsHotkeyRegistered(2) || !access.ApplyHotkeys(access.Settings))
        {
            throw new InvalidOperationException("復元失敗の検出または設定の再適用に失敗しました。");
        }

        bool resumed = false;
        await access.SendWithoutKeyboardHotkeysAsync(access.Settings.PauseKey, () =>
        {
            resumed = true;
            return Task.CompletedTask;
        });
        if (!secondRan || !settingRan || !resumed || access.ActiveKeys.Length != 4)
        {
            throw new InvalidOperationException("非同期送信後のホットキー復元に失敗しました。");
        }
    }
}
