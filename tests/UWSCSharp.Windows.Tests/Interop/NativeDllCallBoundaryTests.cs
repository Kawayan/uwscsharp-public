using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackへ整数値を渡すだけで、ポインター・画面・入力には触れません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllCallBoundaryTests
{
    [ThreadStatic]
    private static Observation? directObservation;

    [ThreadStatic]
    private static Barrier? directBarrier;

    [Fact]
    public async Task DifferentArgumentWidthsRemainDistinctAcrossCollections()
    {
        using var ending = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IntegerCall integerCallback = value => value + 1;
        nint integerAddress = Marshal.GetFunctionPointerForDelegate(integerCallback);
        Task<int> otherSignature = Task.Run(() =>
        {
            int calls = 0;
            while (!ending.IsCancellationRequested && calls < 4096)
            {
                var integerSite = new NativeDllCallSite(typeof(int), typeof(int));
                Assert.Equal(11, integerSite.Invoke(integerAddress, 10));
                calls++;
                started.TrySetResult();
            }

            return calls;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int iteration = 0; iteration < 64; iteration++)
            {
                using var current = new ThreadLocal<Observation?>();
                using var barrier = new Barrier(2, _ =>
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced,
                        blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                });
                NativeCall callback = received =>
                {
                    Observation? observation = current.Value;
                    if (observation is null)
                    {
                        return int.MinValue;
                    }

                    observation.Received = received;
                    observation.Calls++;
                    return barrier.SignalAndWait(TimeSpan.FromSeconds(10)) ? 11 : -1;
                };
                nint address = Marshal.GetFunctionPointerForDelegate(callback);
                var site = new NativeDllCallSite(typeof(int), typeof(nint));
                try
                {
                    var observations = new Observation[2];
                    Parallel.For(0, 2, index =>
                    {
                        Observation observation = CreateObservation(index);
                        current.Value = observation;
                        observation.Result = site.Invoke(address, observation.Expected);
                        observations[index] = observation;
                    });
                    string details = string.Join("; ", observations.Select(value =>
                        $"sent=0x{value.Expected:x}, received=0x{value.Received:x}, "
                        + $"result={value.Result}, calls={value.Calls}"));
                    Assert.True(observations.All(value => value.Received == value.Expected
                            && value.Calls == 1 && value.Result is int result && result == 11),
                        $"mixed, iteration={iteration}, runtime={Environment.Version}; {details}");
                }
                finally
                {
                    GC.KeepAlive(callback);
                }
            }
        }
        finally
        {
            ending.Cancel();
            Assert.True(await otherSignature > 0);
            GC.KeepAlive(integerCallback);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void DirectUnmanagedEntryDeliversTheSameValue(bool collect)
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            using var barrier = new Barrier(2, _ =>
            {
                if (collect)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced,
                        blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }
            });
            nint address = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&DirectCallback;
            var site = new NativeDllCallSite(typeof(int), typeof(nint));
            foreach (bool emitted in new[] { false, true })
            {
                var observations = new Observation[2];
                Parallel.For(0, 2, index =>
                {
                    Observation observation = CreateObservation(index);
                    directObservation = observation;
                    directBarrier = barrier;
                    try
                    {
                        observation.Result = emitted
                            ? site.Invoke(address, observation.Expected)
                            : CompiledInvoke(address, observation.Expected);
                        observations[index] = observation;
                    }
                    finally
                    {
                        directObservation = null;
                        directBarrier = null;
                    }
                });
                string details = string.Join("; ", observations.Select(value =>
                    $"sent=0x{value.Expected:x}, received=0x{value.Received:x}, "
                    + $"result={value.Result}, calls={value.Calls}, "
                    + $"thread={value.CallerThread}/{value.CallbackThread}, "
                    + $"stack=[0x{value.StackLow:x},0x{value.StackHigh:x}), "
                    + $"receivedInStack={value.ReceivedInStack}"));
                Assert.True(observations.All(value => value.Received == value.Expected
                        && value.Calls == 1 && value.Result is int result && result == 11),
                    $"direct, iteration={iteration}, emitted={emitted}, collect={collect}, "
                    + $"runtime={Environment.Version}; {details}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DirectCallback(nint received)
    {
        Observation? observation = directObservation;
        Barrier? barrier = directBarrier;
        if (observation is null || barrier is null)
        {
            return int.MinValue;
        }

        observation.Received = received;
        observation.Calls++;
        observation.CallbackThread = Environment.CurrentManagedThreadId;
        return barrier.SignalAndWait(TimeSpan.FromSeconds(10)) ? 11 : -1;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompiledAndEmittedCallsDeliverTheSameValue(
        bool collect, bool dynamicFirst)
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            using var current = new ThreadLocal<Observation?>();
            var unknownThreads = new ConcurrentQueue<int>();
            using var barrier = new Barrier(2, _ =>
            {
                if (collect)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced,
                        blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }
            });
            NativeCall callback = received =>
            {
                Observation? observation = current.Value;
                if (observation is null)
                {
                    unknownThreads.Enqueue(Environment.CurrentManagedThreadId);
                    return int.MinValue;
                }

                observation.Received = received;
                observation.Calls++;
                observation.CallbackThread = Environment.CurrentManagedThreadId;
                return barrier.SignalAndWait(TimeSpan.FromSeconds(10)) ? 11 : -1;
            };
            nint address = Marshal.GetFunctionPointerForDelegate(callback);
            var site = new NativeDllCallSite(typeof(int), typeof(nint));
            try
            {
                foreach (bool emitted in new[] { dynamicFirst, !dynamicFirst })
                {
                    var observations = new Observation[2];
                    Parallel.For(0, 2, index =>
                    {
                        Observation observation = CreateObservation(index);
                        current.Value = observation;
                        observation.Result = emitted
                            ? site.Invoke(address, observation.Expected)
                            : CompiledInvoke(address, observation.Expected);
                        observations[index] = observation;
                        current.Value = null;
                    });
                    string details = string.Join("; ", observations.Select(value =>
                        $"sent=0x{value.Expected:x}, received=0x{value.Received:x}, "
                        + $"result={value.Result}, calls={value.Calls}, "
                        + $"thread={value.CallerThread}/{value.CallbackThread}, "
                        + $"stack=[0x{value.StackLow:x},0x{value.StackHigh:x}), "
                        + $"receivedInStack={value.ReceivedInStack}"));
                    Assert.True(unknownThreads.IsEmpty
                        && observations.All(value => value.Received == value.Expected
                            && value.Calls == 1 && value.Result is int result && result == 11),
                        $"iteration={iteration}, emitted={emitted}, collect={collect}, "
                        + $"dynamicFirst={dynamicFirst}, runtime={Environment.Version}; {details}");
                }
            }
            finally
            {
                GC.KeepAlive(callback);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe int CompiledInvoke(nint address, nint value) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)address)(value);

    private static Observation CreateObservation(int index)
    {
        GetCurrentThreadStackLimits(out nuint low, out nuint high);
        Assert.True(low < high, "現在のthreadのstack範囲を取得できませんでした。");
        return new Observation
        {
            Expected = new nint(0x123456781000L + index * 0x100),
            CallerThread = Environment.CurrentManagedThreadId,
            StackLow = low,
            StackHigh = high
        };
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetCurrentThreadStackLimits(
        out nuint lowLimit, out nuint highLimit);

    private sealed class Observation
    {
        internal nint Expected;
        internal nint Received;
        internal int Calls;
        internal int CallerThread;
        internal int CallbackThread;
        internal object? Result;
        internal nuint StackLow;
        internal nuint StackHigh;

        internal bool ReceivedInStack => unchecked((nuint)Received) >= StackLow
            && unchecked((nuint)Received) < StackHigh;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NativeCall(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IntegerCall(int value);
}
