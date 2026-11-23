using System.Text;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Flow> ConfigureLogAsync(
            LogOptionsStatement statement, Scope scope)
        {
            if (host is not IScriptLogHost logging)
            {
                throw new ScriptException(statement.Line,
                    "出力先のhostはログ設定に対応していません。");
            }

            foreach (LogOption option in statement.Options)
            {
                Value value = await EvaluateAsync(option.Expression, scope,
                    statement.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                ScriptLogSettings settings = At(statement.Line, () =>
                {
                    if (option.Name == Keywords.LogPath)
                    {
                        if (value.Kind != ValueKind.String
                            || value.Text.Any(character => character < ' '))
                        {
                            throw new InvalidOperationException(
                                "LOGPATHには制御文字のない文字列を指定してください。");
                        }

                        return new ScriptLogSettings(Path: value.Text);
                    }

                    double number = NumericOperations.RuntimeNumber(value);
                    int maximum = option.Name == Keywords.LogLines ? 100_000 : 7;
                    if (number < 0 || number > maximum || number != Math.Truncate(number))
                    {
                        throw new InvalidOperationException(
                            $"{option.Name}には0～{maximum}の整数を指定してください。");
                    }

                    return option.Name == Keywords.LogLines
                        ? new ScriptLogSettings(MaximumLines: (int)number)
                        : new ScriptLogSettings(FileMode: (int)number);
                });
                try
                {
                    await logging.ConfigureLogAsync(settings, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (ArgumentException failure) when (failure is not
                    (DecoderFallbackException or EncoderFallbackException))
                {
                    throw new ScriptException(statement.Line, failure.Message, failure);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    throw new HostFailure(failure);
                }

                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            return default;
        }

        private async ValueTask<Flow> PrintAsync(
            PrintStatement print, Scope scope)
        {
            Value value = await EvaluateAsync(
                print.Expression, scope, print.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
            string text = At(print.Line, () => printFormatter.Format(value));
            await HostCallAsync(async () =>
            {
                await outputGate.WaitAsync(token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                try
                {
                    await host.WriteOutputAsync(text,
                        token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                finally
                {
                    outputGate.Release();
                }
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);

            return default;
        }
    }
}
