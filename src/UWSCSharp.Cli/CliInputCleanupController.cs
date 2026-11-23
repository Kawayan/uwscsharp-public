using System.Text;

namespace UWSCSharp.Cli;

internal readonly record struct CliRecoveryKey(char Character, ushort RepeatCount = 1);

internal readonly record struct CliRecoveryBatch(IReadOnlyList<CliRecoveryKey> Keys, bool HasEvents);

internal interface ICliRecoveryInput : IDisposable
{
    CliRecoveryBatch ReadBatch();
}

internal sealed record CliCleanupAttempt(IReadOnlyList<Exception> Errors, int PendingCount);

internal interface ICliInputCleanup
{
    int PendingCount
    {
        get;
    }

    Task<CliCleanupAttempt> RetryAsync();
}

/// <summary>
/// 実行終了後にだけ、明示された再回収操作を受け付けます。
/// </summary>
internal sealed class CliInputCleanupController(
    ICliInputCleanup cleanup, TextWriter error, Func<Task>? wait = null)
{
    private const int RecoveryPollMilliseconds = 50;

    internal const int MaximumCommandLength = 64;
    private readonly DiagnosticWriter diagnostics = new(error);

    internal async Task<int> CompleteAsync(int scriptExitCode, bool manual,
        CliStopRequests stop, Func<ICliRecoveryInput> openInput)
    {
        int pending = cleanup.PendingCount;
        if (pending == 0)
        {
            return scriptExitCode;
        }

        if (manual)
        {
            stop.EnterRecovery();
        }

        ReportPending(pending);
        if (stop.ExitRequested)
        {
            return 130;
        }

        if (diagnostics.Failed)
        {
            return FailureCode(scriptExitCode);
        }

        if (!manual)
        {
            diagnostics.WriteLine("未回収のまま終了します。プロセス終了は明示解除の成功を示しません。");
            return FailureCode(scriptExitCode);
        }

        ICliRecoveryInput? input = null;
        int result = FailureCode(scriptExitCode);
        try
        {
            input = openInput();
            result = await ReadCommandsAsync(input, scriptExitCode, stop);
        }
        catch (Exception failure)
        {
            diagnostics.WriteLine("回収操作の受付: " + failure.Message);
            result = stop.ExitRequested ? 130 : FailureCode(scriptExitCode);
        }
        finally
        {
            try
            {
                input?.Dispose();
            }
            catch (Exception failure)
            {
                diagnostics.WriteLine("回収操作の後始末: " + failure.Message);
                result = FailureCode(result);
            }
        }

        return stop.ExitRequested ? 130 : diagnostics.Failed ? FailureCode(result) : result;
    }

    private async Task<int> ReadCommandsAsync(ICliRecoveryInput input,
        int scriptExitCode, CliStopRequests stop)
    {
        var line = new StringBuilder();
        bool invalid = false;
        bool draining = true;
        while (true)
        {
            if (stop.ExitRequested)
            {
                diagnostics.WriteLine("回収操作の待機を停止しました。");
                return 130;
            }

            if (diagnostics.Failed)
            {
                return FailureCode(scriptExitCode);
            }

            if (cleanup.PendingCount == 0)
            {
                diagnostics.WriteLine("未回収所有者: 0（回収完了）");
                return scriptExitCode;
            }

            CliRecoveryBatch batch = input.ReadBatch();
            if (draining)
            {
                // 前の操作中に届いた入力を、新しい明示要求として扱いません。
                if (!batch.HasEvents)
                {
                    draining = false;
                    Prompt();
                }
            }
            else
            {
                bool completed = false;
                foreach (CliRecoveryKey key in batch.Keys)
                {
                    for (int repeat = 0; repeat < Math.Min((int)key.RepeatCount, MaximumCommandLength + 1); repeat++)
                    {
                        char character = key.Character;
                        if (character == '\r')
                        {
                            diagnostics.WriteLine();
                            if (stop.ExitRequested)
                            {
                                return 130;
                            }

                            int pendingAtCommand = cleanup.PendingCount;
                            if (pendingAtCommand == 0)
                            {
                                diagnostics.WriteLine("未回収所有者: 0（回収完了）");
                                return scriptExitCode;
                            }

                            string command = line.ToString().Trim();
                            if (!invalid && command.Equals("exit", StringComparison.OrdinalIgnoreCase))
                            {
                                ReportPending(pendingAtCommand);
                                diagnostics.WriteLine("未回収のまま終了します。プロセス終了は明示解除の成功を示しません。");
                                return FailureCode(scriptExitCode);
                            }

                            if (!invalid && command.Equals("retry", StringComparison.OrdinalIgnoreCase))
                            {
                                CliCleanupAttempt attempt = await cleanup.RetryAsync();
                                foreach (Exception failure in attempt.Errors)
                                {
                                    diagnostics.WriteLine("再回収の診断（過去の障害を含む）: " + failure.Message);
                                }

                                ReportPending(attempt.PendingCount);
                            }
                            else
                            {
                                diagnostics.WriteLine("retry または exit を入力してEnterを押してください。");
                            }

                            line.Clear();
                            invalid = false;
                            draining = true;
                            completed = true;
                            break;
                        }

                        if (character == '\b')
                        {
                            if (line.Length > 0)
                            {
                                line.Length--;
                                diagnostics.Write("\b \b");
                            }
                        }
                        else if (!char.IsControl(character) || character == '\t')
                        {
                            if (line.Length < MaximumCommandLength)
                            {
                                line.Append(character);
                                diagnostics.Write(character);
                            }
                            else
                            {
                                invalid = true;
                            }
                        }
                        else
                        {
                            invalid = true;
                        }
                    }

                    if (completed)
                    {
                        break;
                    }
                }
            }

            await (wait?.Invoke() ?? Task.Delay(RecoveryPollMilliseconds));
        }
    }

    private void Prompt() => diagnostics.Write("retry: 再回収 / exit: 未回収のまま終了 > ");

    private void ReportPending(int count) => diagnostics.WriteLine($"未回収所有者: {count}");

    private static int FailureCode(int exitCode) => exitCode == 0 ? 1 : exitCode;

    // 診断できない出力先へ再び書き込まず、見えない操作待ちを防ぎます。
    private sealed class DiagnosticWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        internal bool Failed
        {
            get; private set;
        }

        public override void Write(string? value) => TryWrite(() => inner.Write(value));

        public override void Write(char value) => TryWrite(() => inner.Write(value));

        public override void WriteLine(string? value) => TryWrite(() => inner.WriteLine(value));

        public override void WriteLine() => TryWrite(inner.WriteLine);

        private void TryWrite(Action write)
        {
            if (Failed)
            {
                return;
            }

            try
            {
                write();
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException)
            {
                Failed = true;
            }
        }
    }

}
