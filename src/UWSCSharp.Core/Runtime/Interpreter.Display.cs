namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        internal Value ReadDisplayVariable(string name,
            WindowsDisplayVariable variable)
        {
            token.ThrowIfCancellationRequested();
            if (windows is null)
            {
                throw MissingWindowsAdapter();
            }

            try
            {
                Value result = windows.ReadDisplayVariable(variable, token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                string? message;
                try
                {
                    message = windows.GetScriptErrorMessage(name, failure);
                }
                catch (Exception mappingFailure)
                    when (mappingFailure is not OperationCanceledException)
                {
                    throw new HostFailure(mappingFailure);
                }

                if (message is not null)
                {
                    throw new InvalidOperationException(message, failure);
                }

                throw new HostFailure(failure);
            }
        }
    }
}
