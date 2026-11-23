using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ProviderPointerRouteTests
{
    [Fact]
    public async Task UiaListScrollsBeforeBackgroundPhysicalClick()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll"));
        start.ArgumentList.Add("--list-actions");
        start.ArgumentList.Add("--pointer-scroll");
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(ready, out long handle), ready);
            var platform = new Platform();
            List<uint> inputs = [];
            await using IWindowsAutomationSession session = new WindowsAutomation(
                input => inputs.Add(input.Data.Mouse.Flags), listSelectionPlatform: platform,
                swappedMouseButtons: false).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric(handle)], default);
            await process.StandardInput.WriteLineAsync("snapshot");
            Assert.Equal("list=0;combo=0;expanded=False;far-visible=False",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("far"), Value.Numeric(1073741826 | 512 | 2048),
                    Value.Numeric(1)], default).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new uint[] { 2, 4 }, inputs);
            Assert.NotEmpty(platform.Points);
            await process.StandardInput.WriteLineAsync("snapshot");
            Assert.Equal("list=0;combo=0;expanded=False;far-visible=True",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
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

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal List<Native.Point> Points { get; } = [];

        internal int Foregrounds
        {
            get;
            private set;
        }

        public override void Foreground(nint window) => Foregrounds++;

        public override void MoveCursor(Native.Point point) => Points.Add(point);

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(0, 2048, 1, 2)]
    [InlineData(0, 2048, 0, 0)]
    [InlineData(3, 2048, 1, 2)]
    [InlineData(3, 2048, 0, 2)]
    [InlineData(3, 4096, 0, 2)]
    [InlineData(3, 8192, 1, 4)]
    [InlineData(0, 1024, 1, 0)]
    [InlineData(3, 1024, 0, 0)]
    public async Task UiaPointerUsesInputInsteadOfAnAdditionalSemanticAction(
        int style, int mouseFlags, int request, int expectedInputs)
    {
        int clicks = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("BUTTON", "target", style, 10);
            host.Clicked = (_, _) => Interlocked.Increment(ref clicks);
        });
        var platform = new Platform();
        List<uint> inputs = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(
            input => inputs.Add(input.Data.Mouse.Flags), listSelectionPlatform: platform,
            swappedMouseButtons: false).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(1073741825 | 512 | mouseFlags),
                Value.Numeric(request)], default));
        Assert.Equal(expectedInputs, inputs.Count);
        Assert.NotEmpty(platform.Points);
        if (expectedInputs != 0)
        {
            Assert.Equal(0, clicks);
            Assert.Equal(1, platform.Foregrounds);
        }
    }

    [Theory]
    [InlineData(1024, false, 1, 0, 1)]
    [InlineData(1024, false, 0, 0, 0)]
    [InlineData(2048, false, 1, 2, 0)]
    [InlineData(2048, false, 0, 0, 0)]
    [InlineData(4096, true, 0, 2, 0)]
    [InlineData(8192, false, 1, 4, 0)]
    public async Task AccPointerRetainsTheRequestedStateGate(int mouseFlags,
        bool current, int request, int expectedInputs, int expectedActions)
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(43, current ? 0x10 : 0, "target", "")
        {
            TakingFocus = _ => { },
            PerformingAction = () => actions++
        };
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        var platform = new Platform();
        List<uint> inputs = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(
            input => inputs.Add(input.Data.Mouse.Flags), listSelectionPlatform: platform,
            swappedMouseButtons: false).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(32768 | 512 | mouseFlags),
                Value.Numeric(request)], default));
        Assert.Equal(expectedInputs, inputs.Count);
        Assert.Equal(expectedActions, actions);
        Assert.NotEmpty(platform.Points);
    }
}
