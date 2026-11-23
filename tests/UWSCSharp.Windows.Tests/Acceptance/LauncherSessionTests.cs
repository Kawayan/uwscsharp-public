using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class LauncherSessionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("executable")]
    [InlineData("script")]
    [InlineData("document")]
    public async Task FreshEditorRestoresGroupAndKeepsChildrenIndependentAcrossMissingTarget(string kind)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        var documents = new List<LauncherDocumentProbe>();
        bool scriptTarget = kind == "script";
        bool documentTarget = kind == "document";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string directory = Path.Combine(fixture.DirectoryPath, "sessions");
            string targets = Path.Combine(directory, "targets");
            Directory.CreateDirectory(targets);
            if (scriptTarget)
            {
                await using Stream source = typeof(LauncherSessionTests).Assembly
                    .GetManifestResourceStream("UWSCSharp.Windows.Tests.LauncherChildProbe")!;
                await using FileStream target = File.Create(Path.Combine(targets, "子 script.uws"));
                await source.CopyToAsync(target, timeout.Token);
            }
            else
            {
                string original = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
                foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(original)!))
                {
                    string copied = Path.Combine(targets, Path.GetFileName(file));
                    File.Copy(file, copied);
                    File.SetAttributes(copied, FileAttributes.Normal);
                }
            }

            if (documentTarget)
            {
                var paths = new Dictionary<string, string>();
                foreach (string name in new[] { "first", "second" })
                {
                    var probe = LauncherDocumentProbe.Create(directory, name,
                        Path.Combine(targets, "UWSCSharp.ControlFixture.exe"));
                    documents.Add(probe);
                    paths.Add(name, probe.DocumentPath);
                }

                await File.WriteAllTextAsync(Path.Combine(directory, "document-targets.json"),
                    JsonSerializer.Serialize(paths), timeout.Token);
            }

            string saved = Path.Combine(directory, "saved.json");
            Process firstOwner = fixture.StartEditor("--verify-launcher-session", directory, saved, "save:" + kind);
            await firstOwner.WaitForExitAsync(timeout.Token);
            using JsonDocument savedResult = await ReadResultAsync(saved, firstOwner, "Saved");
            string receipt = Path.Combine(directory, "restored.json");
            Process owner = fixture.StartEditor("--verify-launcher-session", directory, receipt, "run:" + kind);
            await owner.WaitForExitAsync(timeout.Token);
            using JsonDocument result = await ReadResultAsync(receipt, owner, "Passed");
            string[] checks = [.. result.RootElement.GetProperty("checks").EnumerateArray()
                .Select(item => item.GetString()!)];
            Assert.Equal(4, checks.Length);
            Assert.Equal(4, checks.Distinct().Count());
            Assert.Equal(kind, result.RootElement.GetProperty("target_kind").GetString());
            Process child = fixture.Own(result.RootElement.GetProperty("second_pid").GetInt32());
            Assert.False(child.HasExited);
            await File.WriteAllTextAsync(Path.Combine(directory, "child-second.go"), "go", timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, child.ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, "child-second" + (scriptTarget ? ".done" : ".json.done"))));
            output.WriteLine($"target-kind={kind}");
            output.WriteLine(string.Join(Environment.NewLine, checks));
            output.WriteLine($"separate-owner-processes: first={firstOwner.Id},restarted={owner.Id}");
            output.WriteLine("owner-process-exit-keeps-second-child-running-until-explicit-release");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            foreach (LauncherDocumentProbe document in documents)
            {
                try
                {
                    document.Dispose();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                }
            }

            Exception? cleanup = cleanupErrors.Count > 0 ? new AggregateException(cleanupErrors) : null;
            if (primary is not null && cleanup is not null)
            {
                primary.Data["文書関連付けの回収エラー"] = cleanup;
            }

            await fixture.CleanupAsync(primary ?? cleanup);
            if (primary is null && cleanup is not null)
            {
                throw cleanup;
            }
        }

        static async Task<JsonDocument> ReadResultAsync(string path, Process process, string expected)
        {
            string error = path + ".error";
            Assert.False(File.Exists(error), File.Exists(error) ? await File.ReadAllTextAsync(error) : "");
            Assert.Equal(0, process.ExitCode);
            var result = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            try
            {
                Assert.Equal(expected, result.RootElement.GetProperty("result").GetString());
                Assert.Equal(process.Id, result.RootElement.GetProperty("owner_pid").GetInt32());
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}
