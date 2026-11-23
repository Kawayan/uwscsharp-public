using System.ComponentModel;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private async ValueTask<Value> InvokeDropFileAsync(
            IReadOnlyList<Value> values, CancellationToken token)
        {
            var request = DropFileRequest.Read(values);
            try
            {
                IReadOnlyList<string> paths = FileDropPaths.Resolve(request.Directory,
                    request.Names, Environment.CurrentDirectory, token);
                nint root = Resolve(request.WindowId, false);
                FileDropDestination? selected = await FileDropTarget.ResolveAsync(root,
                    request.X, request.Y, token).ConfigureAwait(false);
                if (selected is not FileDropDestination destination)
                {
                    throw new InvalidOperationException("ファイルを受信できるウィンドウがありません。");
                }

                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                using var memory = FileDropMemory.Create(
                    paths, destination.X, destination.Y, token);
                memory.Post(destination.Window, token);
                return Value.String("");
            }
            catch (Exception error) when (error is Win32Exception or IOException
                or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw ScriptError(BuiltinFunctionNames.DropFile, error);
            }
        }
    }
}
