using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
internal sealed class PumpedControlFixture : IDisposable, IAsyncDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Native.Point Point;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    private sealed class OleApartment : IDisposable
    {
        internal OleApartment()
        {
            Marshal.ThrowExceptionForHR(OleInitialize(0));
        }

        /// <summary>
        /// 所有threadのOLE初期化を解除します。
        /// </summary>
        public void Dispose() => OleUninitialize();

        [DllImport("ole32.dll")]
        private static extern int OleInitialize(nint reserved);

        [DllImport("ole32.dll")]
        private static extern void OleUninitialize();
    }

    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread owner;
    private readonly ConcurrentQueue<Action> operations = new();
    private const uint OperationMessage = 0x8003;
    private uint threadId;
    private Exception? failure;
    private bool disposed;
    private string phase = "作成前";

    private PumpedControlFixture(Action<ButtonControlTests.Fixture> configure,
        bool initializeOle, string title, string rootClass, nint? dpiAwarenessContext)
    {
        owner = new Thread(() =>
        {
            nint previousDpi = 0;
            try
            {
                if (dpiAwarenessContext is nint context)
                {
                    previousDpi = Native.SetThreadDpiAwarenessContext(context);
                    Assert.NotEqual(0, previousDpi);
                }

                using OleApartment? apartment = initializeOle ? new OleApartment() : null;
                using var styles = new VisualStylesContext();
                using var fixture = new ButtonControlTests.Fixture(title, 0x100, rootClass);
                configure(fixture);
                threadId = Native.GetCurrentThreadId();
                ready.SetResult(fixture.Root);
                phase = "message待機";
                while (GetMessageW(out Message message, 0, 0, 0) > 0)
                {
                    if (message.Window == 0 && message.Id == OperationMessage)
                    {
                        while (operations.TryDequeue(out Action? operation))
                        {
                            operation();
                        }

                        continue;
                    }

                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                phase = "message待機終了";
                failure = fixture.CallbackFailure;
            }
            catch (Exception exception)
            {
                failure = exception;
                ready.TrySetException(exception);
            }
            finally
            {
                if (previousDpi != 0)
                {
                    Native.SetThreadDpiAwarenessContext(previousDpi);
                }
            }
        })
        {
            IsBackground = true
        };
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
    }

    internal nint Root => ready.Task.GetAwaiter().GetResult();

    /// <summary>
    /// 検証操作を所有thread上の1回の配送内で実行します。
    /// </summary>
    internal void Invoke(Action operation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Thread.CurrentThread == owner)
        {
            operation();
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Enqueue(() =>
        {
            try
            {
                operation();
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
        });
        Assert.True(PostThreadMessageW(threadId, OperationMessage, 0, 0));
        completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    internal static async Task<PumpedControlFixture> CreateAsync(Action<ButtonControlTests.Fixture> configure,
        bool initializeOle = false, string title = "UIA取得専用window", string rootClass = "STATIC",
        nint? dpiAwarenessContext = null)
    {
        var fixture = new PumpedControlFixture(configure, initializeOle, title, rootClass,
            dpiAwarenessContext);
        try
        {
            await fixture.ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 所有threadへ終了を要求し、別threadで終了を待ちます。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (owner.IsAlive && threadId != 0)
        {
            Assert.True(PostThreadMessageW(threadId, 0x12, 0, 0));
        }

        // callback上へ継続した場合も、所有thread自身を同期Joinしません。
        await Task.Run(() => Assert.True(owner.Join(5000),
            $"専用thread終了待ち: {phase}")).ConfigureAwait(false);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (owner.IsAlive && threadId != 0)
        {
            Assert.True(PostThreadMessageW(threadId, 0x12, 0, 0));
        }

        Assert.True(owner.Join(5000), $"専用thread終了待ち: {phase}; owner={threadId}; caller={Native.GetCurrentThreadId()}");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
