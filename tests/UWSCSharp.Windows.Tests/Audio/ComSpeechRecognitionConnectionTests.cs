using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有STAと偽接続点だけを使い、認識器・音声入力・画面を操作しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComSpeechRecognitionConnectionTests
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ResultObject
    {
        /// <summary>
        /// COM通知引数にする検証用オブジェクトです。
        /// </summary>
        public int Marker => 1;
    }

    private sealed class Connection(NativeComEventHandler callback) : IComEventConnection
    {
        internal readonly NativeComEventConnection.Sink Sink = new(Guid.NewGuid(), 7, callback);
        internal bool Closed;

        public Exception? TakeCallbackFailure() => Sink.TakeFailure();

        public void Dispose()
        {
            Closed = true;
            Sink.Close();
        }
    }

    private sealed class Runtime : IComRuntime
    {
        internal readonly object Context = new();
        internal readonly object Phrase = new();
        internal readonly ResultObject Result = new();
        internal readonly List<object> Released = [];
        internal readonly List<int> Threads = [];
        internal Connection? Connected;
        internal int ArgumentCount = 4;
        internal int LastHResult;
        internal string[] StartupResults = ["通知"];
        private string text = "通知";
        private bool emittedOnStart;

        public object Create(string name)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return Context;
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token)
        {
            Assert.Same(Context, target);
            Assert.Equal("_ISpeechRecoContextEvents", interfaceName);
            Assert.Equal("Recognition", eventName);
            Threads.Add(Environment.CurrentManagedThreadId);
            return Connected = new(callback);
        }

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            switch (name)
            {
                case "Default":
                    return "fixture-input";
                case "Recognizer":
                case "CreateGrammar":
                    return new object();
                case "SetId":
                case "AudioInput":
                case "CmdSetRuleIdState":
                    return null;
                case "DictationSetState":
                    if (!emittedOnStart && Equals(Assert.IsType<ComVariantValue>(
                        arguments[0]).Value, 1))
                    {
                        emittedOnStart = true;
                        foreach (string result in StartupResults)
                        {
                            text = result;
                            Emit();
                        }
                    }
                    return null;
            }

            if (name == "Emit")
            {
                Emit();
                return 0;
            }

            if (name == "PhraseInfo")
            {
                Assert.Same(Result, target);
                return new ComVariantValue(9, Phrase) { Acquisitions = [Phrase] };
            }

            Assert.Same(Phrase, target);
            Assert.Equal("GetText", name);
            return new ComVariantValue(8, text);
        }

        private void Emit()
        {
            int stride = IntPtr.Size == 8 ? 24 : 16;
            nint buffer = Marshal.AllocCoTaskMem(4 * stride);
            Marshal.Copy(new byte[4 * stride], 0, buffer, 4 * stride);
            try
            {
                object[] values = [1, 0L, 0, Result];
                for (int index = 0; index < values.Length; index++)
                {
                    ComVariantValue.Write(values[index], buffer + (3 - index) * stride);
                }

                var parameters = new DISPPARAMS { cArgs = ArgumentCount, rgvarg = buffer };
                Guid iid = Guid.Empty;
                LastHResult = Connected!.Sink.Invoke(7, ref iid, 0, 1,
                    ref parameters, 0, 0, 0);
            }
            finally
            {
                for (int index = 0; index < 4; index++)
                {
                    Marshal.ThrowExceptionForHR(VariantClear(buffer + index * stride));
                }

                Marshal.FreeCoTaskMem(buffer);
            }
        }

        public void Release(object target)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Released.Add(target);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptConsumesRecognitionNotificationOnce(bool stopFirst)
    {
        var runtime = new Runtime();
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        string stop = stopFirst ? "RECOSTATE(FALSE)" : "";
        await new Interpreter(windows: automation).RunAsync($"""
            RECOSTATE(TRUE)
            {stop}
            PRINT DICTATE(FALSE)
            PRINT DICTATE(FALSE)
            RECOSTATE(FALSE)
            """, output.Add);
        Assert.Equal(["通知", ""], output);
        Assert.Equal(0, runtime.LastHResult);
        Assert.True(runtime.Connected!.Closed);
        Assert.Single(runtime.Released, item => ReferenceEquals(item,
            runtime.Phrase));
    }

    [Fact]
    public async Task ScriptConsumesOnlyTheLatestRecognitionNotification()
    {
        var runtime = new Runtime { StartupResults = ["最初", "最新"] };
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            RECOSTATE(TRUE)
            PRINT DICTATE(FALSE)
            PRINT DICTATE(FALSE)
            """, output.Add);
        Assert.Equal(["最新", ""], output);
        Assert.Equal(0, runtime.LastHResult);
        Assert.Equal(2, runtime.Released.Count(item =>
            ReferenceEquals(item, runtime.Phrase)));
        Assert.True(runtime.Connected!.Closed);
    }

    [Fact]
    public async Task RestartDiscardsUnreadRecognitionResult()
    {
        var runtime = new Runtime();
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            RECOSTATE(TRUE)
            RECOSTATE(FALSE)
            RECOSTATE(TRUE)
            PRINT DICTATE(FALSE)
            """, output.Add);
        Assert.Equal([""], output);
        Assert.Equal(0, runtime.LastHResult);
        Assert.Single(runtime.Released, item => ReferenceEquals(item,
            runtime.Phrase));
        Assert.True(runtime.Connected!.Closed);
    }

    [Fact]
    public async Task RepeatedNotificationsReleaseTemporaryObjectsBeforeDelivery()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value context = await session.CreateAsync("fixture", false, default);
        using var registration = new CancellationTokenSource();
        int received = 0;
        await session.ConnectSpeechRecognitionAsync(context, text =>
        {
            Assert.Equal("通知", text);
            Assert.Equal(++received, runtime.Released.Count);
            Assert.All(runtime.Released, item => Assert.Same(runtime.Phrase, item));
        }, registration.Token);
        // 登録後のtoken取消は、通知の寿命を取り消しません。
        registration.Cancel();
        for (int index = 0; index < 100; index++)
        {
            await session.InvokeAsync(context, "Emit", [], null, default);
            Assert.Equal(0, runtime.LastHResult);
        }

        Assert.Equal(100, received);
        Assert.Null(runtime.Connected!.TakeCallbackFailure());
        await session.DisposeAsync();
        Assert.True(runtime.Connected.Closed);
        Assert.Equal(101, runtime.Released.Count);
        Assert.Same(runtime.Context, runtime.Released[^1]);
        Assert.Single(runtime.Threads.Distinct());
    }

    [Fact]
    public async Task SubscriberFailureIsAvailableAfterReferencesAreReleased()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value context = await session.CreateAsync("fixture", false, default);
        var expected = new InvalidOperationException("subscriber");
        IComEventConnection connection = await session.ConnectSpeechRecognitionAsync(
            context, _ => throw expected, default);
        await session.InvokeAsync(context, "Emit", [], null, default);
        Assert.True(runtime.LastHResult < 0);
        Assert.Equal([runtime.Phrase], runtime.Released);
        Assert.Same(expected, connection.TakeCallbackFailure());
        Assert.Null(connection.TakeCallbackFailure());
    }

    [Fact]
    public async Task MalformedRecognitionNotificationEndsScriptWait()
    {
        var runtime = new Runtime { ArgumentCount = 3 };
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        List<string> output = [];
        Exception? failure = await Record.ExceptionAsync(() =>
            new Interpreter(windows: automation).RunAsync("""
                RECOSTATE(TRUE)
                PRINT DICTATE()
                PRINT "unexpected"
                """, output.Add, deadline.Token));
        Assert.NotNull(failure);
        Assert.False(deadline.IsCancellationRequested);
        Assert.Contains("音声認識の通知引数は4個必要です。", failure.ToString());
        Assert.Empty(output);
        Assert.True(runtime.LastHResult < 0);
        Assert.DoesNotContain(runtime.Phrase, runtime.Released);
        Assert.True(runtime.Connected!.Closed);
    }

    [Fact]
    public async Task MalformedNotificationDoesNotReadOrDeliverResult()
    {
        var runtime = new Runtime { ArgumentCount = 3 };
        await using var session = new ComSession(runtime);
        Value context = await session.CreateAsync("fixture", false, default);
        bool received = false;
        IComEventConnection connection = await session.ConnectSpeechRecognitionAsync(
            context, _ => received = true, default);
        await session.InvokeAsync(context, "Emit", [], null, default);
        Assert.False(received);
        Assert.Empty(runtime.Released);
        Assert.IsType<InvalidDataException>(connection.TakeCallbackFailure());
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);
}
