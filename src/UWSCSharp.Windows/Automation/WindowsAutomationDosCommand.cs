using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private ValueTask<Value> InvokeDosCommandAsync(
            IReadOnlyList<Value> values, CancellationToken token)
        {
            var request = DosCommandRequest.Read(values);
            string shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string command = $"\"{shell}\" {(request.UnicodeOutput ? "/U " : "")}/S /C \"{request.Command}\"";
            return InvokeCommandAsync(shell, command, request.NoWait,
                request.ShowWindow ? 1 : 0, request.ReturnExitCode,
                output => CommandOutputDecoder.Decode(output,
                    request.UnicodeOutput, commands.AnsiCodePage), token);
        }
    }
}
