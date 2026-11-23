using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// COM初期化とpumpは偽物へ差し替え、画面・機器・権限は操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorPublicContractTests
{
    [Theory]
    [InlineData(20, 5)]
    [InlineData(1, 11)]
    [InlineData(40, 20)]
    [InlineData(64, 258)]
    public async Task ScriptConstantsAndTypesReachTheSharedSession(int selector, int type)
    {
        var backend = new Backend();
        var apartment = new Apartment();
        int creates = 0;
        WindowsAutomation automation = Create(backend, apartment, () => creates++);
        await using IWindowsAutomationSession root = automation.CreateSession();
        await using IWindowsAutomationSession fork = root.Fork();
        Value value = await root.InvokeAsync("SENSOR", [Value.Numeric(selector)], default);
        Value again = await fork.InvokeAsync("SENSOR", [Value.Numeric(selector)], default);
        Assert.Equal(value, again);
        Assert.Equal(1, creates);
        Value kind = await root.InvokeAsync("VARTYPE", [value], default);
        Assert.Equal(type, kind.Number);
        if (selector == 40)
        {
            Assert.Equal(long.MaxValue, value.GetUnchangedNativeScalar());
        }

        await fork.DisposeAsync();
        await root.DisposeAsync();
        Assert.Equal(1, backend.Disposes);
        Assert.Equal(1, apartment.Uninitializes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(68)]
    public async Task UnknownSelectorRefreshesButDoesNotRead(int selector)
    {
        var backend = new Backend();
        WindowsAutomation automation = Create(backend, new Apartment());
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value value = await session.InvokeAsync("SENSOR", [Value.Numeric(selector)], default);
        Assert.Equal(Value.Empty, value);
        Assert.Equal(1, backend.Refreshes);
        Assert.Equal(0, backend.Reads);
    }

    [Fact]
    public async Task MissingDeviceStaysEmptyIncludingBarcode()
    {
        var backend = new Backend { Found = false };
        WindowsAutomation automation = Create(backend, new Apartment());
        await using IWindowsAutomationSession session = automation.CreateSession();
        Assert.Equal(Value.Empty, await session.InvokeAsync("SENSOR", [Value.Numeric(41)], default));
    }

    [Fact]
    public async Task ScriptDistinguishesMissingDeviceFromFalsePresence()
    {
        var backend = new Backend { Present = false };
        List<string> output = [];
        await new Interpreter(windows: Create(backend, new Apartment()))
            .RunAsync("""
                present = SENSOR(SNSR_Biometric_HumanPresense)
                missing = SENSOR(0)
                PRINT VARTYPE(present)
                PRINT present + 0
                PRINT VARTYPE(missing)
                """, output.Add);
        Assert.Equal(["11", "0", "0"], output);
        Assert.Equal(1, backend.Reads);
    }

    [Fact]
    public async Task CopiedNumericNaNRetainsItsBits()
    {
        const long bits = unchecked((long)0xfff8000000000000);
        var backend = new Backend { Number = BitConverter.Int64BitsToDouble(bits) };
        await using IWindowsAutomationSession session = Create(backend, new Apartment()).CreateSession();
        Value value = await session.InvokeAsync("SENSOR", [Value.Numeric(20)], default);
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(value.Number));
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Assert.IsType<double>(value.GetUnchangedNativeScalar())));
    }

    [Fact]
    public async Task InterpreterPublishesTheConstantAndCatchesNativeFailures()
    {
        var backend = new Backend();
        var lines = new List<string>();
        await new Interpreter(windows: Create(backend, new Apartment())).RunAsync(
            "PRINT SNSR_LIGHT_LUX\nPRINT SENSOR(SNSR_LIGHT_LUX)", lines.Add);
        Assert.Equal(new[] { "20", "42.5" }, lines);
        backend = new Backend { Failure = new COMException("センサー取得失敗", unchecked((int)0x80070005)) };
        lines.Clear();
        await new Interpreter(windows: Create(backend, new Apartment())).RunAsync(
            "TRY\nPRINT SENSOR(20)\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\"", lines.Add);
        Assert.Equal(new[] { "caught", "after" }, lines);
        Assert.Equal(1, backend.Disposes);
    }

    [Fact]
    public async Task SensorRecoversAfterRefreshAndNotificationFailures()
    {
        int creates = 0;
        var backend = new Backend
        {
            CallbackFailure = new COMException("通知の失敗")
        };
        backend.BeforeRefresh = FailFirstRefresh;
        WindowsAutomation automation = Create(backend, new Apartment(),
            () => creates++);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            FOR attempt = 1 TO 3
                TRY
                    PRINT SENSOR(SNSR_LIGHT_LUX)
                EXCEPT
                    PRINT attempt
                ENDTRY
            NEXT
            """, output.Add);
        Assert.Equal(["1", "2", "42.5"], output);
        Assert.Equal(1, creates);
        Assert.Equal(3, backend.Refreshes);
        Assert.Equal(2, backend.Reads);
        Assert.Equal(1, backend.Disposes);

        void FailFirstRefresh()
        {
            if (backend.Refreshes == 1)
            {
                throw new COMException("列挙の失敗");
            }
        }
    }

    [Fact]
    public async Task InvalidArgumentsDoNotCreateTheSensorSession()
    {
        int creates = 0;
        WindowsAutomation automation = Create(new Backend(), new Apartment(), () => creates++);
        await using IWindowsAutomationSession session = automation.CreateSession();
        foreach (Value[] arguments in new[] { [], new[] { new Value(ValueKind.Omitted) }, [Value.Numeric(1), Value.Numeric(2)] })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("SENSOR", arguments, default).AsTask());
        }

        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task ScriptConvertsSelectorsBeforeCreatingTheSensorSession()
    {
        int creates = 0;
        var backend = new Backend();
        var interpreter = new Interpreter(windows:
            Create(backend, new Apartment(), () => creates++));
        List<string> output = [];
        await interpreter.RunAsync("""
            TRY
                PRINT SENSOR("invalid")
            EXCEPT
                PRINT "invalid"
            ENDTRY
            TRY
                PRINT SENSOR(2147483648)
            EXCEPT
                PRINT "range"
            ENDTRY
            """, output.Add);
        Assert.Equal(["invalid", "range"], output);
        Assert.Equal(0, creates);

        output.Clear();
        await interpreter.RunAsync("""
            PRINT SENSOR("20")
            PRINT SENSOR(20.9)
            PRINT VARTYPE(SENSOR(-2147483648))
            """, output.Add);
        Assert.Equal(["42.5", "42.5", "0"], output);
        Assert.Equal(1, creates);
        Assert.Equal(3, backend.Refreshes);
        Assert.Equal(2, backend.Reads);
    }

    [Fact]
    public async Task RootCancellationStopsTheSharedSessionAfterTheActiveCallReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend
        {
            BeforeRefresh = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var apartment = new Apartment();
        await using IWindowsAutomationSession session = Create(backend, apartment).CreateSession();
        session.ConfigurePause(new ScriptPauseControl(), cancellation.Token);
        Task<Value> query = session.InvokeAsync("SENSOR", [Value.Numeric(20)], default).AsTask();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await cancellation.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, backend.Disposes);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        await session.DisposeAsync();
        Assert.Equal(1, backend.Disposes);
        Assert.Equal(1, apartment.Uninitializes);
    }

    [Fact]
    public async Task AlreadyCanceledRootDoesNotCreateTheLazySession()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int creates = 0;
        await using IWindowsAutomationSession session = Create(new Backend(), new Apartment(),
            () => creates++).CreateSession();
        session.ConfigurePause(null, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync("SENSOR", [Value.Numeric(20)], default).AsTask());
        Assert.Equal(0, creates);
    }

    private static WindowsAutomation Create(Backend backend, Apartment apartment, Action? created = null) =>
        new(_ => throw new InvalidOperationException("物理入力は禁止です。"), swappedMouseButtons: false)
        {
            SensorSessionFactory = () =>
            {
                created?.Invoke();
                return new SensorSession(() => backend, apartment);
            }
        };

    private sealed class Backend : ISensorSessionController
    {
        internal int Refreshes;
        internal int Reads;
        internal int Disposes;
        internal bool Found = true;
        internal bool Present = true;
        internal double Number = 42.5d;
        internal Exception? Failure;
        internal Exception? CallbackFailure;
        internal Action? BeforeRefresh;

        public int Refresh()
        {
            Refreshes++;
            BeforeRefresh?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }

            return unchecked((int)0x80070490);
        }

        public bool TryRead(SensorDataField field, out object? value)
        {
            Reads++;
            value = field.Kind switch
            {
                SensorDataValueKind.Numeric => Number,
                SensorDataValueKind.Boolean => Present,
                SensorDataValueKind.Integer => long.MaxValue,
                SensorDataValueKind.String => "住所",
                SensorDataValueKind.EmptyString => string.Empty,
                _ => null
            };
            return Found;
        }

        public Exception? TakeCallbackFailure() =>
            Interlocked.Exchange(ref CallbackFailure, null);

        public void Dispose() => Disposes++;
    }

    private sealed class Apartment : ISensorApartment
    {
        internal int Uninitializes;

        public int Initialize() => 0;

        public void Pump()
        {
        }

        public void Uninitialize() => Uninitializes++;
    }
}
