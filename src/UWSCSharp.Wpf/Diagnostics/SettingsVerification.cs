using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UWSCSharp.Wpf;

internal static class SettingsVerification
{
    internal static string IniPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");

    internal static async Task<string?> RestoreAsync(Action close, string path,
        byte[]? previous, string? error)
    {
        try
        {
            close();
        }
        catch (Exception cleanup)
        {
            error = (error is null ? "" : error + "\n") + cleanup;
        }

        try
        {
            if (previous is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                await File.WriteAllBytesAsync(path, previous);
            }
        }
        catch (Exception cleanup)
        {
            error = (error is null ? "" : error + "\n") + cleanup;
        }

        return error;
    }

    internal static async Task<int> WriteReceiptAsync(string receipt,
        List<string> checks, string? error)
    {
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = error is null ? "PASS" : "FAIL",
            checks,
            error,
            window_shown = true,
            input_injection = false
        }));
        return error is null ? 0 : 1;
    }

    internal static async Task RunModalAsync<TWindow>(Dispatcher dispatcher,
        Action open, Func<TWindow, Task> action) where TWindow : Window
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            TWindow? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<TWindow>().Single();
                await action(dialog);
            }
            catch (Exception caught)
            {
                failure = caught;
                dialog?.Close();
            }
            finally
            {
                completed.SetResult();
            }
        }), DispatcherPriority.ApplicationIdle);
        open();
        await completed.Task;
        if (failure is not null)
        {
            throw failure;
        }
    }

    internal static Task RunSettingsModalAsync<TWindow>(
        Dispatcher dispatcher, Action open, Func<TWindow, Task> action,
        bool allowLoadError, string loadMessage, string timeoutMessage)
        where TWindow : Window =>
        RunModalAsync<TWindow>(dispatcher, open, async dialog =>
        {
            await WaitUntilAsync(() => Save(dialog).IsEnabled
                || Error(dialog).Text.Length > 0, timeoutMessage);
            VerificationAssert.Require(
                allowLoadError || Save(dialog).IsEnabled, loadMessage);
            await action(dialog);
        });

    internal static Button Save(Window dialog) =>
        (Button)dialog.FindName("SaveSettings");

    internal static TextBlock Error(Window dialog) =>
        (TextBlock)dialog.FindName("SettingsError");

    internal static void ClickSave(Window dialog) =>
        Save(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task WaitUntilAsync(Func<bool> predicate,
        string timeoutMessage)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException(timeoutMessage);
            }

            await Task.Delay(10);
        }
    }
}
