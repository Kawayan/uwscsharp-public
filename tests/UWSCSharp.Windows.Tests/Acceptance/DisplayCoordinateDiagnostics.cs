using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class DisplayCoordinateDiagnostics(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    [Trait("Category", "SandboxOnly")]
    public void ComparePhysicalAndScopedCursorReads(int awareness)
    {
        Assert.True(GetPhysicalCursorPos(out Native.Point saved));
        nint previous = Native.SetThreadDpiAwarenessContext(awareness);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(SetPhysicalCursorPos(37, 61));
            Assert.True(GetPhysicalCursorPos(out Native.Point physical));
            Assert.True(Native.GetCursorPos(out Native.Point logical));
            Native.Point scoped;
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.GetCursorPos(out scoped));
            }

            Native.Point adapter = new NativeDisplayPlatform().CursorPosition();
            output.WriteLine(
                $"context={awareness} physical={physical.X},{physical.Y} " +
                $"logical={logical.X},{logical.Y} scoped={scoped.X},{scoped.Y} " +
                $"adapter={adapter.X},{adapter.Y} dpi={GetDpiForSystem()}");
            Assert.True(SetPhysicalCursorPos(73, 29));
            Assert.True(GetPhysicalCursorPos(out physical));
            adapter = new NativeDisplayPlatform().CursorPosition();
            output.WriteLine(
                $"second physical={physical.X},{physical.Y} adapter={adapter.X},{adapter.Y}");
        }
        finally
        {
            SetPhysicalCursorPos(saved.X, saved.Y);
            Native.SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out Native.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
