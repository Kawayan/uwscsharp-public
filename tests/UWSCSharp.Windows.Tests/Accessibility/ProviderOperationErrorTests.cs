using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ProviderOperationErrorTests
{
    private sealed class Pattern : IUiaExpandCollapsePattern
    {
        internal Action? Expanding;
        internal Action? Collapsing;
        internal readonly List<string> Calls = [];

        public int State() => 0;

        public void Expand()
        {
            Calls.Add("expand");
            Expanding?.Invoke();
        }

        public void Collapse()
        {
            Calls.Add("collapse");
            Collapsing?.Invoke();
        }
    }

    private sealed class LocationProvider(Exception? failure)
        : AccessibleTestProvider(42, 0, "", "value"), IAccessibleNative
    {
        void IAccessibleNative.Location(out int x, out int y,
            out int width, out int height, object child)
        {
            x = 3;
            y = 4;
            width = 5;
            height = 6;
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    [Theory]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80131509))]
    [InlineData(unchecked((int)0x80070002))]
    public async Task NativeMsaaFailureReachesExceptAndNextReadRecovers(int code)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var good = new AccessibleTestProvider(42, 0, "", "good");
        var bad = new AccessibleTestProvider(42, 0, "", "bad");
        AccessibleTestProvider current = bad;
        bad.ReadingValue = () =>
        {
            Volatile.Write(ref current, good);
            throw new COMException("providerの取得失敗", code);
        };
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter,
                    Volatile.Read(ref current));
            };
        }, initializeOle: true);
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"));
        await using (IWindowsAutomationSession session = automation.CreateSession())
        {
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric((long)fixture.Root)], default);
            Exception? failure = await Record.ExceptionAsync(async () =>
                await session.InvokeAsync("GETITEM",
                    [id, Value.Numeric(16777728)], default));
            Assert.NotNull(failure);
            Assert.Equal(code, failure.HResult);
            Assert.Equal(failure.Message,
                session.GetScriptErrorMessage("GETITEM", failure));
        }

        Volatile.Write(ref current, bad);
        List<string> output = [];
        await new Interpreter(windows: automation)
            .RunAsync($"id=HNDTOID({(long)fixture.Root})\nTRY\n"
                + "GETITEM(id,16777728)\nEXCEPT\nPRINT TRY_ERRLINE\n"
                + "PRINT TRY_ERRMSG\nENDTRY\nPRINT GETITEM(id,16777728)\n"
                + "PRINT ALL_ITEM_LIST[0]", output.Add);
        Assert.Equal(4, output.Count);
        Assert.Equal("3", output[0]);
        Assert.StartsWith("3行:", output[1]);
        Assert.Equal("1", output[2]);
        Assert.Equal("good", output[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UiaPatternFailureIsIdentifiedWithoutReplacingException(bool collapse)
    {
        var failure = new COMException("pattern失敗", unchecked((int)0x80004005));
        var pattern = new Pattern();
        if (collapse)
        {
            pattern.Collapsing = () => throw failure;
        }
        else
        {
            pattern.Expanding = () => throw failure;
        }

        COMException actual = Assert.Throws<COMException>(() =>
            UiaSelection.ReadExpanded(pattern, () => "value", default));
        Assert.Same(failure, actual);
        Assert.True(WindowsOperationErrors.Contains(actual));
        Assert.Equal(["expand", "collapse"], pattern.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCallbackFailureIsNotReclassifiedByCollapse(bool cleanupFails)
    {
        var primary = new COMException("callback内部障害");
        var cleanup = new COMException("collapse失敗");
        var pattern = new Pattern();
        if (cleanupFails)
        {
            pattern.Collapsing = () => throw cleanup;
        }

        COMException actual = Assert.Throws<COMException>(() =>
            UiaSelection.ReadExpanded(pattern, () => throw primary, default));
        Assert.Same(primary, actual);
        Assert.False(WindowsOperationErrors.Contains(primary));
        if (cleanupFails)
        {
            Assert.Same(cleanup, primary.Data["UWSCSharp.UiaComboCollapseError"]);
            Assert.True(WindowsOperationErrors.Contains(cleanup));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MsaaProviderAndNotificationCallbackHaveSeparateBoundaries(bool callback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var failure = new COMException("MSAA失敗");
        var provider = new AccessibleTestProvider(42, 0, "", "value");
        if (!callback)
        {
            provider.ReadingValue = () => throw failure;
        }

        COMException actual = Assert.Throws<COMException>(() =>
            AccessibleItems.ReadProvider(provider, 16777216, false, true, 0,
                default, onItem: callback ? _ => throw failure : null));
        Assert.Same(failure, actual);
        Assert.Equal(!callback, WindowsOperationErrors.Contains(actual));
    }

    [Fact]
    public void ProviderCancellationAndMemoryRemainUnclassified()
    {
        using var cancel = new CancellationTokenSource();
        var cancelled = new OperationCanceledException(cancel.Token);
        var pattern = new Pattern { Expanding = () => throw cancelled };
        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
            UiaSelection.ReadExpanded(pattern, () => "value", cancel.Token));
        Assert.Equal(cancel.Token, actual.CancellationToken);
        Assert.False(WindowsOperationErrors.Contains(actual));
        var memory = new COMException("メモリ不足", unchecked((int)0x8007000e));
        pattern.Expanding = () => throw memory;
        Assert.Same(memory, Assert.Throws<COMException>(() =>
            UiaSelection.ReadExpanded(pattern, () => "value", default)));
        Assert.False(WindowsOperationErrors.Contains(memory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MsaaLocationPreservesAllFieldsAndFailureIdentity(bool fails)
    {
        var failure = new COMException("Location失敗");
        var provider = new LocationProvider(fails ? failure : null);
        if (fails)
        {
            COMException actual = Assert.Throws<COMException>(() =>
                AccessibleReader.Location(provider, out _, out _, out _, out _, 0));
            Assert.Same(failure, actual);
            Assert.True(WindowsOperationErrors.Contains(actual));
        }
        else
        {
            AccessibleReader.Location(provider, out int x, out int y,
                out int width, out int height, 0);
            Assert.Equal((3, 4, 5, 6), (x, y, width, height));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("index")]
    [InlineData("rcw")]
    [InlineData("type")]
    [InlineData("marshal")]
    public void InfrastructureFailureIsNotAProviderOperationError(string kind)
    {
        Exception failure = kind switch
        {
            "null" => new NullReferenceException(),
            "index" => new IndexOutOfRangeException(),
            "rcw" => new InvalidComObjectException(),
            "marshal" => new MarshalDirectiveException(),
            _ => new TypeLoadException()
        };
        var pattern = new Pattern { Expanding = () => throw failure };
        Exception actual = Assert.ThrowsAny<Exception>(() =>
            UiaSelection.ReadExpanded(pattern, () => "value", default));
        Assert.Same(failure, actual);
        Assert.False(WindowsOperationErrors.Contains(actual));
    }

    [Fact]
    public void RequiredReferenceAndOptionalNullHaveDifferentContracts()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            WindowsOperationErrors.ProviderReference<object>(() => null!));
        Assert.True(WindowsOperationErrors.Contains(failure));
        Assert.Null(WindowsOperationErrors.Provider<object?>(() => null));
    }
}
