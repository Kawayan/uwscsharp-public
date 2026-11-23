using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ListEnumerationBufferTests
{
    [Fact]
    public void ItemDataOverwritesDwordAndPreservesKnownTail()
    {
        var buffer = new ListEnumerationBuffer();
        byte[] data = buffer.Prepare(7);
        MemoryMarshal.AsBytes("abcdef\0".AsSpan()).CopyTo(data);
        buffer.Capture(data, 6);
        buffer.WriteItemData(0x00420000);
        Assert.Equal("", buffer.Read(65001));
        data = buffer.Prepare(2);
        data[0] = (byte)'A';
        data[1] = 0;
        buffer.Capture(data, -1);
        Assert.Equal("ABcdef", buffer.Read(65001));
    }

    [Fact]
    public void UnchangedUnknownBytesCannotBecomeText()
    {
        var buffer = new ListEnumerationBuffer(zeroInitialized: false);
        byte[] data = buffer.Prepare(4);
        buffer.Capture(data, 0);
        Assert.Throws<NotSupportedException>(() => buffer.Read(65001));
    }
}
