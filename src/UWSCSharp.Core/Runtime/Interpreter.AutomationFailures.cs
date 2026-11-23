using System.Collections;
using System.ComponentModel;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed class AutomationFailureException(WindowsAutomationFailure failure)
        : InvalidOperationException(Describe(failure), failure.Error)
    {
        internal WindowsAutomationFailure Failure => failure;

        private static string Describe(WindowsAutomationFailure failure)
        {
            var codes = new HashSet<int>();
            ReadCodes(failure.Error, codes);
            string suffix = codes.Count == 0 ? ""
                : $" (OSエラー: {string.Join(", ", codes.Order())})";
            return $"{failure.FunctionName}: {failure.Error.Message}{suffix}";
        }

        private static void ReadCodes(Exception error, HashSet<int> codes)
        {
            if (error is Win32Exception native)
            {
                codes.Add(native.NativeErrorCode);
            }

            if (error is AggregateException aggregate)
            {
                foreach (Exception item in aggregate.InnerExceptions)
                {
                    ReadCodes(item, codes);
                }
            }
            else if (error.InnerException is { } inner)
            {
                ReadCodes(inner, codes);
            }
        }
    }

    private sealed partial class Execution
    {
        private void ThrowIfAutomationFailed()
        {
            if (windows?.RequiresAutomationFailurePolling == true
                && windows.TakeAutomationFailure() is { } failure)
            {
                var error = new AutomationFailureException(failure);
                if (token.IsCancellationRequested)
                {
                    var cancelled = new OperationCanceledException(token);
                    AddAutomationError(cancelled, error);
                    throw cancelled;
                }

                throw error;
            }
        }

        private static ScriptException ScriptAutomationFailure(
            int line, AutomationFailureException failure)
        {
            var result = new ScriptException(line, failure.Message, failure.Failure.Error);
            CopyFailureData(failure, result);
            return result;
        }

        private static void CopyFailureData(Exception source, Exception destination)
        {
            foreach (DictionaryEntry item in source.Data)
            {
                destination.Data[item.Key] = item.Value;
            }
        }

        private static void AddAutomationCleanupError(Exception primary, Exception additional)
        {
            AddAutomationError(primary, additional, "UWSCSharp.AutomationCleanupErrors");
        }

        private static void AddAutomationError(Exception primary, Exception additional,
            string key = "UWSCSharp.AutomationErrors")
        {
            if (ReferenceEquals(primary, additional))
            {
                return;
            }

            IEnumerable<Exception> previous = primary.Data[key]
                is AggregateException aggregate ? aggregate.InnerExceptions : [];
            primary.Data[key] = new AggregateException(previous.Append(additional));
        }

        private void CollectPendingAutomationFailure(Exception primary)
        {
            try
            {
                if (windows?.TakeAutomationFailure() is { } failure)
                {
                    if (ReferenceEquals(primary, failure.Error))
                    {
                        // 元例外を包み直してDataへ保存すると循環するため、発生元だけを補います。
                        const string key = "UWSCSharp.AutomationFunctionNames";
                        IEnumerable<string> previous = primary.Data[key]
                            as IEnumerable<string> ?? [];
                        primary.Data[key] = previous.Append(failure.FunctionName)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    else
                    {
                        AddAutomationError(primary, new AutomationFailureException(failure));
                    }
                }
            }
            catch (Exception error)
            {
                AddAutomationError(primary, error);
            }
        }

        private async ValueTask WaitWithAutomationFailureAsync(Func<ValueTask> action)
        {
            await WaitWithAutomationFailureAsync(async () =>
            {
                await action().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return false;
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async ValueTask<T> WaitWithAutomationFailureAsync<T>(Func<ValueTask<T>> action)
        {
            if (windows?.RequiresAutomationFailurePolling != true)
            {
                return await WaitWithInputPollingAsync(action()).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            CancellationToken saved = token;
            using var operationStop = CancellationTokenSource.CreateLinkedTokenSource(saved);
            token = operationStop.Token;
            try
            {
                ThrowIfAutomationFailed();
                ValueTask<T> operation = action();
                T result;
                if (operation.IsCompleted)
                {
                    result = await operation.ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                else
                {
                    Task<T> pending = operation.AsTask();
                    await WaitWithInputPollingAsync(pending, operationStop)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    result = await pending.ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                ThrowIfAutomationFailed();
                return result;
            }
            catch (Exception error) when (error is not AutomationFailureException)
            {
                Exception primary = error;
                if (error is OperationCanceledException cancelled && saved.IsCancellationRequested
                    && cancelled.CancellationToken != saved)
                {
                    // 操作単位のリンクtokenを、呼出元の取消tokenとして報告しません。
                    primary = new OperationCanceledException(error.Message, error, saved);
                    CopyFailureData(error, primary);
                }

                CollectPendingAutomationFailure(primary);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                throw;
            }
            finally
            {
                token = saved;
            }
        }
    }
}
