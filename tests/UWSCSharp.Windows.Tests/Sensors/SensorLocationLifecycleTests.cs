using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽のmanager・機器・通知を使い、実COM初期化・GUI・実機器を呼びません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorLocationLifecycleTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    public void EveryLocationSelectorStartsReadsAndStopsOnlyTheFirstDevice(int selector)
    {
        SensorControllerTests.Api api = Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        Assert.Empty(api.DeviceCalls);
        Assert.True(controller.TryRead(Field(selector), out _));
        Assert.Equal(new[] { "start:201", "read:201", "stop:201" }, api.DeviceCalls);
        Assert.False(api.HasDevice(201));
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void EachReadUsesAFreshSinkAndClosesThePreviousOne()
    {
        SensorControllerTests.Api api = Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        SensorEventSink? first = null;
        SensorEventSink? second = null;
        api.OnOperation = call =>
        {
            if (call == "set:201" && api.HasDevice(201))
            {
                if (first is null)
                {
                    first = api.Device(201);
                }
                else
                {
                    second = api.Device(201);
                }
            }
        };
        Assert.True(controller.TryRead(Field(60), out _));
        Assert.True(controller.TryRead(Field(60), out _));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Guid id = SensorControllerTests.Api.Id(201);
        Assert.Equal(0, first.OnLeave(in id));
        Assert.True(controller.TryRead(Field(60), out _));
        controller.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFailureStillStopsAndKeepsTheOriginalFailure(bool stopFails)
    {
        SensorControllerTests.Api api = Api();
        var failure = new InvalidOperationException("取得例外");
        api.OnOperation = call =>
        {
            if (call == "data:201")
            {
                throw failure;
            }
        };
        api.ClearFailure = stopFails ? 201 : 0;
        using SensorController controller = Create(api);
        controller.Refresh();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            controller.TryRead(Field(60), out _));
        Assert.Same(failure, error);
        Assert.Equal(stopFails, error.Data.Contains("UWSCSharp.SensorControllerCleanup"));
        Assert.Equal(new[] { "start:201", "read:201", "stop:201" }, api.DeviceCalls);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void StartFailureReleasesItsPartialConnectionWithoutReading()
    {
        SensorControllerTests.Api api = Api();
        api.SetFailure = 201;
        using SensorController controller = Create(api);
        controller.Refresh();
        Assert.Equal(SensorControllerTests.Api.Failure,
            Assert.Throws<UnauthorizedAccessException>(() => controller.TryRead(Field(60), out _)).HResult);
        Assert.Equal(new[] { "start:201", "stop:201" }, api.DeviceCalls);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void ANotificationReadReusesTheActiveConnectionAndFetchesItsOwnReport()
    {
        SensorControllerTests.Api api = Api();
        SensorController? current = null;
        int notifications = 0;
        using SensorController controller = Create(api, () =>
        {
            notifications++;
            Assert.True(current!.TryRead(Field(60), out object? value));
            Assert.Equal(1.5, value);
        });
        current = controller;
        controller.Refresh();
        api.OnOperation = call =>
        {
            if (call == "set:201" && api.HasDevice(201))
            {
                Assert.Equal(0, api.Device(201).OnDataUpdated(201, 301));
            }
        };
        Assert.True(controller.TryRead(Field(60), out object? result));
        Assert.Equal(1.5, result);
        Assert.Equal(1, notifications);
        Assert.Null(controller.TakeCallbackFailure());
        Assert.Equal(new[] { "start:201", "read:201", "read:201", "stop:201" }, api.DeviceCalls);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void LeaveDuringStartDoesNotReadTheRemovedRegistration()
    {
        SensorControllerTests.Api api = Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        api.OnOperation = call =>
        {
            if (call == "set:201" && api.HasDevice(201))
            {
                Guid id = SensorControllerTests.Api.Id(201);
                Assert.Equal(0, api.Device(201).OnLeave(in id));
            }
        };
        Assert.False(controller.TryRead(Field(60), out object? value));
        Assert.Null(value);
        Assert.Equal(new[] { "start:201", "stop:201" }, api.DeviceCalls);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void DisposeDuringStartIsReportedAfterTheConnectionIsReleased()
    {
        SensorControllerTests.Api api = Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        api.OnOperation = call =>
        {
            if (call == "set:201" && api.HasDevice(201))
            {
                controller.Dispose();
            }
        };
        Assert.Throws<ObjectDisposedException>(() => controller.TryRead(Field(60), out _));
        api.AssertBalanced();
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), true)]
    [InlineData(unchecked((int)0x80004001), false)]
    [InlineData(unchecked((int)0x80004003), false)]
    public async Task NativeMappedFailuresCanBeCaughtByTheScript(int result, bool start)
    {
        SensorControllerTests.Api api = Api();
        api.SetFailure = start ? 201 : 0;
        api.DataResult = start ? 0 : result;
        api.CategoryOverride = start ? Field(60).CategoryId : null;
        int selector = start ? 60 : 20;
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), swappedMouseButtons: false)
        {
            SensorSessionFactory = () => new SensorSession(
                () => Create(api), new Apartment())
        };
        var lines = new List<string>();
        await new Interpreter(windows: automation).RunAsync(
            $"TRY\nPRINT SENSOR({selector})\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\"", lines.Add);
        Assert.Equal(new[] { "caught", "after" }, lines);
        api.AssertBalanced();
    }

    [Fact]
    public void FailedStopCanBeFollowedByAnotherReadWithoutLeakingTheOldSink()
    {
        SensorControllerTests.Api api = Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        api.ClearFailure = 201;
        Assert.Equal(SensorControllerTests.Api.Failure,
            Assert.Throws<UnauthorizedAccessException>(() => controller.TryRead(Field(60), out _)).HResult);
        api.ClearFailure = 0;
        Assert.True(controller.TryRead(Field(60), out _));
        Assert.False(api.HasDevice(201));
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public async Task ScriptCanReadAgainAfterLocationStopFails()
    {
        SensorControllerTests.Api api = Api();
        api.ClearFailure = 201;
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false)
        {
            SensorSessionFactory = () => new SensorSession(
                () => Create(api), new Apartment())
        };
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            TRY
                PRINT SENSOR(SNSR_Location_Altitude)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT SENSOR(SNSR_Location_Altitude)
            """, text =>
            {
                output.Add(text);
                if (text == "caught")
                {
                    api.ClearFailure = 0;
                }
            });
        Assert.Equal(["caught", "1.5"], output);
        Assert.Equal(["start:201", "read:201", "stop:201",
            "start:201", "read:201", "stop:201"], api.DeviceCalls);
        api.AssertBalanced();
    }

    [Fact]
    public async Task AnUnrelatedManagedFailureIsNotMarkedAsANativeOperation()
    {
        SensorControllerTests.Api api = Api();
        var failure = new NullReferenceException("内部不具合");
        api.OnOperation = call =>
        {
            if (call == "data:201")
            {
                throw failure;
            }
        };
        var automation = new WindowsAutomation(_ => { }, swappedMouseButtons: false)
        {
            SensorSessionFactory = () => new SensorSession(
                () => Create(api), new Apartment())
        };
        await using IWindowsAutomationSession session = automation.CreateSession();
        Assert.Same(failure, await Assert.ThrowsAsync<NullReferenceException>(() =>
            session.InvokeAsync("SENSOR", [Value.Numeric(60)], default).AsTask()));
        Assert.Null(session.GetScriptErrorMessage("SENSOR", failure));
        await session.DisposeAsync();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    public async Task FailedGetDataReturnsThePublicLocationDefaultAndStillStops(
        int selector)
    {
        SensorControllerTests.Api api = Api();
        api.DataResult = unchecked((int)0x80004005);
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false)
        {
            SensorSessionFactory = () => new SensorSession(
                () => Create(api), new Apartment())
        };

        await using (IWindowsAutomationSession session = automation.CreateSession())
        {
            Value value = await session.InvokeAsync(BuiltinFunctionNames.Sensor,
                [Value.Numeric(selector)], default);
            if (selector < 64)
            {
                Assert.Equal(ValueKind.Number, value.Kind);
                Assert.Equal(unchecked((long)0xfff8000000000000),
                    BitConverter.DoubleToInt64Bits(value.Number));
            }
            else
            {
                Assert.Equal(ValueKind.String, value.Kind);
                Assert.Equal(string.Empty, value.Text);
            }
        }

        Assert.Equal(["start:201", "read:201", "stop:201"], api.DeviceCalls);
        api.AssertBalanced();
    }

    private sealed class Apartment : ISensorApartment
    {
        public int Initialize() => 0;

        public void Pump()
        {
        }

        public void Uninitialize()
        {
        }
    }

    private static SensorControllerTests.Api Api() => new()
    {
        CategoryOverride = Field(60).CategoryId
    };

    private static SensorController Create(SensorControllerTests.Api api, Action? updated = null) =>
        new(api, api, api, api, updated: updated);

    private static SensorDataField Field(int selector)
    {
        Assert.True(SensorDataFieldCatalog.TryGet(selector, out SensorDataField field));
        return field;
    }
}
