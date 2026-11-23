using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

internal sealed class DropFileReceiver
{
    private readonly CapturedProcess process;

    private DropFileReceiver(string directory, CapturedProcess process, JsonElement metadata)
    {
        DirectoryPath = directory;
        this.process = process;
        Metadata = metadata;
    }

    internal string DirectoryPath
    {
        get;
    }

    internal JsonElement Metadata
    {
        get;
    }

    internal nint Window => (nint)Metadata.GetProperty("hwnd").GetInt64();

    internal nint Child => (nint)Metadata.GetProperty("child").GetInt64();

    internal static async Task<DropFileReceiver> StartAsync(string architecture, string mode = "")
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-drop-receiver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CapturedProcess? process = null;
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory,
                "drop-file-probe", architecture, "DropFileProbe.exe");
            Assert.True(File.Exists(executable), executable);
            process = CapturedProcess.StartUncaptured(executable,
                $"\"{executable}\" \"{directory}\" \"UWSC drop {Guid.NewGuid():N}\" {mode}",
                visible: false, CancellationToken.None);
            string ready = Path.Combine(directory, "ready.json");
            JsonElement metadata = await ReadPublishedJsonAsync(ready);
            Assert.Equal(process.Id, metadata.GetProperty("pid").GetUInt32());
            Assert.Equal(architecture == "x86" ? 32 : 64,
                metadata.GetProperty("bits").GetInt32());
            return new(directory, process, metadata);
        }
        catch (Exception error)
        {
            if (process is not null)
            {
                await process.StopAfterFailureAsync(error);
                process.Dispose();
            }

            try
            {
                DeleteDirectory(directory);
            }
            catch (Exception cleanup)
            {
                error.Data["受信画面の作成失敗後の解放"] = cleanup;
            }

            throw;
        }
    }

    internal Task<JsonElement> ReadDropAsync() =>
        ReadPublishedJsonAsync(Path.Combine(DirectoryPath, "drop-0.json"));

    internal async Task CloseAsync()
    {
        Assert.True(Native.PostMessageW(Window, 0x10, 0, 0));
        Assert.Equal(0u, await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)));
    }

    internal async Task TerminateBeforeReadingDropAsync()
    {
        await WaitForFileAsync(Path.Combine(DirectoryPath, "drop-entered.json"));
        await TerminateAsync();
    }

    internal async Task TerminateBeforeDispatchAsync()
    {
        await WaitForFileAsync(Path.Combine(DirectoryPath, "drop-queued.json"));
        await TerminateAsync();
    }

    private async Task TerminateAsync()
    {
        process.TerminateTree(71);
        Assert.Equal(71u, await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(8)));
    }

    internal static async Task ReleaseAllAsync(
        IReadOnlyList<DropFileReceiver> receivers, Exception? primary)
    {
        List<Exception> failures = [];
        foreach (DropFileReceiver receiver in receivers.Reverse())
        {
            var cleanup = new InvalidOperationException("受信画面の停止と解放");
            await receiver.process.StopAfterFailureAsync(cleanup);
            receiver.process.Dispose();
            foreach (object? value in cleanup.Data.Values)
            {
                if (value is Exception error)
                {
                    failures.Add(error);
                }
            }

            try
            {
                DeleteDirectory(receiver.DirectoryPath);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (failures.Count != 0)
        {
            var failure = new AggregateException("受信画面の解放に失敗しました。", failures);
            if (primary is null)
            {
                throw failure;
            }

            primary.Data["受信画面の解放"] = failure;
        }
    }

    private static void DeleteDirectory(string directory)
    {
        string root = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<JsonElement> ReadPublishedJsonAsync(string path)
    {
        await WaitForFileAsync(path);
        // 受信側は書込みを終えてから名前を変更します。
        // 名前変更に使うハンドルと共有できる状態で読み取ります。
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
            FileOptions.Asynchronous);
        using JsonDocument data = await JsonDocument.ParseAsync(stream);
        return data.RootElement.Clone();
    }

    private static async Task WaitForFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), path);
            await Task.Delay(25);
        }
    }
}
