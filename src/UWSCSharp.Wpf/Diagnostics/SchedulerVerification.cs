using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約診断で共通の待機、スクリプト保存、画面操作と時計を提供します。
/// </summary>
internal static class SchedulerVerification
{
    internal static void WriteRuntimeHostScript(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(true));

    internal static async Task WaitForRuntimeHost(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(25, deadline.Token);
        }
    }

    internal static T FindRegistrationControl<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && (AutomationProperties.GetAutomationId(root) == id
            || root is FrameworkElement element && element.Name == id))
        {
            return match;
        }
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            T? found = TryFind(child);
            if (found is not null)
            {
                return found;
            }
        }
        throw new InvalidOperationException("診断対象のコントロールがありません: " + id);

        T? TryFind(DependencyObject child)
        {
            try
            {
                return FindRegistrationControl<T>(child, id);
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    internal static void InvokeRegistrationButton(DependencyObject root, string id)
    {
        Button button = FindRegistrationControl<Button>(root, id);
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    internal sealed class SchedulerVerificationClock : TimeProvider
    {
        internal DateTimeOffset Now
        {
            get; set;
        } =
            new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
