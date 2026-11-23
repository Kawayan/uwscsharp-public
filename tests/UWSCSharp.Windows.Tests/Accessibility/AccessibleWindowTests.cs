using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleWindowTests
{
    private static Value N(long value) => Value.Numeric(value);

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint wParam,
        [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    [Theory]
    [InlineData("cycle", "探索深度")]
    [InlineData("depth", "探索深度")]
    [InlineData("children", "子要素件数")]
    [InlineData("characters", "取得文字数")]
    [InlineData("variant", "子VARIANT形式")]
    public async Task ProviderFailuresPublishCurrentPrefixAndAllowNextWindowRead(
        string failure, string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var leaf = new AccessibleTestProvider(42, 0, "", "good");
        AccessibleTestProvider current = leaf;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter,
                    Volatile.Read(ref current));
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        IReadOnlyList<string> before = session.AllItems;
        AccessibleTestProvider invalid;
        if (failure == "cycle")
        {
            var children = new AccessibleTestProvider[1];
            invalid = new AccessibleTestProvider(10, 0, "", "", children);
            children[0] = invalid;
        }
        else if (failure == "depth")
        {
            invalid = leaf;
            for (int depth = 0; depth < 128; depth++)
            {
                invalid = new AccessibleTestProvider(10, 0, "", "", invalid);
            }
        }
        else if (failure == "children")
        {
            invalid = new AccessibleTestProvider(10, 0, "", "",
                [.. Enumerable.Repeat(leaf, 100001)]);
        }
        else if (failure == "characters")
        {
            string value = new('長', NativeListControl.MaximumCharacters + 1);
            invalid = new AccessibleTestProvider(42, 0, "", value);
        }
        else
        {
            invalid = new AccessibleSimpleProvider([7], [leaf])
            {
                Variants = [true]
            };
        }

        if (failure == "cycle")
        {
            Volatile.Write(ref current, invalid);
            int observed = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return 0;
                }

                Guid iid = typeof(IAccessibleNative).GUID;
                Marshal.ThrowExceptionForHR(AccessibleReader.AccessibleObjectFromWindow(
                    fixture.Root, 0xfffffffcu, ref iid, out IAccessibleNative? accessible));
                List<object> owned = [];
                List<nint> identities = [];
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        nint identity = Marshal.GetIUnknownForObject(accessible!);
                        try
                        {
                            Assert.True(Marshal.IsComObject(accessible!));
                            identities.Add(identity);
                        }
                        finally
                        {
                            Marshal.Release(identity);
                        }

                        object child = accessible!.Child(1)!;
                        owned.Add(accessible);
                        accessible = (IAccessibleNative)child;
                    }

                    return identities.Distinct().Count();
                }
                finally
                {
                    owned.Add(accessible!);
                    foreach (object value in owned)
                    {
                        if (Marshal.IsComObject(value))
                        {
                            Marshal.ReleaseComObject(value);
                        }
                    }
                }
            });
            // 所有providerのCOM再marshalでは各参照のidentityが異なります。
            Assert.Equal(5, observed);
        }

        Volatile.Write(ref current, invalid);
        Exception? error = await Record.ExceptionAsync(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        if (failure == "variant")
        {
            Assert.IsType<NotSupportedException>(error);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
        }

        Assert.Contains(message, error!.Message);
        Assert.NotSame(before, session.AllItems);
        Assert.Empty(session.AllItems);
        Assert.Equal(["good"], before);
        Volatile.Write(ref current, leaf);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        Assert.Equal(["good"], session.AllItems);
        Assert.NotSame(before, session.AllItems);
    }

    [Fact]
    public async Task WindowProviderKeepsPreviousSnapshotAfterCancellationAndComFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var first = new AccessibleTestProvider(42, 0, "", "first");
        var second = new AccessibleTestProvider(42, 0, "", "second");
        int requests = 0;
        Action? rootRead = null;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Interlocked.Increment(ref requests);
                Guid iid = typeof(IAccessibleNative).GUID;
                var root = new AccessibleTestProvider(42, 0, "", "root", first, second)
                {
                    ReadingValue = Volatile.Read(ref rootRead)
                };
                return LresultFromObject(ref iid, (nuint)parameter, root);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            Guid iid = typeof(IAccessibleNative).GUID;
            Assert.Equal(0, AccessibleReader.AccessibleObjectFromWindow(
                fixture.Root, 0xfffffffcu, ref iid, out IAccessibleNative? accessible));
            try
            {
                Assert.True(Marshal.IsComObject(accessible!));
            }
            finally
            {
                if (accessible is not null && Marshal.IsComObject(accessible))
                {
                    Marshal.ReleaseComObject(accessible);
                }
            }
        });
        IReadOnlyList<string> before = session.AllItems;
        Assert.Equal(["root", "first", "second"], before);
        Assert.True(Volatile.Read(ref requests) > 0);
        using var cancellation = new CancellationTokenSource();
        second.ReadingValue = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], cancellation.Token));
        Assert.NotSame(before, session.AllItems);
        Assert.Equal(["root", "first"], session.AllItems);
        Assert.Equal(["root", "first", "second"], before);
        second.ReadingValue = null;
        Volatile.Write(ref rootRead, () => throw new COMException("所有providerの失敗です。",
            unchecked((int)0x80004005)));
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        Assert.Equal(unchecked((int)0x80004005), error.HResult);
        Assert.NotSame(before, session.AllItems);
        Assert.Empty(session.AllItems);
        Volatile.Write(ref rootRead, null);
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        Assert.NotSame(before, session.AllItems);
        Assert.Equal(before, session.AllItems);
        fixture.Dispose();
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(16777216 | 512)], default));
        Assert.Empty(session.AllItems);
    }
}
