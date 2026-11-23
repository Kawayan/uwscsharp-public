namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        internal Value ReadCurrentDirectory()
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string directory = files?.CurrentDirectory
                    ?? Environment.CurrentDirectory;
                token.ThrowIfCancellationRequested();
                return Value.String(directory);
            }
            catch (Exception failure) when (failure is IOException
                or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(failure.Message, failure);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                throw new HostFailure(failure);
            }
        }
    }
}
