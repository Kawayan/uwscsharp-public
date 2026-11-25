using System.Globalization;
using System.IO;
using System.Windows.Automation;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ComEventPlaybackVerification
{
    internal static Task<int> RunAsync(string handle, string mode, string receipt)
    {
        Require(SandboxEnvironment.IsCurrent, "Sandbox内の検証");
        return Task.Run(async () =>
        {
            nint owner = (nint)long.Parse(handle, CultureInfo.InvariantCulture);
            var window = AutomationElement.FromHandle(owner);
            AutomationElement pause = Find("PausePlaybackButton");
            AutomationElement stop = Find("StopPlaybackButton");
            using var deadline =
                new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CancellationToken token = deadline.Token;

            Invoke(pause);
            var pausedText = new PropertyCondition(
                AutomationElement.NameProperty, "一時停止中");
            await WaitAsync(() =>
                window.FindFirst(TreeScope.Descendants, pausedText) is not null);
            await File.WriteAllTextAsync(receipt + ".paused", "Paused", token);

            if (mode == "resume")
            {
                await WaitAsync(() => File.Exists(receipt + ".resume"));
                Invoke(pause);
            }

            await WaitAsync(() => File.Exists(receipt + ".stop"));
            Invoke(stop);
            await File.WriteAllTextAsync(receipt, "Stopped", token);
            return 0;

            AutomationElement Find(string id) => window.FindFirst(
                TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.AutomationIdProperty, id))
                ?? throw new InvalidOperationException(
                    $"停止画面の操作部品が見つかりません: {id}");

            async Task WaitAsync(Func<bool> predicate)
            {
                while (!predicate())
                {
                    await Task.Delay(25, token);
                }
            }
        });
    }

    private static void Invoke(AutomationElement button)
    {
        Require(button.Current.IsEnabled, "停止画面のボタンが有効");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
}
