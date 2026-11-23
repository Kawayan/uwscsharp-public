using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 公開scriptから実COMとmessage pumpを使うため、Sandboxだけで実行します。
[Trait("Category", "SandboxOnly")]
public sealed class SensorScriptIsolationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublicScriptReturnsEmptyWithoutDevicesAndReleasesTheSta()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(Directory.Exists(@"C:\ui-input"));
        uint dpi = GetDpiForSystem();
        int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        output.WriteLine($"caller-dpi-api={dpi}; caller-awareness={awareness}");
        Assert.True(dpi > 0);
        Assert.InRange(awareness, 0, 2);
        var apartment = new Apartment(output);
        int creates = 0;
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), swappedMouseButtons: false)
        {
            SensorSessionFactory = () =>
            {
                creates++;
                return new SensorSession(apartment: apartment);
            }
        };
        var lines = new List<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await new Interpreter(windows: automation).RunAsync(
            "PRINT SNSR_LIGHT_LUX\nPRINT VARTYPE(SENSOR(SNSR_LIGHT_LUX))\nPRINT VARTYPE(SENSOR(0))\nPRINT VARTYPE(SENSOR(SNSR_SCANNER_BARCODEDATA))",
            lines.Add, cancellation.Token);
        Assert.Equal(new[] { "20", "0", "0", "0" }, lines);
        Assert.Equal(1, creates);
        Assert.Equal(apartment.InitializedThread, apartment.UninitializedThread);
        Assert.Equal(1, apartment.Uninitializes);
        output.WriteLine($"script-lines={string.Join(",", lines)}; creates={creates}; owner={apartment.InitializedThread}; init=0x{apartment.InitializeResult:x8}; uninitialize=1");
    }

    private sealed class Apartment(ITestOutputHelper output) : ISensorApartment
    {
        internal int InitializeResult;
        internal int InitializedThread;
        internal int UninitializedThread;
        internal int Uninitializes;

        public int Initialize()
        {
            InitializedThread = Environment.CurrentManagedThreadId;
            uint dpi = GetDpiForSystem();
            int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            output.WriteLine($"owner-dpi-api={dpi}; owner-awareness={awareness}");
            Assert.True(dpi > 0);
            Assert.InRange(awareness, 0, 2);
            InitializeResult = SensorApartment.Instance.Initialize();
            return InitializeResult;
        }

        public void Pump() => SensorApartment.Instance.Pump();

        public void Uninitialize()
        {
            UninitializedThread = Environment.CurrentManagedThreadId;
            Uninitializes++;
            SensorApartment.Instance.Uninitialize();
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
}
