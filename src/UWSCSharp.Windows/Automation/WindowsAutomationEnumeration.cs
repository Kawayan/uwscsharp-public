using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        public IReadOnlyList<int> AllWindowIds { get; private set; } = [];

        private Value InvokeGetAllWin(CallArguments args,
            CancellationToken token)
        {
            int parentId = args.Int(0);
            bool topLevel = parentId == 0;
            nint parent = topLevel ? 0 : Resolve(parentId, false);
            var handles = new List<nint>();
            if (topLevel || parent != 0)
            {
                ExceptionDispatchInfo? callbackFailure = null;
                // unmanaged callbackの外で取消と例外を伝播します。
                Native.EnumCallback callback = (handle, _) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    try
                    {
                        handles.Add(handle);
                        return true;
                    }
                    catch (Exception error)
                    {
                        callbackFailure = ExceptionDispatchInfo.Capture(error);
                        return false;
                    }
                };
                Marshal.SetLastPInvokeError(0);
                if (topLevel)
                {
                    bool success = Native.EnumWindows(callback, 0);
                    int error = Marshal.GetLastPInvokeError();
                    token.ThrowIfCancellationRequested();
                    callbackFailure?.Throw();
                    if (!success && error != 0)
                    {
                        throw WindowsOperationErrors.Record(
                            new Win32Exception(error));
                    }
                }
                else
                {
                    Native.EnumChildWindows(parent, callback, 0);
                    token.ThrowIfCancellationRequested();
                    callbackFailure?.Throw();
                }
            }

            token.ThrowIfCancellationRequested();
            int[] ids = [.. handles.Where(Native.IsWindow)
                .Distinct().Select(Register)];
            token.ThrowIfCancellationRequested();
            AllWindowIds = Array.AsReadOnly(ids);
            return N(ids.Length);
        }
    }
}
