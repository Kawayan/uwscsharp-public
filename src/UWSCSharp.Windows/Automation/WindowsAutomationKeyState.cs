using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private static readonly TimeSpan KeyStateQueryTimeout = TimeSpan.FromSeconds(2);

        private async ValueTask<Value> InvokeGetKeyStateAsync(
            CallArguments args, CancellationToken token)
        {
            var request = KeyStateRequest.Read(args.Get(0));
            bool result = request.Kind == KeyStateKind.Ime
                ? await NativeControlWorker.RunAsync(active =>
                    KeyStateReader.Read(request, keyState, swapMouseButtons, active),
                    token, KeyStateQueryTimeout).ConfigureAwait(false)
                : KeyStateReader.Read(request, keyState, swapMouseButtons, token,
                    mouseKeyHistory);
            return Value.Logical(result);
        }

        public bool RequiresInputPolling => true;

        public void PollInput(CancellationToken cancellationToken)
        {
            lock (mousePollGate)
            {
                if (disposed)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                if (timestamp < nextMousePoll)
                {
                    return;
                }

                nextMousePoll = timestamp
                    + System.Diagnostics.Stopwatch.Frequency / 50;
                mouseKeyHistory.Poll(keyState, cancellationToken);
            }
        }
    }
}
