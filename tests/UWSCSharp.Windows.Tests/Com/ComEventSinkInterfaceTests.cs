using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComEventSinkInterfaceTests
{
    [Fact]
    public void SinkExposesRequestedSourceInterface()
    {
        var source = Guid.NewGuid();
        var sink = new NativeComEventConnection.Sink(source, 1,
            (ref DISPPARAMS parameters) => { });
        nint unknown = Marshal.GetIUnknownForObject(sink);
        nint pointer = 0;
        try
        {
            Assert.Equal(0, Marshal.QueryInterface(unknown, in source, out pointer));
            Assert.NotEqual(0, pointer);
        }
        finally
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }

            Marshal.Release(unknown);
            GC.KeepAlive(sink);
        }
    }

    [Fact]
    public void ClosedSinkIgnoresLaterNotifications()
    {
        int calls = 0;
        var sink = new NativeComEventConnection.Sink(Guid.NewGuid(), 1,
            (ref DISPPARAMS parameters) => calls++);
        Guid iid = Guid.Empty;
        DISPPARAMS arguments = default;

        Assert.Equal(0, sink.Invoke(1, ref iid, 0, 1,
            ref arguments, 0, 0, 0));
        Assert.Equal(1, calls);

        sink.Close();

        Assert.Equal(0, sink.Invoke(1, ref iid, 0, 1,
            ref arguments, 0, 0, 0));
        Assert.Equal(1, calls);
        Assert.Null(sink.TakeFailure());
    }
}
