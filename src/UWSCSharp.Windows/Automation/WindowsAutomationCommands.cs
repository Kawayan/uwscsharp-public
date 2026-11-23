using System.ComponentModel;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private async ValueTask<Value> InvokeCommandAsync(string executable,
            string command, bool noWait, int display, bool returnExitCode,
            Func<byte[], string> decode, CancellationToken token)
        {
            bool capture = !noWait && display == 0 && !returnExitCode;
            CapturedProcess? process = null;
            try
            {
                if (display != 0)
                {
                    WindowsCommandHost host = commands.CommandHost
                        ?? throw new NotSupportedException(
                            "表示ありのコマンドには、対応した実行元の起動情報を指定してください。");
                    process = CapturedProcess.StartUncaptured(host.Executable,
                        host.BuildCommandLine(executable, command, display), display, token);
                }
                else if (capture)
                {
                    process = await CapturedProcess.StartAsync(executable, command, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    process = CapturedProcess.StartUncaptured(executable, command,
                        false, token);
                }

                token.ThrowIfCancellationRequested();
                if (noWait)
                {
                    return Value.String("");
                }

                byte[]? output = capture
                    ? await process.ReadOutputAsync(commands.MaximumOutputBytes, token)
                        .ConfigureAwait(false) : null;
                uint code = await process.WaitForExitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (display != 0)
                {
                    if (code != 0)
                    {
                        throw new IOException(
                            $"表示用コマンドホストが異常終了しました（終了コード: {code}）。");
                    }

                    return Value.String("");
                }

                if (returnExitCode)
                {
                    int signed = unchecked((int)code);
                    return Value.Numeric(signed) with
                    {
                        NativeVariantType = 3,
                        NativeScalar = signed
                    };
                }

                // 変換失敗でもJobを保持したまま子孫を停止できる範囲で変換します。
                string text = decode(output!);
                token.ThrowIfCancellationRequested();
                return Value.String(text);
            }
            catch (Exception failure)
            {
                if (process is not null)
                {
                    await process.StopAfterFailureAsync(failure).ConfigureAwait(false);
                }

                if (failure is Win32Exception or IOException or ArgumentException
                    or NotSupportedException)
                {
                    WindowsOperationErrors.Record(failure);
                }

                throw;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
