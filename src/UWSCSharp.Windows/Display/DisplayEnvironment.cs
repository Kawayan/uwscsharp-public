using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed record DisplayMonitor(nint Handle, int X, int Y,
    int Width, int Height, string Name, bool Primary);

internal interface IDisplayPlatform
{
    IReadOnlyList<DisplayMonitor> Enumerate(CancellationToken token);

    Native.Point CursorPosition();

    int BitsPerPixel();
}

/// <summary>
/// 現在の主画面を先頭にそろえ、画面情報を共有します。
/// </summary>
internal sealed class DisplayEnvironment(IDisplayPlatform platform)
{
    internal IReadOnlyList<DisplayMonitor> Monitors(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<DisplayMonitor> monitors = platform.Enumerate(token);
        token.ThrowIfCancellationRequested();
        if (monitors.Count > 0 && (monitors.Count(m => m.Primary) != 1
            || monitors.Any(m => m.Width <= 0 || m.Height <= 0)))
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "モニターの構成情報が不正です。"));
        }

        return [.. monitors.OrderByDescending(m => m.Primary)];
    }

    internal Value Query(MonitorRequest? request, CancellationToken token)
    {
        IReadOnlyList<DisplayMonitor> monitors = Monitors(token);
        if (request is null)
        {
            return Value.Numeric(monitors.Count);
        }

        if (request.Value.Number < 0 || request.Value.Number >= monitors.Count)
        {
            return Value.Numeric(0);
        }

        DisplayMonitor monitor = monitors[request.Value.Number];
        return request.Value.Item switch
        {
            MonitorItem.X => Value.Numeric(monitor.X),
            MonitorItem.Y => Value.Numeric(monitor.Y),
            MonitorItem.Width => Value.Numeric(monitor.Width),
            MonitorItem.Height => Value.Numeric(monitor.Height),
            MonitorItem.Name => Value.String(monitor.Name),
            MonitorItem.Primary => Value.Numeric(monitor.Primary ? 1 : 0),
            _ => throw new InvalidOperationException("モニター取得項目が不正です。")
        };
    }

    internal Value Read(WindowsDisplayVariable variable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int result;
        if (variable is WindowsDisplayVariable.MouseX or WindowsDisplayVariable.MouseY)
        {
            Native.Point point = platform.CursorPosition();
            result = variable == WindowsDisplayVariable.MouseX ? point.X : point.Y;
        }
        else if (variable == WindowsDisplayVariable.ScreenBitsPerPixel)
        {
            result = platform.BitsPerPixel();
            if (result <= 0)
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException(
                    "主画面のビット数を取得できませんでした。"));
            }
        }
        else
        {
            IReadOnlyList<DisplayMonitor> monitors = Monitors(token);
            if (monitors.Count == 0)
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException(
                    "主画面がありません。"));
            }

            result = variable switch
            {
                WindowsDisplayVariable.ScreenWidth => monitors[0].Width,
                WindowsDisplayVariable.ScreenHeight => monitors[0].Height,
                _ => throw new ArgumentOutOfRangeException(nameof(variable))
            };
        }

        token.ThrowIfCancellationRequested();
        return Value.Numeric(result);
    }
}
