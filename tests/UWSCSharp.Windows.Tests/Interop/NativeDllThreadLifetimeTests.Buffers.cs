using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeDllThreadLifetimeTests
{
    public static IEnumerable<object[]> BufferCases()
    {
        foreach (string type in new[] { "string", "pchar", "pbyte", "wstring", "pwchar" })
        {
            for (int shape = 0; shape < 3; shape++)
            {
                foreach (bool arrayVar in new[] { false, true })
                {
                    for (int protocol = 0; protocol < 4; protocol++)
                    {
                        foreach (bool externalCall in new[] { false, true })
                        {
                            yield return [type, shape, arrayVar, protocol, externalCall];
                        }
                    }
                }
            }
        }
    }

    // 実DLLのイベントと自プロセスメモリだけを使い、入力操作を拒否します。
    [Theory]
    [MemberData(nameof(BufferCases))]
    public async Task MixedBuffersRemainAliveAndWriteBackBeforeCancellation(
        string type, int shape, bool arrayVar, int protocol, bool externalCall)
    {
        using var loader = new RealLoader(buffered: true);
        using var cancellation = new CancellationTokenSource();
        var stopping = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CapturingAutomation(RealAutomation(loader), stopping);
        var interpreter = new Interpreter(windows: factory);
        var output = new ConcurrentQueue<string>();
        using var input = new RealInput(BufferScript(loader.Path, loader.Prefix,
            type, shape, arrayVar, externalCall), externalCall,
            RealParentScript(loader.Prefix));
        var run = Task.Run(() => interpreter.RunAsync(input.Program,
            new CallbackScriptHost(message =>
            {
                output.Enqueue(message);
                if (message.StartsWith("worker-finally=", StringComparison.Ordinal))
                {
                    loader.WorkerPrinted.TrySetResult();
                }
            }), cancellation.Token));
        Task? closing = null;
        try
        {
            await loader.WaitForCallsAsync();
            loader.AssertLoaded();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            loader.AssertLoaded();
            if (protocol == 1)
            {
                cancellation.Cancel();
                loader.ReleaseAll();
            }
            else if (protocol == 3)
            {
                loader.ReleaseParent.Set();
                await stopping.Task.WaitAsync(Timeout);
                Assert.False(run.IsCompleted);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                loader.AssertLoaded();
                loader.ReleaseChild.Set();
            }
            else
            {
                NativeDllModule.Function? retained = null;
                if (protocol == 2)
                {
                    IWindowsAutomationSession root = Assert.IsAssignableFrom<
                        IWindowsAutomationSession>(factory.Session);
                    INativeDllModuleSource source =
                        Assert.IsAssignableFrom<INativeDllModuleSource>(root);
                    var signature = new NativeDllCallSite(typeof(int),
                        typeof(nint), typeof(nint), typeof(nint), typeof(nint));
                    retained = source.ResolveNativeDll(loader.Path, "Gate", signature);
                    closing = root.DisposeAsync().AsTask();
                    Assert.False(closing.IsCompleted);
                    Assert.Throws<ObjectDisposedException>(() => root.Fork());
                    Assert.Throws<ObjectDisposedException>(() =>
                        source.ResolveNativeDll(loader.Path, "Gate", signature));
                }
                loader.ReleaseChild.Set();
                await loader.WorkerPrinted.Task.WaitAsync(Timeout);
                if (closing is not null)
                {
                    await closing.WaitAsync(Timeout);
                    Assert.Throws<ObjectDisposedException>(() => retained!.Invoke(
                        (nint)0, (nint)0, (nint)0, (nint)0));
                }
                Assert.False(run.IsCompleted);
                loader.AssertLoaded();
                loader.ReleaseParent.Set();
            }
            await ObserveAsync(run, protocol == 1);
            Assert.Equal(ExpectedBufferOutput(type, protocol, externalCall),
                output.ToArray());
            loader.AssertUnloaded();
        }
        finally
        {
            loader.ReleaseAll();
            await ObserveAsync(run, cancellation.IsCancellationRequested);
            if (closing is not null)
            {
                await closing.WaitAsync(Timeout);
            }
        }
    }

    private static string BufferScript(string path, string prefix,
        string type, int shape, bool arrayVar, bool externalCall)
    {
        string array = (arrayVar ? "VAR " : "") + "int[]";
        string arguments = shape == 0
            ? $"{array},VAR {type},VAR int"
            : $"{{byte,{array},VAR {type},VAR int,byte}},longlong,longlong";
        string values = shape == 0 ? "items[0],text,number"
            : "17,items[0],text,number,34,0,0";
        bool wide = type is "wstring" or "pwchar";
        string initial = wide ? "日本語ab" : "abcde";
        string pack = shape == 2 ? " PACK" : "";
        return $$"""
            OPTION OPTFINALLY
            DEF_DLL Hold({{arguments}},int,int,wstring,wstring,wstring):int:{{path}}{{pack}}
            DEF_DLL Gate(wstring,wstring,wstring,wstring):int:{{path}}
            THREAD Worker()
            """ + "\n" + (externalCall ? "" : RealParentScript(prefix)) + $$"""
            PROCEDURE Worker()
                DIM items[1,2]=7,8,9,40,50,60
                DIM text="{{initial}}",number=19
                TRY
                    PRINT "worker-result="+Hold({{values}},{{shape}},{{(wide ? 1 : 0)}},"{{prefix}}-child-entered","{{prefix}}-child-release","{{prefix}}-child-verified")
                FINALLY
                    PRINT "worker-finally="+items[0][0]+","+items[0][1]+","+items[0][2]+","+items[1][0]+","+items[1][1]+","+items[1][2]+"|"+text+"|"+number+"|"+VARTYPE(items[0][0])+","+VARTYPE(items[1][0])+","+VARTYPE(text)+","+VARTYPE(number)
                ENDTRY
            FEND
            """ + "\n";
    }

    private static string[] ExpectedBufferOutput(string type, int protocol,
        bool externalCall)
    {
        string text = type switch
        {
            "string" => "X",
            "wstring" => "あ",
            "pwchar" => "あ\0うえお",
            _ => "X\0ZQR"
        };
        int textType = type is "wstring" or "pwchar" ? 258 : 256;
        string snapshot = $"worker-finally=101,102,103,40,50,60|{text}|109|3,5,{textType},3";
        string[] output = protocol is 0 or 2
            ? ["worker-result=42", snapshot, "parent=11"]
            : protocol == 1 ? [snapshot] : ["parent=11", snapshot];
        return externalCall ? ["call-returned", .. output] : output;
    }
}
