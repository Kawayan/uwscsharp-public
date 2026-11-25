using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class TrayShellLayoutVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt,
        bool restartExplorer = false)
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("通知領域の配置調査はSandbox内だけで実行します。");
        }

        Directory.CreateDirectory(directory);
        var main = new MainWindow(false, confirmDiscard: () => true,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"));
        var access = new MainWindow.TrayVerificationAccess(main);
        try
        {
            main.ShowAtStartup(startInTray: true);
            VerificationAssert.Require(!main.IsVisible && access.Icon?.Visible == true,
                "起動時のトレイ格納");
            await Task.Delay(500);
            Point point = access.Icon!.GetShellIconPhysicalCenter();
            int? restartedShell = null;
            if (restartExplorer)
            {
                (restartedShell, point) = await RestartShellAsync(main, access);
            }

            object? layout = restartExplorer ? null
                : await Task.Run(() => ReadTrayShellLayout(point));
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Observed",
                mouse_input = false,
                restartedShell,
                point = new
                {
                    point.X,
                    point.Y
                },
                layout
            }));
            return 0;
        }
        finally
        {
            main.Close();
        }
    }

    private static async Task<(int ProcessId, Point Point)> RestartShellAsync(MainWindow main,
        MainWindow.TrayVerificationAccess access)
    {
        nint shell = GetShellWindow();
        Native.GetWindowThreadProcessId(shell, out uint owner);
        VerificationAssert.Require(shell != 0 && owner != 0, "ゲストのシェル");
        using var explorer = Process.GetProcessById(checked((int)owner));
        using var current = Process.GetCurrentProcess();
        VerificationAssert.Require(explorer.SessionId == current.SessionId
            && explorer.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase),
            "再起動するゲストのExplorer");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        nint handle = access.Handle;
        explorer.Kill();
        await explorer.WaitForExitAsync(deadline.Token);

        while (true)
        {
            nint newShell = GetShellWindow();
            Native.GetWindowThreadProcessId(newShell, out uint newOwner);
            if (newShell != 0 && newOwner != 0 && newOwner != owner)
            {
                Point point;
                try
                {
                    point = access.Icon!.GetShellIconPhysicalCenter();
                }
                catch (Exception error) when (error is ExternalException
                    or InvalidOperationException)
                {
                    // シェルの起動後も通知領域の初期化を待ちます。
                    await Task.Delay(100, deadline.Token);
                    continue;
                }

                VerificationAssert.Require(!main.IsVisible && access.Handle == handle
                    && access.Icon.Visible, "再登録後も格納状態を維持");
                access.Restore();
                VerificationAssert.Require(main.IsVisible && access.Handle == handle
                    && !access.Icon.Visible, "再登録後の画面復帰");
                return (checked((int)newOwner), point);
            }

            await Task.Delay(100, deadline.Token);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    private static object ReadTrayShellLayout(Point point)
    {
        var owned = new List<object>();
        try
        {
            var automation = (IUiaAutomation)new UiaComObject();
            owned.Add(automation);
            IUiaElement root = automation.GetRootElement();
            owned.Add(root);
            IUiaElement hit = automation.ElementFromPoint(new Native.Point
            {
                X = checked((int)point.X),
                Y = checked((int)point.Y)
            });
            owned.Add(hit);
            object atPoint = Read(hit);
            using Process explorer = Process.GetProcessesByName("explorer").Single();
            IUiaCondition condition = automation.CreatePropertyCondition(30002, explorer.Id);
            owned.Add(condition);
            IUiaElements elements = root.FindAll(4, condition);
            owned.Add(elements);
            var items = new List<object>();
            int count = elements.Length();
            for (int index = 0; index < Math.Min(count, 128); index++)
            {
                IUiaElement element = elements.GetElement(index);
                owned.Add(element);
                items.Add(Read(element));
            }

            return new
            {
                at_point = atPoint,
                total = count,
                items
            };
        }
        finally
        {
            foreach (object value in owned.AsEnumerable().Reverse())
            {
                Marshal.ReleaseComObject(value);
            }
        }

        static object Read(IUiaElement element) => new
        {
            name = element.Property(30005),
            id = element.Property(30011),
            type = element.Property(30003),
            class_name = element.Property(30012),
            rectangle = element.Property(30001),
            offscreen = element.Property(30022)
        };
    }
}
