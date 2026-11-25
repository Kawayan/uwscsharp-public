using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class TrayPointerInvestigation
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("トレイの追加調査はSandbox内だけで実行します。");
        }

        Directory.CreateDirectory(directory);
        VerificationAssert.Require(TrayShellInput.GetPhysicalCursorPos(out TrayShellInput.PhysicalPoint saved),
            "調査前のポインター位置を取得する");
        var main = new MainWindow(false, confirmDiscard: () => true,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"));
        var trayAccess = new MainWindow.TrayVerificationAccess(main);
        int clicks = 0;
        bool restored = false;
        string outcome = "NotClicked";
        try
        {
            main.Show();
            trayAccess.Hide();
            await Task.Delay(500);
            TrayIcon tray = trayAccess.Icon!;
            TrayIcon.PointerSample before = await Task.Run(tray.SamplePointer);
            Record("before-move", before);
            Point destination = new(before.IconX, before.IconY);
            ScriptDropOleVerification.Move(destination);
            await Task.Delay(50);
            TrayIcon.PointerSample moved = await Task.Run(tray.SamplePointer);
            Record("after-move", moved);
            if (moved.PointerX == before.IconX && moved.PointerY == before.IconY
                && before.AtIcon.IsChevron && moved.AtPointer.IsChevron
                && moved.AtPointer.Contains(moved.PointerX, moved.PointerY))
            {
                clicks++;
                ScriptDropOleVerification.Button(down: true);
                try
                {
                    await Task.Delay(20);
                }
                finally
                {
                    ScriptDropOleVerification.Button(down: false);
                }

                outcome = "ChevronClickedOnce";
                Record("after-click", await Task.Run(tray.SamplePointer));
                await Task.Delay(300);
                Record("after-300ms", await Task.Run(tray.SamplePointer));
                await Task.Delay(1000);
                Record("after-1300ms", await Task.Run(tray.SamplePointer));
            }
            else
            {
                outcome = "NotClickedTargetMismatch";
            }
        }
        finally
        {
            try
            {
                ScriptDropOleVerification.Button(down: false);
                ScriptDropOleVerification.Move(new Point(saved.X, saved.Y));
                VerificationAssert.Require(TrayShellInput.GetPhysicalCursorPos(out TrayShellInput.PhysicalPoint actual),
                    "復元後のポインター位置を取得する");
                restored = actual.X == saved.X && actual.Y == saved.Y;
                File.AppendAllText(receipt + ".events", JsonSerializer.Serialize(new
                {
                    phase = "cleanup",
                    clicks,
                    button_released = true,
                    pointer_restored = restored,
                    saved_x = saved.X,
                    saved_y = saved.Y,
                    actual.X,
                    actual.Y
                }) + Environment.NewLine);
            }
            finally
            {
                main.Close();
            }
        }

        VerificationAssert.Require(restored, "調査前のポインター位置に戻す");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = "Observed",
            outcome,
            click_count = clicks,
            button_released = true,
            pointer_restored = restored,
            launcher_validation = false
        }));
        return 0;

        void Record(string phase, TrayIcon.PointerSample sample)
            => File.AppendAllText(receipt + ".events", JsonSerializer.Serialize(new
            {
                phase,
                sample
            }) + Environment.NewLine);
    }
}

internal sealed partial class TrayIcon
{
    internal PointerSample SamplePointer()
    {
        VerificationAssert.Require(TrayShellInput.GetPhysicalCursorPos(out TrayShellInput.PhysicalPoint cursor),
            "観測時の物理座標を取得する");
        return ReadShellSample(GetShellIconPhysicalCenter(),
            new Point(cursor.X, cursor.Y));
    }

    private PointerSample ReadShellSample(Point icon, Point cursor)
    {
        var owned = new List<object>();
        try
        {
            var automation = (IUiaAutomation)new UiaComObject();
            owned.Add(automation);
            PointerHit atIcon = HitAt(checked((int)icon.X), checked((int)icon.Y));
            PointerHit atPointer = cursor == icon
                ? atIcon
                : HitAt(checked((int)cursor.X), checked((int)cursor.Y));
            IUiaElement root = automation.GetRootElement();
            owned.Add(root);
            IUiaCondition condition = automation.CreatePropertyCondition(30005, notification.Text);
            owned.Add(condition);
            IUiaElements elements = root.FindAll(4, condition);
            owned.Add(elements);
            var matches = new List<PointerHit>();
            for (int index = 0; index < Math.Min(elements.Length(), 16); index++)
            {
                IUiaElement element = elements.GetElement(index);
                owned.Add(element);
                matches.Add(Read(element));
            }

            return new PointerSample(checked((int)icon.X), checked((int)icon.Y),
                checked((int)cursor.X), checked((int)cursor.Y), atIcon,
                atPointer, notification.Text,
                elements.Length(), matches);

            PointerHit HitAt(int x, int y)
            {
                IUiaElement element = automation.ElementFromPoint(new Native.Point { X = x, Y = y });
                owned.Add(element);
                return Read(element);
            }
        }
        finally
        {
            foreach (object value in owned.AsEnumerable().Reverse())
            {
                Marshal.ReleaseComObject(value);
            }
        }

        static PointerHit Read(IUiaElement element)
        {
            object? raw = element.Property(30001);
            double[] bounds = raw is Array array && array.Length == 4
                ? [.. Enumerable.Range(0, 4).Select(index => Convert.ToDouble(array.GetValue(index)))]
                : [];
            return new PointerHit((string?)element.Property(30005) ?? "",
                (string?)element.Property(30011) ?? "",
                (string?)element.Property(30012) ?? "",
                (bool)element.Property(30022)!, bounds);
        }
    }

    internal sealed record PointerSample(int IconX, int IconY, int PointerX, int PointerY,
        PointerHit AtIcon, PointerHit AtPointer, string IconName, int NamedCount, List<PointerHit> NamedMatches);

    internal sealed record PointerHit(string Name, string Id, string ClassName, bool Offscreen, double[] Bounds)
    {
        internal bool IsChevron => !Offscreen && Id == "SystemTrayIcon"
            && ClassName == "SystemTray.NormalButton"
            && Name == "非表示のアイコンを表示する 隠れているインジケーターを表示します";

        internal bool Contains(int x, int y) => Bounds.Length == 4
            && Bounds.All(double.IsFinite)
            && x >= Bounds[0] && x < Bounds[0] + Bounds[2]
            && y >= Bounds[1] && y < Bounds[1] + Bounds[3];
    }
}
