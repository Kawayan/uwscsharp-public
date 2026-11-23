using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private ValueTask<Value> InvokePowerShellAsync(
            IReadOnlyList<Value> values, CancellationToken token)
        {
            var request = PowerShellRequest.Read(values);
            token.ThrowIfCancellationRequested();
            if (request.Command.Length == 0)
            {
                return ValueTask.FromResult(Value.String(""));
            }

            return InvokeCommandAsync(WindowsPowerShellCommand.Executable,
                WindowsPowerShellCommand.Build(request.Command), request.NoWait,
                request.Display, false, CommandOutputDecoder.DecodeUtf8, token);
        }
    }
}
