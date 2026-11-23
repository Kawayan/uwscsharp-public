using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private static readonly TimeSpan ControlLookupTimeout = TimeSpan.FromSeconds(2);

        private async ValueTask<Value> InvokeGetCtlHndAsync(
            CallArguments args, CancellationToken token)
        {
            nint root = Resolve(args.Int(0));
            if (root == 0)
            {
                return N(0);
            }

            string item = args.Str(1);
            if (item == BuiltinWindowSelectors.GetMenuHnd)
            {
                return N((long)NativeMenuControl.GetMenu(root));
            }

            if (item == BuiltinWindowSelectors.GetSysMenuHnd)
            {
                return N((long)GetSystemMenu(root, false));
            }

            int occurrence = Math.Max(1, args.Int(2, 1));
            nint found = await NativeControlWorker.RunAsync(active =>
                FindControlHandle(root, item, occurrence, active), token,
                ControlLookupTimeout).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return N((long)(Native.IsWindow(found) ? found : 0));
        }

        private static nint FindControlHandle(nint root, string item,
            int occurrence, CancellationToken token)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                nint found = 0;
                int matches = 0;
                ExceptionDispatchInfo? failure = null;
                Native.EnumChildWindows(root, (child, _) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    try
                    {
                        bool match = pass == 0
                            ? NativeListNameMatcher.Matches(
                                ButtonName.Normalize(Text(child)),
                                ButtonName.Normalize(item), false)
                            : string.Equals(Text(child, true), item,
                                StringComparison.OrdinalIgnoreCase);
                        if (match && ++matches == occurrence)
                        {
                            found = child;
                            return false;
                        }

                        return true;
                    }
                    catch (Exception error)
                    {
                        failure = ExceptionDispatchInfo.Capture(error);
                        return false;
                    }
                }, 0);
                token.ThrowIfCancellationRequested();
                failure?.Throw();
                if (found != 0)
                {
                    return found;
                }
            }

            return 0;
        }
    }
}
