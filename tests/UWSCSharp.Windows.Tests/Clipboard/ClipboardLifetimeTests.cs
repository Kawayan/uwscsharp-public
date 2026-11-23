using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ClipboardLifetimeTests
{
    private sealed class Api : ClipboardApi, IDisposable
    {
        internal string Failure = "";
        internal int Closes;
        internal int Unlocks;
        internal int Frees;
        internal int Destroys;
        internal nint Memory;
        internal nuint Length;
        internal Action? BeforeSet;

        internal override nint Allocate(uint flags, nuint size)
        {
            Assert.Equal(2u, flags);
            Length = size;
            Memory = Marshal.AllocHGlobal(checked((int)size));
            return Memory;
        }

        internal override bool Open(nint owner) => Failure != "open";

        internal override void Close() => Closes++;

        internal override bool Available(uint format) => Failure != "missing";

        internal override nint Get(uint format) => Failure == "get" ? 0 : Memory;

        internal override nuint Size(nint memory) => Length;

        internal override nint Lock(nint memory) => Failure == "lock" ? 0 : memory;

        internal override void Unlock(nint memory) => Unlocks++;

        internal override void Free(nint memory)
        {
            Frees++;
            Marshal.FreeHGlobal(memory);
            Memory = 0;
        }

        internal override bool Empty() => Failure != "empty";

        internal override nint Set(uint format, nint memory)
        {
            BeforeSet?.Invoke();
            return Failure == "set" ? 0 : memory;
        }

        internal override nint CreateOwner() => Failure == "owner" ? 0 : 42;

        internal override void DestroyOwner(nint owner) => Destroys++;

        public void Dispose()
        {
            if (Memory != 0)
            {
                Marshal.FreeHGlobal(Memory);
            }
        }
    }

    [Theory]
    [InlineData("lock", 0, 0)]
    [InlineData("owner", 0, 0)]
    [InlineData("open", 0, 1)]
    [InlineData("empty", 1, 1)]
    [InlineData("set", 1, 1)]
    public void FailedWriteFreesOwnedMemoryAndClosesOnlyOpenedClipboard(
        string failure, int closes, int destroys)
    {
        using var api = new Api { Failure = failure };
        Assert.Throws<Win32Exception>(() => new ClipboardBackend(api).WriteText("日本語", default));
        Assert.Equal(1, api.Frees);
        Assert.Equal(closes, api.Closes);
        Assert.Equal(destroys, api.Destroys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulTransferDoesNotFreeSystemOwnedMemory(bool cancel)
    {
        using var api = new Api();
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            api.BeforeSet = cancellation.Cancel;
            Assert.ThrowsAny<OperationCanceledException>(() =>
                new ClipboardBackend(api).WriteText("text", cancellation.Token));
        }
        else
        {
            new ClipboardBackend(api).WriteText("text", default);
        }

        Assert.Equal(0, api.Frees);
        Assert.Equal(1, api.Closes);
        Assert.Equal(1, api.Unlocks);
        Assert.Equal(1, api.Destroys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("get")]
    [InlineData("lock")]
    public void ReadCopiesBeforeClosingAndNeverFreesClipboardMemory(string failure)
    {
        using var api = new Api { Failure = failure };
        byte[] data = Encoding.Unicode.GetBytes("日本語😀\0extra");
        nint memory = api.Allocate(2, (nuint)data.Length);
        Marshal.Copy(data, 0, memory, data.Length);
        var backend = new ClipboardBackend(api);
        if (failure is "get" or "lock")
        {
            Assert.Throws<Win32Exception>(() => backend.ReadText(default));
        }
        else
        {
            Assert.Equal(failure == "missing" ? "" : "日本語😀", backend.ReadText(default));
        }

        Assert.Equal(1, api.Closes);
        Assert.Equal(failure == "" ? 1 : 0, api.Unlocks);
        Assert.Equal(0, api.Frees);
    }
}
