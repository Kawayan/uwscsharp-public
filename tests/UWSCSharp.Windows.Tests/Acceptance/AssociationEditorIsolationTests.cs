using System.Diagnostics;
using System.Text;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AssociationEditorIsolationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssociationRunPreservesExistingEditor(bool unsavedDraft)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            Process editor = fixture.StartEditor();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            nint window = await WaitForWindowAsync(editor, timeout.Token);
            await SnapshotAsync(window, expand: true, token: timeout.Token);
            string? draft = unsavedDraft ? "// 未保存の原稿 " + Guid.NewGuid() : null;
            EditorSnapshot before = await SnapshotAsync(window, draft, token: timeout.Token);
            if (draft is not null)
            {
                Assert.Equal(draft, before.Source);
            }

            Assert.False(before.ReadOnly);
            string directory = Path.Combine(fixture.DirectoryPath, "別の日本語 scripts");
            Directory.CreateDirectory(directory);
            string script = Path.Combine(directory, "関連付けからの実行.uws");
            await File.WriteAllTextAsync(script, """
                DIM f=FOPEN("association.ready",F_WRITE8)
                FCLOSE(f)
                WHILE !FOPEN("association.go",F_EXISTS)
                    SLEEP(0.05)
                WEND
                f=FOPEN("association.result",F_WRITE8)
                FPUT(f,PARAM_STR[0],0)
                FCLOSE(f)
                EXITEXIT(0)
                """, new UTF8Encoding(true));
            Process associated = fixture.Start(script, "別プロセスの引数");
            Assert.NotEqual(editor.Id, associated.Id);
            while (!File.Exists(Path.Combine(directory, "association.ready")))
            {
                Assert.False(associated.HasExited, "待機開始前に終了しました。");
                await Task.Delay(50, timeout.Token);
            }

            Assert.False(editor.HasExited);
            Assert.Equal(window, await WaitForWindowAsync(editor, timeout.Token));
            Assert.Equal(before, await SnapshotAsync(window, token: timeout.Token));
            Assert.False(File.Exists(Path.Combine(directory, "association.result")));
            output.WriteLine("association-running: editor-source/output/file/status unchanged");

            await File.WriteAllTextAsync(Path.Combine(directory, "association.go"), "go");
            await associated.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, associated.ExitCode);
            Assert.Equal("別プロセスの引数", (await File.ReadAllTextAsync(
                Path.Combine(directory, "association.result"))).Trim());
            Assert.False(editor.HasExited);
            Assert.Equal(window, await WaitForWindowAsync(editor, timeout.Token));
            Assert.Equal(before, await SnapshotAsync(window, token: timeout.Token));
            output.WriteLine("association-ended: same editor window and values preserved");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // 未保存原稿の終了確認は対象に含めず、所有する検証プロセスだけを回収する。
            await fixture.CleanupAsync(primary);
        }
    }

    private static async Task<nint> WaitForWindowAsync(Process process,
        CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            process.Refresh();
            Assert.False(process.HasExited);
            nint window = process.MainWindowHandle;
            if (window != 0)
            {
                Native.GetWindowThreadProcessId(window, out uint processId);
                Assert.Equal((uint)process.Id, processId);
                Assert.True(Native.IsWindowVisible(window));
                Assert.True(Native.IsWindowEnabled(window));
                Assert.False(Native.IsIconic(window));
                return window;
            }

            await Task.Delay(50, token);
        }
    }

    internal static Task<EditorSnapshot> SnapshotAsync(nint window,
        string? draft = null, bool expand = false, CancellationToken token = default,
        string? invokeButton = null) =>
        Task.Run(() =>
        {
            var owned = new List<object>();
            Exception? primary = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var automation = (IUiaAutomation)new UiaComObject();
                owned.Add(automation);
                IUiaElement root = automation.ElementFromHandle(window);
                owned.Add(root);

                IUiaElement Find(string id)
                {
                    IUiaCondition condition = automation.CreatePropertyCondition(30011, id);
                    owned.Add(condition);
                    IUiaElements matches = root.FindAll(4, condition);
                    owned.Add(matches);
                    Assert.Equal(1, matches.Length());
                    IUiaElement element = matches.GetElement(0);
                    owned.Add(element);
                    return element;
                }

                if (expand)
                {
                    IUiaElement panel = Find("EditorPanel");
                    Guid iid = typeof(IUiaExpandCollapsePattern).GUID;
                    object pattern = panel.PatternAs(10005, ref iid);
                    owned.Add(pattern);
                    var expandable = (IUiaExpandCollapsePattern)pattern;
                    expandable.Expand();
                    Assert.Equal(1, expandable.State());
                }

                if (invokeButton is not null)
                {
                    Guid iid = typeof(IUiaInvoke).GUID;
                    object pattern = Find(invokeButton).PatternAs(10000, ref iid);
                    owned.Add(pattern);
                    ((IUiaInvoke)pattern).Invoke();
                }

                IUiaElement editor = Find("Editor");
                Guid valueId = typeof(IUiaValue).GUID;
                object valuePattern = editor.PatternAs(10002, ref valueId);
                owned.Add(valuePattern);
                var value = (IUiaValue)valuePattern;
                if (draft is not null)
                {
                    value.SetValue(draft);
                }

                token.ThrowIfCancellationRequested();
                return new EditorSnapshot(value.Value(),
                    Assert.IsType<string>(Find("Output").Property(30045)),
                    Assert.IsType<string>(Find("FileLabel").Property(30005)),
                    Assert.IsType<string>(Find("StatusLabel").Property(30005)),
                    value.IsReadOnly());
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(primary);
                for (int index = owned.Count - 1; index >= 0; index--)
                {
                    cleanup.ReleaseComOnly(owned[index]);
                }

                cleanup.Finish();
            }
        }, token);

    internal sealed record EditorSnapshot(string Source, string Output,
        string File, string Status, bool ReadOnly);
}
