using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class UiaPreparationTests
{
    private sealed class Backend : IUiaPreparationBackend
    {
        internal readonly List<string> Calls = [];
        internal Action? OnPump;
        private readonly int thread = Environment.CurrentManagedThreadId;

        public void Sleep(int milliseconds, CancellationToken token)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Calls.Add($"Sleep:{milliseconds}");
        }

        public void Pump(CancellationToken token)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Calls.Add("Pump");
            OnPump?.Invoke();
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    public void PreparesChangedTargetsOnSameWorkerAndKeepsSessionIsolation()
    {
        var backend = new Backend();
        var preparation = new UiaMozillaPreparation(backend);
        int thread = Environment.CurrentManagedThreadId;
        int Read(Action? prepare)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            backend.Calls.Add("Warmup");
            prepare?.Invoke();
            backend.Calls.Add("Read");
            return 42;
        }

        Assert.Equal(42, preparation.Execute(1, Read, default));
        Assert.Equal("Warmup", backend.Calls[0]);
        Assert.Equal("Read", backend.Calls[^1]);
        Assert.Equal(4, backend.Calls.Count(call => call == "Sleep:40"));
        Assert.Equal(160, backend.Calls.Count(call => call == "Sleep:0"));
        Assert.Equal(160, backend.Calls.Count(call => call == "Pump"));
        for (int cycle = 0; cycle < 4; cycle++)
        {
            Assert.Equal("Sleep:40", backend.Calls[1 + 81 * cycle]);
        }
        backend.Calls.Clear();
        preparation.Execute(1, Read, default);
        Assert.Equal(["Warmup", "Read"], backend.Calls);
        backend.Calls.Clear();
        preparation.Execute(2, Read, default);
        Assert.Contains("Pump", backend.Calls);
        backend.Calls.Clear();
        new UiaMozillaPreparation(backend).Execute(2, Read, default);
        Assert.Contains("Pump", backend.Calls);
    }

    [Fact]
    [Trait("Category", "Headless")]
    public void CancellationBeforeEntryDoesNotRegisterTarget()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var preparation = new UiaMozillaPreparation(new Backend());
        Assert.ThrowsAny<OperationCanceledException>(() => preparation.Execute<int>(1,
            _ => throw new InvalidOperationException("取消後の初期化は禁止です。"), cancellation.Token));
        preparation.Execute(1, prepare =>
        {
            Assert.NotNull(prepare);
            return 0;
        }, default);
    }

    [Fact]
    [Trait("Category", "Headless")]
    public void FailureAfterTargetRegistrationDoesNotRetrySameHandle()
    {
        var backend = new Backend();
        var preparation = new UiaMozillaPreparation(backend);
        Assert.Throws<InvalidOperationException>(() => preparation.Execute<int>(1,
            _ => throw new InvalidOperationException("初期化失敗"), default));
        Assert.Empty(backend.Calls);
        preparation.Execute(1, prepare =>
        {
            Assert.Null(prepare);
            return 0;
        }, default);
    }

    [Fact]
    [Trait("Category", "Headless")]
    public void CancellationDuringPumpStopsBeforeSelectionAndRetainsTarget()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend { OnPump = cancellation.Cancel };
        var preparation = new UiaMozillaPreparation(backend);
        Assert.ThrowsAny<OperationCanceledException>(() => preparation.Execute<int>(1, prepare =>
        {
            prepare!();
            throw new InvalidOperationException("取消後に取得してはいけません。");
        }, cancellation.Token));
        Assert.Equal(1, backend.Calls.Count(call => call == "Pump"));
        preparation.Execute(1, prepare =>
        {
            Assert.Null(prepare);
            return 0;
        }, default);
    }

    [Fact]
    public async Task NativePumpProcessesOwnedWindowQueueAndStopsOnQuit()
    {
        Exception? failure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var fixture = new ButtonControlTests.Fixture();
                int received = 0;
                fixture.Command = (_, _) => received++;
                Assert.True(PostMessageW(fixture.Root, 0x111, 42, fixture.Root));
                var backend = new NativeUiaPreparationBackend();
                backend.Pump(default);
                Assert.Equal(1, received);
                PostQuitMessage(0);
                Assert.ThrowsAny<OperationCanceledException>(() => backend.Pump(default));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        owner.IsBackground = true;
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
        Assert.True(await Task.Run(() => owner.Join(TimeSpan.FromSeconds(5))));
        Assert.Null(failure);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}
