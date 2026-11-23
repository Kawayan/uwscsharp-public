using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の参照数と同期通知で所有を確認し、実COM server・機器・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorEventConnectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionRetainsSourceAndClosesBeforeUnregistration(bool manager)
    {
        var api = new Api();
        var receiver = new Receiver();
        int entered = 0;
        SensorCallbackSink sink = manager
            ? new SensorManagerEventSink((_, _) => entered++)
            : new SensorEventSink(receiver);
        using var connection = SensorEventConnection.Connect(1, sink, api);
        Assert.Equal(manager, api.Manager);
        Assert.Equal(2, api.SourceReferences);
        Assert.Equal(1, api.SinkReferences);
        api.OnSet = pointer =>
        {
            if (pointer == 0)
            {
                if (sink is SensorManagerEventSink managerSink)
                {
                    Assert.Equal(0, managerSink.OnSensorEnter(10, 2));
                }
                else
                {
                    Assert.Equal(0, ((SensorEventSink)sink).OnDataUpdated(10, 20));
                }
            }
        };
        connection.Dispose();
        Assert.Equal(0, entered);
        Assert.Empty(receiver.Calls);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, api.SinkReferences);
        Assert.Equal(["add-source", "get-sink", "set:2", "release-sink", "set:0", "release-source"],
            api.Calls);
    }

    [Fact]
    public void RegistrationFailureDetachesPartialRegistrationAndKeepsPrimaryHResult()
    {
        var api = new Api { SetResult = unchecked((int)0x80070005) };
        int calls = 0;
        var sink = new SensorManagerEventSink((_, _) => calls++);
        Assert.Equal(api.SetResult, Assert.ThrowsAny<Exception>(() =>
            SensorEventConnection.Connect(1, sink, api)).HResult);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, api.SinkReferences);
        Assert.Equal(0, sink.OnSensorEnter(10, 1));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SinkPointerFailureDoesNotAttemptToChangeTheNativeSink()
    {
        var failure = new InvalidOperationException("sink取得失敗");
        var api = new Api { PointerFailure = failure };
        var sink = new SensorManagerEventSink((_, _) => throw new InvalidOperationException());
        Assert.Same(failure, Record.Exception(() => SensorEventConnection.Connect(1, sink, api)));
        Assert.Equal(["add-source", "get-sink", "release-source"], api.Calls);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, sink.OnSensorEnter(10, 1));
    }

    [Fact]
    public void ReentrantSinkClosureRollsBackRegistration()
    {
        var api = new Api();
        var sink = new SensorManagerEventSink((_, _) => { });
        api.OnSet = pointer =>
        {
            if (pointer != 0)
            {
                sink.Close();
            }
        };
        Assert.Throws<ObjectDisposedException>(() => SensorEventConnection.Connect(1, sink, api));
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, api.SinkReferences);
    }

    [Fact]
    public void ReusingASinkDoesNotCloseOrOverwriteItsFirstConnection()
    {
        var api = new Api();
        int calls = 0;
        var sink = new SensorManagerEventSink((_, _) => calls++);
        using var connection = SensorEventConnection.Connect(1, sink, api);
        int nativeCalls = api.Calls.Count;
        Assert.Throws<InvalidOperationException>(() => SensorEventConnection.Connect(1, sink, api));
        Assert.Equal(nativeCalls, api.Calls.Count);
        Assert.Equal(0, sink.OnSensorEnter(10, 1));
        Assert.Equal(1, calls);
        connection.Dispose();
        Assert.Throws<InvalidOperationException>(() => SensorEventConnection.Connect(1, sink, api));
        Assert.Equal(1, api.SourceReferences);
    }

    [Fact]
    public void FailedDetachLeavesOnlyAnInertSinkEvenIfTheServerRetainsIt()
    {
        var api = new Api { ClearResult = unchecked((int)0x80004005), RetainOnClearFailure = true };
        int calls = 0;
        var sink = new SensorManagerEventSink((_, _) => calls++);
        using var connection = SensorEventConnection.Connect(1, sink, api);
        Assert.Equal(api.ClearResult, Assert.ThrowsAny<Exception>(connection.Dispose).HResult);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(1, api.SinkReferences);
        Assert.Equal(0, sink.OnSensorEnter(10, 1));
        Assert.Equal(0, calls);
        int nativeCalls = api.Calls.Count;
        connection.Dispose();
        Assert.Equal(nativeCalls, api.Calls.Count);
        api.Release(2);
    }

    [Fact]
    public void PrimaryRegistrationErrorSurvivesAllCleanupErrors()
    {
        var api = new Api
        {
            SetResult = unchecked((int)0x80070005),
            ClearResult = unchecked((int)0x80004005),
            SinkReleaseFailure = true,
            SourceReleaseFailure = true
        };
        var sink = new SensorManagerEventSink((_, _) => { });
        Exception error = Assert.ThrowsAny<Exception>(() => SensorEventConnection.Connect(1, sink, api));
        Assert.Equal(api.SetResult, error.HResult);
        Assert.IsAssignableFrom<Exception>(error.Data["UWSCSharp.SensorEventCleanup"]);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, api.SinkReferences);
    }

    [Fact]
    public void DisposeKeepsTheDetachErrorWhenReleaseAlsoFails()
    {
        var api = new Api { ClearResult = unchecked((int)0x80070005), SourceReleaseFailure = true };
        var sink = new SensorManagerEventSink((_, _) => { });
        using var connection = SensorEventConnection.Connect(1, sink, api);
        Exception error = Assert.ThrowsAny<Exception>(connection.Dispose);
        Assert.Equal(api.ClearResult, error.HResult);
        Assert.IsType<InvalidOperationException>(error.Data["UWSCSharp.SensorEventCleanup"]);
        Assert.Equal(1, api.SourceReferences);
        Assert.Equal(0, api.SinkReferences);
    }

    [Fact]
    public void WrongThreadIsRejectedBeforeNativeOperationsOrReceiverCalls()
    {
        var api = new Api();
        int calls = 0;
        var sink = new SensorManagerEventSink((_, _) => calls++);
        using var connection = SensorEventConnection.Connect(1, sink, api);
        int nativeCalls = api.Calls.Count;
        Exception? connectError = null;
        Exception? disposeError = null;
        int callbackResult = 0;
        var thread = new Thread(() =>
        {
            connectError = Record.Exception(() => SensorEventConnection.Connect(1, sink, api));
            disposeError = Record.Exception(connection.Dispose);
            callbackResult = sink.OnSensorEnter(10, 1);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(unchecked((int)0x8001010e), Assert.IsType<COMException>(connectError).HResult);
        Assert.Equal(unchecked((int)0x8001010e), Assert.IsType<COMException>(disposeError).HResult);
        Assert.Equal(unchecked((int)0x8001010e), callbackResult);
        Assert.Equal(nativeCalls, api.Calls.Count);
        Assert.Equal(0, calls);
        Assert.IsType<COMException>(connection.TakeCallbackFailure());
        Assert.Null(connection.TakeCallbackFailure());
        Assert.Equal(0, sink.OnSensorEnter(10, 1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DeviceSinkDeliversAllFieldsAndStoresOnlyTheFirstFailure()
    {
        var receiver = new Receiver();
        var sink = new SensorEventSink(receiver);
        Guid id = new("11223344-5566-7788-99aa-bbccddeeff00");
        Assert.Equal(0, sink.OnStateChanged(10, 3));
        Assert.Equal(0, sink.OnDataUpdated(10, 20));
        Assert.Equal(0, sink.OnEvent(10, in id, 30));
        Assert.Equal(0, sink.OnLeave(in id));
        Assert.Equal(["state:10:3", "data:10:20", $"event:10:{id}:30", $"leave:{id}"], receiver.Calls);
        var failure = new COMException("通知失敗", unchecked((int)0x80070005));
        receiver.Failure = failure;
        Assert.Equal(failure.HResult, sink.OnDataUpdated(10, 20));
        receiver.Failure = new InvalidOperationException("後の通知失敗");
        Assert.True(sink.OnLeave(in id) < 0);
        Assert.Same(failure, sink.TakeFailure());
        Assert.Null(sink.TakeFailure());
        sink.Close();
        int calls = receiver.Calls.Count;
        Assert.Equal(0, sink.OnStateChanged(10, 1));
        Assert.Equal(0, sink.OnDataUpdated(10, 20));
        Assert.Equal(0, sink.OnEvent(10, in id, 30));
        Assert.Equal(0, sink.OnLeave(in id));
        Assert.Equal(calls, receiver.Calls.Count);
    }

    private sealed class Receiver : ISensorEventReceiver
    {
        internal Exception? Failure;
        internal List<string> Calls { get; } = [];

        public void StateChanged(nint sensor, int state) => Record($"state:{sensor}:{state}");

        public void DataUpdated(nint sensor, nint report) => Record($"data:{sensor}:{report}");

        public void Event(nint sensor, in Guid eventId, nint data) => Record($"event:{sensor}:{eventId}:{data}");

        public void Leave(in Guid id) => Record($"leave:{id}");

        private void Record(string call)
        {
            Calls.Add(call);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class Api : ISensorEventNativeApi
    {
        internal int SourceReferences = 1;
        internal int SinkReferences;
        internal int SetResult;
        internal int ClearResult;
        internal bool Manager;
        internal bool RetainOnClearFailure;
        internal bool SinkReleaseFailure;
        internal bool SourceReleaseFailure;
        internal Exception? PointerFailure;
        internal Action<nint>? OnSet;
        private nint installed;
        internal List<string> Calls { get; } = [];

        public void AddRef(nint source)
        {
            Assert.Equal(1, source);
            SourceReferences++;
            Calls.Add("add-source");
        }

        public void Release(nint instance)
        {
            if (instance == 1)
            {
                SourceReferences--;
                Calls.Add("release-source");
                if (SourceReleaseFailure)
                {
                    throw new InvalidOperationException("通知元の解放失敗");
                }
            }
            else
            {
                Assert.Equal(2, instance);
                SinkReferences--;
                Calls.Add("release-sink");
                if (SinkReleaseFailure)
                {
                    throw new InvalidOperationException("sinkの解放失敗");
                }
            }
        }

        public nint GetSinkPointer(SensorCallbackSink sink)
        {
            Calls.Add("get-sink");
            if (PointerFailure is not null)
            {
                throw PointerFailure;
            }

            SinkReferences++;
            return 2;
        }

        public int SetSink(nint source, nint sink, bool manager)
        {
            Assert.Equal(1, source);
            Assert.True(SourceReferences > 1);
            Manager = manager;
            Calls.Add($"set:{sink}");
            int result = sink == 0 ? ClearResult : SetResult;
            if (sink != 0)
            {
                SinkReferences++;
            }

            if (!(sink == 0 && result < 0 && RetainOnClearFailure))
            {
                if (installed != 0)
                {
                    SinkReferences--;
                }

                installed = sink;
            }

            OnSet?.Invoke(sink);
            return result;
        }
    }
}
