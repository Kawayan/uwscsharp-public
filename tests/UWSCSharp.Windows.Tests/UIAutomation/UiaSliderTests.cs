using System.Diagnostics;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class UiaSliderTests
{
    private sealed class PreparationBackend : IUiaPreparationBackend
    {
        internal int Waits;
        internal int Pumps;

        public void Sleep(int milliseconds, CancellationToken token)
        {
            if (milliseconds == 40)
            {
                Waits++;
            }
        }

        public void Pump(CancellationToken token) => Pumps++;
    }

    [Fact]
    public async Task NativeRootControlIsNotSelectedAsADescendant()
    {
        SliderControlTests.InitializeControls();
        nint slider = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            slider = host.Child("msctls_trackbar32", "", 0, 10);
            Native.SendMessageW(slider, Native.TbmSetPos, 1, 37);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)slider)], default);
        Assert.Equal(Value.Numeric(-999999), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id], default));
        Value result = await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider, [id, Value.Numeric(50)], default);
        Assert.Equal(0, result.Number);
        Assert.True(result.IsLogical);
        Assert.Equal(Value.Numeric(-999999), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id], default));
        var backend = new PreparationBackend();
        var preparation = new UiaMozillaPreparation(backend);
        Assert.Equal(37, await UiaSlider.ExecuteAsync(
            fixture.Root, 1, null, 0, default, preparation));
        Assert.Equal(37, await UiaSlider.ExecuteAsync(
            fixture.Root, 1, null, 0, default, preparation));
        Assert.Equal(4, backend.Waits);
        Assert.Equal(160, backend.Pumps);
    }

    private sealed class Range : IUiaRangeValue
    {
        internal double Current = 37;
        internal double Min = -20;
        internal double Max = 120;
        internal bool ReadOnly;
        internal Action? Read;
        internal Action<double>? Set;
        internal readonly List<double> Calls = [];

        public void SetValue(double value)
        {
            Calls.Add(value);
            Set?.Invoke(value);
        }

        public double Value()
        {
            Read?.Invoke();
            return Current;
        }

        public bool IsReadOnly() => ReadOnly;

        public double Maximum() => Max;

        public double Minimum() => Min;

        public double LargeChange() => 10;
    }

    [Fact]
    public void CancellationBeforeMutationPreservesTheTokenAndPosition()
    {
        using var cancellation = new CancellationTokenSource();
        var range = new Range { Read = cancellation.Cancel };
        OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
            UiaSlider.SetRange(range, true, 50, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Empty(range.Calls);
        Assert.Equal(37, range.Current);
    }

    [Theory]
    [InlineData(37.9, 37)]
    [InlineData(-37.9, -37)]
    public void GetterTruncatesWithoutMutatingTheProvider(double value, int expected)
    {
        var range = new Range { Current = value };
        Assert.Equal(expected, UiaSlider.Range(null!, range, 0, null, 0, default));
        Assert.Empty(range.Calls);
        Assert.Equal(value, range.Current);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2147483648d)]
    [InlineData(-2147483649d)]
    public void GetterRejectsNonfiniteAndOverflowValues(double value)
    {
        var range = new Range { Current = value };
        Exception failure = Assert.ThrowsAny<Exception>(() =>
            UiaSlider.Range(null!, range, 0, null, 0, default));
        Assert.True(WindowsOperationErrors.Contains(failure));
        Assert.Empty(range.Calls);
    }

    [Fact]
    public void GetterRetainsClientFailureAndCancellationToken()
    {
        var expected = new InvalidOperationException("取得境界の失敗です。");
        var range = new Range { Read = () => throw expected };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            UiaSlider.Range(null!, range, 0, null, 0, default)));
        Assert.True(WindowsOperationErrors.Contains(expected));
        using var cancellation = new CancellationTokenSource();
        range.Read = cancellation.Cancel;
        OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
            UiaSlider.Range(null!, range, 0, null, 0, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Empty(range.Calls);
    }

    [Fact]
    public void CancellationAfterMutationIsNotReportedAsSuccessOrRolledBack()
    {
        using var cancellation = new CancellationTokenSource();
        var range = new Range();
        range.Set = value =>
        {
            range.Current = value;
            cancellation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() =>
            UiaSlider.SetRange(range, true, 50, cancellation.Token));
        Assert.Equal([50d], range.Calls);
        Assert.Equal(50, range.Current);
    }

    [Theory]
    [InlineData(double.NaN, 120)]
    [InlineData(-20, double.PositiveInfinity)]
    [InlineData(120, -20)]
    public void MalformedProviderRangesAreRejectedBeforeMutation(double minimum, double maximum)
    {
        var range = new Range { Min = minimum, Max = maximum };
        Assert.Throws<InvalidOperationException>(() =>
            UiaSlider.SetRange(range, true, 50, default));
        Assert.Empty(range.Calls);
    }

    [Fact]
    public void CompensationAndSameValueAreDistinguishedWithoutRetry()
    {
        var range = new Range();
        Assert.Equal(1, UiaSlider.SetRange(range, true, 37, default));
        Assert.Empty(range.Calls);
        Assert.Equal(0, UiaSlider.SetRange(range, true, 50, default));
        Assert.Equal([50d], range.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledAndReadOnlyProvidersAreNotMutated(bool enabled, bool readOnly)
    {
        var range = new Range { ReadOnly = readOnly };
        Assert.Equal(0, UiaSlider.SetRange(range, enabled, 50, default));
        Assert.Empty(range.Calls);
    }

    [Fact]
    public void ProviderFailureIsMarkedAndNotRetried()
    {
        var expected = new InvalidOperationException("専用providerの設定失敗");
        var range = new Range { Set = _ => throw expected };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            UiaSlider.SetRange(range, true, 50, default)));
        Assert.True(WindowsOperationErrors.Contains(expected));
        Assert.Equal([50d], range.Calls);
    }

    [Fact]
    public async Task RealProviderCoversPublicRoutingGeometryAndScrollAxes()
    {
        string fixture = FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("--sliders");
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            long handle = long.Parse(ready!);
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
                [Value.Numeric(handle)], default);

            async Task<double> Get(int number = 1, int item = 0) =>
                (await session.InvokeAsync(BuiltinFunctionNames.GetSlider,
                    [id, Value.Numeric(number), Value.Numeric(item)], default)).Number;

            async Task<double> Set(int value, int number = 1) =>
                (await session.InvokeAsync(BuiltinFunctionNames.SetSlider,
                    [id, Value.Numeric(value), Value.Numeric(number)], default)).Number;

            async Task<string?> Command(string command)
            {
                await process.StandardInput.WriteLineAsync(command);
                return await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(37, await Get());
            Assert.Equal(61, await Get(2));
            Assert.Equal(61, await Get(-2));
            Assert.Equal(81, await Get(3));
            Assert.Equal(42, await Get(4));
            Assert.Equal(24, await Get(5));
            Assert.Equal(37, await Set(-10000000));
            Assert.Equal("37,61,81,24,42;;", await Command("state"));
            Assert.Equal(-20, await Get(1, 1));
            Assert.Equal(120, await Get(1, 2));
            Assert.Equal(10, await Get(1, 3));
            Assert.Equal(1, await Get(3, 4));
            Assert.Equal(0, await Get(1, 4));
            string[] rectangles = Assert.IsType<string>(
                await Command("geometry")).Split(';');
            for (int number = 1; number <= 5; number++)
            {
                int index = Math.Min(number - 1, 3);
                string[] coordinates = rectangles[index].Split(',');
                for (int item = 5; item <= 8; item++)
                {
                    Assert.Equal(int.Parse(coordinates[item - 5]),
                        await Get(number, item));
                }
            }
            Assert.Equal(1, await Set(50));
            Assert.Equal(50, await Get());
            Assert.Equal(0, await Set(50, 2));
            Assert.Equal(61, await Get(2));
            Assert.Equal(1, await Set(int.MaxValue));
            Assert.Equal(120, await Get());
            Assert.Equal(1, await Set(int.MinValue));
            Assert.Equal(-20, await Get());
            Assert.Equal(0, await Set(50, int.MinValue));
            Assert.Equal(-999999, await Get(int.MinValue));
            Assert.Equal("OK", await Command("pane"));
            Assert.Equal(1, await Set(60, 1));
            Assert.Equal(60, await Get(1));
            Assert.Equal(24, await Get(2));
            Assert.Equal(1, await Set(70, 2));
            Assert.Equal(60, await Get(1));
            Assert.Equal(70, await Get(2));
            Assert.Equal(25, await Get(1, 3));
            Assert.Equal(50, await Get(2, 3));
            Assert.Equal(1, await Get(1, 4));
            Assert.Equal(0, await Get(2, 4));
            Assert.Equal(1, await Set(999));
            Assert.Equal(100, await Get());
            Assert.Equal("OK", await Command("fraction"));
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
