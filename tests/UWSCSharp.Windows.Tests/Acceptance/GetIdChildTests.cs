using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class GetIdChildTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window, string text);

    [Theory]
    [InlineData("exact")]
    [InlineData("partial")]
    [InlineData("case")]
    [InlineData("nested")]
    [InlineData("empty")]
    [InlineData("omitted")]
    [InlineData("missing")]
    [InlineData("class")]
    public async Task SearchesSelectedParentsDescendants(string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "GETID-" + Guid.NewGuid();
        nint exact = 0;
        nint partial = 0;
        nint nested = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            exact = owner.Child("STATIC", "Document", 0, 10);
            partial = owner.Child("STATIC", "Document-more", 0, 40);
            nested = Native.CreateWindowExW(0, "STATIC", "Nested", 0x50000000,
                0, 0, 60, 20, exact, 0, 0, 0);
            Assert.NotEqual(0, nested);
        }, title: title);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        string childTitle = mode switch
        {
            "partial" => "Document-mo",
            "case" => "document",
            "nested" => "Nested",
            "empty" or "omitted" => "",
            "missing" => "Absent",
            _ => "Document"
        };
        Value child = mode == "omitted"
            ? new Value(ValueKind.Omitted) : Value.String(childTitle);
        Value result = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title.ToLowerInvariant()),
                Value.String(mode == "class" ? "AbsentClass" : "sta"),
                Value.Numeric(0.01), child], default);
        if (mode is "missing" or "class")
        {
            Assert.Equal(-1, result.AsNumber());
            return;
        }

        nint expected = mode switch
        {
            "partial" => partial,
            "nested" => nested,
            "empty" or "omitted" => fixture.Root,
            _ => exact
        };
        Value handle = await session.InvokeAsync(BuiltinFunctionNames.IdToHnd,
            [result], default);
        Assert.Equal((long)expected, handle.AsNumber());
        Value repeated = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title), Value.String(""), Value.Numeric(0.01), child], default);
        Assert.Equal(result, repeated);
    }

    [Fact]
    public async Task MissingChildDoesNotSelectAnotherParentsMatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "GETID-parent-" + Guid.NewGuid();
        await using PumpedControlFixture exact = await PumpedControlFixture.CreateAsync(
            _ => { }, title: title);
        await using PumpedControlFixture partial = await PumpedControlFixture.CreateAsync(
            owner => owner.Child("STATIC", "OtherDocument", 0, 10), title: title + "-more");
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value result = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title), Value.String(""), Value.Numeric(0.01),
                Value.String("OtherDocument")], default);
        Assert.Equal(-1, result.AsNumber());
    }

    [Fact]
    public async Task WaitCanFindRenamedChildAndCancellationInterruptsMissingChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "GETID-wait-" + Guid.NewGuid();
        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            owner => child = owner.Child("STATIC", "Pending", 0, 10), title: title);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Task<Value> search = session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title), Value.String(""), Value.Numeric(-1),
                Value.String("Ready")], default).AsTask();
        Assert.False(search.IsCompleted);
        Assert.True(SetWindowTextW(child, "Ready"));
        Value result = await search.WaitAsync(TimeSpan.FromSeconds(5));
        Value handle = await session.InvokeAsync(BuiltinFunctionNames.IdToHnd,
            [result], default);
        Assert.Equal((long)child, handle.AsNumber());

        using var cancellation = new CancellationTokenSource();
        Task<Value> missing = session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title), Value.String(""), Value.Numeric(-1),
                Value.String("Missing")], cancellation.Token).AsTask();
        Assert.False(missing.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await missing.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
