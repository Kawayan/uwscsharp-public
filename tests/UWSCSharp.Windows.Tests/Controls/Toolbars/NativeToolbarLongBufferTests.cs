using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarLongBufferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityQueryCurrentlyChangesPrefixBeforeGetter(bool cancelDuringQuery)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "unused", 701);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        owner.WriteRetainingBytes(Encoding.Unicode.GetBytes("seed-tail\0"));
        using var cancel = new CancellationTokenSource();
        List<bool> queries = [];
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, command, pointer) =>
        {
            if (message != 0x44b)
            {
                return null;
            }

            Assert.Equal(701, command);
            queries.Add(pointer == 0);
            if (pointer == 0)
            {
                // 追加照会の副作用をモデル化し、原版の実行結果とは区別します。
                owner.WriteRetainingBytes(Encoding.Unicode.GetBytes("QZ"));
                if (cancelDuringQuery)
                {
                    cancel.Cancel();
                }
            }

            return -1;
        });
        var buffer = new NativeToolbarClickBuffer(owner);
        if (cancelDuringQuery)
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                buffer.ReadText(toolbar, 701, cancel.Token));
            Assert.Equal([true], queries);
        }
        else
        {
            Assert.Equal("QZed-tail", buffer.ReadText(toolbar, 701, default));
            Assert.Equal([true, false], queries);
        }

        byte[] actual = new byte[20];
        owner.ReadRetainingBytes(actual);
        Assert.Equal("QZed-tail\0", Encoding.Unicode.GetString(actual));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("partial")]
    [InlineData("none")]
    [InlineData("cancel")]
    public void LongScratchPreservesSharedPrefixAndCompletedWrites(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "unused", 701);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        byte[] seed = [.. Enumerable.Repeat((byte)0x5a,
            NativeControlBufferOwner.Capacity)];
        Encoding.Unicode.GetBytes("seed-tail\0").CopyTo(seed, 0);
        owner.WriteRetainingBytes(seed);
        byte[] expected = (byte[])seed.Clone();
        using var cancel = new CancellationTokenSource();
        int queries = 0;
        int getters = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, command, pointer) =>
        {
            if (message == 0x41d)
            {
                return 0;
            }

            if (message != 0x44b)
            {
                return null;
            }

            Assert.Equal(701, command);
            if (pointer == 0)
            {
                queries++;
                return 6000;
            }

            getters++;
            Assert.NotEqual(owner.Acquire().Address, pointer);
            Assert.Equal(BitConverter.ToInt32(seed, 32), Marshal.ReadInt32(pointer, 32));
            if (outcome == "success")
            {
                char[] text = [.. new string('x', 6000), '\0'];
                Marshal.Copy(text, 0, pointer, text.Length);
                Encoding.Unicode.GetBytes(new string('x', 2048)).CopyTo(expected, 0);
            }
            else if (outcome != "none")
            {
                char[] text = ['N', 'Z'];
                Marshal.Copy(text, 0, pointer, text.Length);
                Encoding.Unicode.GetBytes("NZ").CopyTo(expected, 0);
            }

            if (outcome == "cancel")
            {
                cancel.Cancel();
            }

            return -1;
        });
        var buffer = new NativeToolbarClickBuffer(owner);
        if (outcome == "cancel")
        {
            Assert.ThrowsAny<OperationCanceledException>(() => buffer.ReadText(toolbar, 701, cancel.Token));
        }
        else
        {
            string expectedName = outcome == "success" ? new string('x', 512)
                : outcome == "partial" ? "NZed-tail" : "seed-tail";
            Assert.Equal(expectedName, buffer.ReadText(toolbar, 701, default));
        }

        byte[] actual = new byte[NativeControlBufferOwner.Capacity];
        owner.ReadRetainingBytes(actual);
        Assert.Equal(expected, actual);
        // 次のgetterが失敗しても、共有baseの本文prefixを矩形として読みます。
        (_, Native.Rect rectangle) = buffer.ReadRectangle(toolbar, 0, default);
        Assert.Equal(BitConverter.ToInt32(expected, 0), rectangle.Left);
        Assert.Equal(BitConverter.ToInt32(expected, 4), rectangle.Top);
        Assert.Equal(1, queries);
        Assert.Equal(1, getters);
        Assert.Null(fixture.CallbackFailure);
    }
}
