using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

internal interface IButtonRouteGatePlatform
{
    uint Thread(nint root);

    bool Enumerate(uint thread, Func<nint, bool> visitor);

    string Label(nint window);
}

/// <summary>
/// rootのthreadにある.NET通知windowからBTN補助経路を判定します。
/// </summary>
internal static class NativeButtonRouteGate
{
    internal static bool Matches(nint root, IButtonRouteGatePlatform platform,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint thread = platform.Thread(root);
        token.ThrowIfCancellationRequested();
        List<string> labels = [];
        ExceptionDispatchInfo? failure = null;
        platform.Enumerate(thread, window =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string label = DirectTargetSelector.NormalizeLabel(platform.Label(window));
                if (label.Length != 0)
                {
                    labels.Add(label);
                }

                return true;
            }
            catch (Exception error)
            {
                // 例外を抑止せず、unmanaged callbackから戻った後に伝播します。
                failure = ExceptionDispatchInfo.Capture(error);
                return false;
            }
        });
        failure?.Throw();
        token.ThrowIfCancellationRequested();
        foreach (string label in labels)
        {
            if (label.Contains(".NET-BROADCASTEVENTWINDOW", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class NativeButtonRouteGatePlatform : IButtonRouteGatePlatform
{
    public uint Thread(nint root) => Native.GetWindowThreadProcessId(root, out _);

    public bool Enumerate(uint thread, Func<nint, bool> visitor)
        => EnumThreadWindows(thread, (window, _) => visitor(window), 0);

    public string Label(nint window) => NativeSelectionTarget.Label(window);
}
