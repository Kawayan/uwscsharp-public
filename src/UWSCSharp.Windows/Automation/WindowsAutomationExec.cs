using System.Diagnostics;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private const int ExecWindowLookupTimeoutMilliseconds = 5000;
        private const int ExecPollMilliseconds = 25;

        private async ValueTask<Value> InvokeExecAsync(
            IReadOnlyList<Value> values, CancellationToken token)
        {
            var args = ExecArguments.Read(values);
            token.ThrowIfCancellationRequested();
            using var process = ExecProcess.Start(args.Command);
            nint window = 0;
            if (!args.Wait || args.HasGeometry)
            {
                var watch = Stopwatch.StartNew();
                while (!process.HasExited && watch.ElapsedMilliseconds < ExecWindowLookupTimeoutMilliseconds)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitForPauseAsync(watch, token).ConfigureAwait(false);
                    window = FindExecWindow(process.Id);
                    if (window != 0)
                    {
                        ApplyExecGeometry(window, args);
                        break;
                    }

                    await DelayMillisecondsAsync(ExecPollMilliseconds, token).ConfigureAwait(false);
                }
            }

            if (!args.Wait)
            {
                token.ThrowIfCancellationRequested();
                int id = Register(window);
                if (id > 0)
                {
                    lastWindow = id;
                }

                return N(id);
            }

            while (!process.HasExited)
            {
                await DelayMillisecondsAsync(ExecPollMilliseconds, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            return N(unchecked((int)process.ExitCode()));
        }

        private static nint FindExecWindow(uint processId)
        {
            nint found = 0;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint owner);
                if (owner == processId && Native.IsWindowVisible(window))
                {
                    found = window;
                    return false;
                }

                return true;
            }, 0);
            return found;
        }

        private static void ApplyExecGeometry(nint window, ExecArguments args)
        {
            if (!args.HasGeometry)
            {
                return;
            }

            using var dpi = new PhysicalDpiScope();
            Native.Rect rect = Rect(window);
            Check(Native.SetWindowPos(window, 0,
                args.X ?? rect.Left, args.Y ?? rect.Top,
                args.Width is null or 0 ? rect.Right - rect.Left : args.Width.Value,
                args.Height is null or 0 ? rect.Bottom - rect.Top : args.Height.Value,
                Native.SwpNoZOrder | Native.SwpNoActivate));
        }
    }
}
