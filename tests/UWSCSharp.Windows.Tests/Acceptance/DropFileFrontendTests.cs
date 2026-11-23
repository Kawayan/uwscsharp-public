using System.Text;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class DropFileFrontendTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string frontend in new[] { "UWSCSharp.Cli", "UWSCSharp.Wpf" })
        {
            foreach (string architecture in new[] { "x86", "x64" })
            {
                foreach (bool worker in new[] { false, true })
                {
                    yield return [frontend, architecture, worker];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ScriptSendsFilesAndCatchesInvalidTarget(
        string frontend, string architecture, bool worker)
    {
        List<DropFileReceiver> receivers = [];
        Exception? primary = null;
        CapturedProcess? process = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            DropFileReceiver receiver = await DropFileReceiver.StartAsync(architecture);
            receivers.Add(receiver);
            string directory = receiver.DirectoryPath;
            string file = Path.Combine(directory, "日本語😀.txt");
            await File.WriteAllTextAsync(file, "probe", timeout.Token);
            string receipt = Path.Combine(directory, "caught.txt");
            string script = Path.Combine(directory, "main.uws");
            string sending = worker
                ? "THREAD SendFiles(target)\nWHILE finished=FALSE\nSLEEP(0.01)\nWEND"
                : $"DROPFILE(target,\"{directory}\",\"日本語😀.txt\")";
            await File.WriteAllTextAsync(script, $$"""
                PUBLIC finished=FALSE
                DIM target=HNDTOID({{(long)receiver.Window}})
                DIM caught=FALSE
                TRY
                    DROPFILE(0,"{{directory}}","日本語😀.txt")
                EXCEPT
                    caught=TRUE
                ENDTRY
                DIM f=FOPEN("{{receipt}}",F_WRITE8)
                FPUT(f,caught,0)
                FCLOSE(f)
                {{sending}}
                PROCEDURE SendFiles(id)
                    DROPFILE(id,"{{directory}}","日本語😀.txt")
                    finished=TRUE
                FEND
                """, new UTF8Encoding(true), timeout.Token);
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            process = await CapturedProcess.StartAsync(executable,
                $"\"{executable}\" \"{script}\"", timeout.Token);
            Task<byte[]> output = process.ReadOutputAsync(cancellationToken: timeout.Token);
            uint exit = await process.WaitForExitAsync(timeout.Token);
            string text = Encoding.UTF8.GetString(await output);
            Assert.True(exit == 0, $"exit={exit}\n{text}");
            Assert.Equal("1\r\n", await File.ReadAllTextAsync(receipt, timeout.Token));
            JsonElement drop = await receiver.ReadDropAsync();
            Assert.Equal((long)receiver.Window, drop.GetProperty("recipient").GetInt64());
            string path = string.Concat(drop.GetProperty("paths")[0]
                .EnumerateArray().Select(unit => (char)unit.GetInt32()));
            Assert.Equal(file, path);
            Assert.Single(drop.GetProperty("paths").EnumerateArray());
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (process is not null)
            {
                Exception cleanup = primary ?? new InvalidOperationException("実行ホストの停止と解放");
                await process.StopAfterFailureAsync(cleanup);
                process.Dispose();
                if (primary is null && cleanup.Data.Count != 0)
                {
                    cleanupFailure = cleanup;
                }
            }

            await DropFileReceiver.ReleaseAllAsync(receivers, primary ?? cleanupFailure);
            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
    }
}
