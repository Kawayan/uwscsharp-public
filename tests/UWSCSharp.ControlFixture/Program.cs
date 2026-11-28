using System.Runtime.InteropServices;

namespace UWSCSharp.ControlFixture;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Controls
    {
        public uint Size;
        public uint Classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public uint Mask;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TreeItem
    {
        public uint Mask;
        public nint Handle;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public int SelectedImage;
        public int Children;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TreeInsert
    {
        public nint Parent;
        public nint After;
        public TreeItem Item;
    }

    private static nint AddTree(nint tree, nint parent, string name)
    {
        nint text = Marshal.StringToHGlobalUni(name);
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TreeInsert>());
        try
        {
            var insert = new TreeInsert
            {
                Parent = parent == 0 ? -65536 : parent,
                After = -65534,
                Item = new TreeItem { Mask = 1, Text = text }
            };
            Marshal.StructureToPtr(insert, pointer, false);
            nint result = SendMessageW(tree, 0x1132, 0, pointer);
            if (result == 0)
            {
                throw new InvalidOperationException("TREEVIEWを追加できません。");
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(text);
        }
    }

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref Controls controls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string name,
        string text, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message,
        nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint window, int index, int value);

    [StructLayout(LayoutKind.Sequential)]
    private struct ViewItem
    {
        public uint Mask;
        public int Index;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public nint Parameter;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public nint ColumnPointer;
        public nint ColumnFormats;
        public int Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Column
    {
        public uint Mask;
        public int Format;
        public int Width;
        public nint Text;
        public int Capacity;
        public int SubItem;
        public int Image;
        public int Order;
        public int Minimum;
        public int Default;
        public int Ideal;
    }

    private static nint Send<T>(nint view, uint message, nint index,
        T value) where T : struct
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            return SendMessageW(view, message, index, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void AddView(nint root, int top = 220, bool check = false)
    {
        nint view = CreateWindowExW(0, "SysListView32", "", 0x50000001,
            10, top, 200, 100, root, 44, 0, 0);
        if (view == 0)
        {
            throw new InvalidOperationException("LISTVIEWを作成できません。");
        }

        if (check)
        {
            SendMessageW(view, 0x1036, 4, 4);
        }

        for (int column = 0; column < 2; column++)
        {
            nint text = Marshal.StringToHGlobalUni(column == 0 ? " Key " : "Value");
            try
            {
                var item = new Column { Mask = 6, Width = 70, Text = text };
                if (Send(view, 0x1061, column, item) != column)
                {
                    throw new InvalidOperationException("列を追加できません。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        for (int row = 0; row < 2; row++)
        {
            for (int column = 0; column < 2; column++)
            {
                nint text = Marshal.StringToHGlobalUni(column == 0
                    ? (check ? $"remote-check{row}" : row == 0 ? " one " : "two") : $" 外部{row} ");
                try
                {
                    var item = new ViewItem { Mask = 1, Index = row, SubItem = column, Text = text };
                    nint result = Send(view, column == 0 ? 0x104du : 0x1074u,
                        column == 0 ? 0 : row, item);
                    if (result != (column == 0 ? row : 1))
                    {
                        throw new InvalidOperationException("行を追加できません。");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(text);
                }
            }

            var selected = new ViewItem { StateMask = 2, State = 2 };
            if (Send(view, 0x102b, row, selected) == 0)
            {
                throw new InvalidOperationException("専用行を選択できません。");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolbarButton
    {
        public int Image;
        public int Id;
        public byte State;
        public byte Style;
        public ushort Reserved;
        public nuint Data;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolbarInfo
    {
        public uint Size;
        public uint Mask;
        public int Id;
        public int Image;
        public byte State;
        public byte Style;
        public ushort Width;
        public nuint Data;
        public nint Text;
        public int Capacity;
    }

    private static void AddToolbar(nint root)
    {
        nint toolbar = CreateWindowExW(0, "ToolbarWindow32", "", 0x5000000e,
            10, 530, 200, 40, root, 47, 0, 0);
        if (toolbar == 0)
        {
            throw new InvalidOperationException("専用TOOLBARを作成できません。");
        }

        SendMessageW(toolbar, 0x41e, Marshal.SizeOf<ToolbarButton>(), 0);
        for (int index = 0; index < 3; index++)
        {
            nint text = Marshal.StringToHGlobalUni(index == 0
                ? " &remote toolbar... \0" : index == 1 ? "original\0" : "remote dropdown\0");
            try
            {
                nint name = SendMessageW(toolbar, 0x44d, 0, text);
                if (name < 0)
                {
                    throw new InvalidOperationException("TOOLBARの文字列を登録できません。");
                }

                var button = new ToolbarButton
                {
                    Image = -2,
                    Id = 301 + index,
                    State = 4,
                    Style = index == 2 ? (byte)128 : (byte)0,
                    Text = name
                };
                if (Send(toolbar, 0x444, 1, button) == 0)
                {
                    throw new InvalidOperationException("TOOLBARのbuttonを登録できません。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        nint changed = Marshal.StringToHGlobalUni(new string('長', 4097));
        try
        {
            var info = new ToolbarInfo
            {
                Size = (uint)Marshal.SizeOf<ToolbarInfo>(),
                Mask = 2,
                Text = changed
            };
            if (Send(toolbar, 0x440, 302, info) == 0)
            {
                throw new InvalidOperationException("TOOLBARの本文を変更できません。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(changed);
        }

        // 長文captionによって後続buttonが表示領域外へ押し出されないようにします。
        SendMessageW(toolbar, 0x43b, 0, 60 | 60 << 16);
        SendMessageW(toolbar, 0x41f, 0, 60 | 22 << 16);
    }

    private static void Main(string[] args)
    {
        if (args is ["--com-event-connection-probe", string externalProgId,
            "external-server"])
        {
            Environment.Exit(ComEventConnectionProbe.RunAsync(externalProgId,
                frameworkThread: true, active: true).GetAwaiter().GetResult());
            return;
        }

        if (args is ["--com-event-connection-probe", string frameworkProgId,
            "framework-thread"])
        {
            Environment.Exit(ComEventConnectionProbe.RunAsync(frameworkProgId, true)
                .GetAwaiter().GetResult());
            return;
        }

        if (args is ["--com-event-connection-probe", string eventProgId])
        {
            Environment.Exit(ComEventConnectionProbe.RunAsync(eventProgId)
                .GetAwaiter().GetResult());
            return;
        }

        if (args is ["--hotkey-reservation-probe", string hotkeyDirectory, string hotkeyValue])
        {
            Environment.Exit(HotkeyReservationProbe.Run(hotkeyDirectory,
                int.Parse(hotkeyValue, System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        if (args is ["--scheduler-worker", .. string[] identityOptions]
            && identityOptions.Length == 6
            && File.Exists(Path.Combine(identityOptions[4], "identity-probe")))
        {
            Environment.Exit(SchedulerIdentityProbe.RunAsync(identityOptions, false).GetAwaiter().GetResult());
            return;
        }
        if (args is ["--scheduler-identity-peer", .. string[] peerOptions])
        {
            Environment.Exit(SchedulerIdentityProbe.RunAsync(peerOptions, true).GetAwaiter().GetResult());
            return;
        }

        if (args is ["--scheduler-worker", _, _, _, _, string startupLogs, _]
            && File.Exists(Path.Combine(startupLogs, "startup-probe")))
        {
            File.WriteAllText(Path.Combine(startupLogs, "ready"), Environment.ProcessId.ToString());
            if (File.ReadAllText(Path.Combine(startupLogs, "startup-probe")) == "wait")
            {
                Thread.Sleep(TimeSpan.FromMinutes(2));
            }
            Environment.Exit(23);
            return;
        }

        if (args is ["--scheduler-job-probe", .. string[] schedulerOptions])
        {
            Environment.Exit(SchedulerJobProbe.RunAsync(schedulerOptions).GetAwaiter().GetResult());
            return;
        }

        if (args is ["--scheduler-monitor-lease-probe", string leasePath])
        {
            SchedulerStorageProbe.CheckMonitorLease(leasePath);
            return;
        }

        if (args is ["--scheduler-settings-probe", string settingsPath])
        {
            SchedulerStorageProbe.ResaveSettings(settingsPath);
            return;
        }

        if (args is ["--launcher-child-probe", string launcherReceipt,
            string launcherRelease, .. string[] launcherArguments])
        {
            Environment.Exit(LauncherChildProbe.Run(
                launcherReceipt, launcherRelease, launcherArguments));
            return;
        }

        if (args is ["--audio-runner-probe", string runnerPipe])
        {
            Environment.Exit(AudioRunnerProbe.RunAsync(runnerPipe).GetAwaiter().GetResult());
            return;
        }

        if (args is [UWSCSharp.Windows.AudioHostProcess.HostArgument, string pipe])
        {
            Environment.Exit(AudioHostProbe.RunAsync(pipe).GetAwaiter().GetResult());
            return;
        }

        if (args is ["--audio-host-child"])
        {
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        if (args is ["--input-lock-pointer-probe", string pointerReceipt, string pointerMode, string pointerDpi])
        {
            InputLockPointerProbe.RunAsync(pointerReceipt, pointerMode, int.Parse(pointerDpi, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-lock-touch-probe", string touchReceipt, string touchBlocked, string touchEnding, string touchCount])
        {
            InputLockTouchProbe.RunAsync(touchReceipt, bool.Parse(touchBlocked), touchEnding, int.Parse(touchCount, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-lock-direct-probe", string directReceipt, string directEnding, string directDpi])
        {
            InputLockDirectProbe.RunAsync(directReceipt, directEnding, int.Parse(directDpi, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-lock-run-probe", string runReceipt, string ending])
        {
            InputLockRunProbe.RunAsync(runReceipt, ending).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-ownership-probe", string inputReceipt])
        {
            InputOwnershipProbe.RunAsync(inputReceipt).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-ownership-probe", string heldInputReceipt, "hold"])
        {
            InputOwnershipProbe.RunAsync(heldInputReceipt, hold: true).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--input-ownership-probe", string threadInputReceipt,
            "owner-thread-exit"])
        {
            InputOwnershipProbe.RunAsync(threadInputReceipt,
                exitOwnerThread: true).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--watchdog-owner-probe", string receipt, string killOnClose])
        {
            WatchdogOwnerProbe.RunAsync(receipt, bool.Parse(killOnClose)).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--ini-settings-save", string mode, string ini,
            string ready, string release])
        {
            IniSettingsSaveProbe.RunAsync(mode, ini, ready, release)
                .GetAwaiter().GetResult();
            return;
        }

        if (args is ["--process-output-probe", .. string[] outputOptions])
        {
            Environment.Exit(ProcessOutputProbe.RunAsync(outputOptions).GetAwaiter().GetResult());
            return;
        }

        if (args is ["--cli-native-dll-console-probe", .. string[] nativeOptions])
        {
            Environment.Exit(CliThreadConsoleProbe.RunNativeLibrary(nativeOptions));
            return;
        }

        if (args is ["--cli-thread-console-probe", .. string[] threadOptions])
        {
            Environment.Exit(CliThreadConsoleProbe.Run(threadOptions));
            return;
        }

        if (args is ["--cli-input-cleanup-probe", .. string[] cleanupOptions])
        {
            Environment.Exit(CliInputCleanupProbe.Run(cleanupOptions));
            return;
        }

        if (args is ["--cli-console-probe", .. string[] cliOptions])
        {
            Environment.Exit(CliConsoleProbe.Run(cliOptions));
            return;
        }

        if (args is ["--gdi-capture-probe", .. string[] gdiOptions])
        {
            Environment.Exit(GdiCaptureProbe.Run(gdiOptions is ["leak"]));
            return;
        }

        if (args is ["--exec-probe", .. string[] execOptions])
        {
            ExecProbe.Run(execOptions);
            return;
        }

        if (args is ["--saveimg", .. string[] saveOptions])
        {
            SaveImageClipboardProbe.RunAsync(saveOptions).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--peekcolor", .. string[] options])
        {
            ClipboardProbe.RunAsync(options).GetAwaiter().GetResult();
            return;
        }

        if (args is ["--chkimg", .. string[] imageOptions])
        {
            ClipboardProbe.RunAsync(imageOptions, checkImage: true)
                .GetAwaiter().GetResult();
            return;
        }

        if (args is ["--item-data-probe"])
        {
            ItemDataProbe.Run();
            return;
        }

        // 外部control検証専用processです。実アプリへ入力しません。
        var controls = new Controls { Size = 8, Classes = 15 };
        if (!InitCommonControlsEx(ref controls))
        {
            throw new InvalidOperationException("TABの初期化に失敗しました。");
        }

        nint root = CreateWindowExW(0, "STATIC", "M3 child process fixture",
            0x10000000, 10, 10, 300, 600, 0, 0, 0, 0);
        nint tab = CreateWindowExW(0, "SysTabControl32", "", 0x50000000,
            10, 10, 200, 100, root, 42, 0, 0);
        if (root == 0 || tab == 0)
        {
            throw new InvalidOperationException("専用windowを作成できません。");
        }

        string[] names = [" &日本語... ", "", "same", "same", new('長', 300)];
        for (int index = 0; index < names.Length; index++)
        {
            nint text = Marshal.StringToHGlobalUni(names[index]);
            nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Item>());
            try
            {
                var item = new Item { Mask = 1, Text = text };
                Marshal.StructureToPtr(item, pointer, false);
                if (SendMessageW(tab, 0x133e, index, pointer) != index)
                {
                    throw new InvalidOperationException("TABを追加できません。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
                Marshal.FreeHGlobal(text);
            }
        }

        nint tree = CreateWindowExW(0, "SysTreeView32", "", 0x50000000,
            10, 120, 200, 70, root, 43, 0, 0);
        if (tree == 0)
        {
            throw new InvalidOperationException("専用TREEVIEWを作成できません。");
        }

        nint leaf = AddTree(tree, AddTree(tree, 0, " 親 "), " &子... ");
        if (SendMessageW(tree, 0x110b, 9, leaf) == 0)
        {
            throw new InvalidOperationException("専用TREE項目を選択できません。");
        }
        AddView(root);
        nint checkTree = CreateWindowExW(0, "SysTreeView32", "", 0x50000000,
            10, 330, 200, 60, root, 45, 0, 0);
        if (checkTree == 0)
        {
            throw new InvalidOperationException("専用check TREEを作成できません。");
        }

        SetWindowLongW(checkTree, -16, 0x50000100);
        AddTree(checkTree, 0, "tree-check");
        AddView(root, 400, check: true);
        nint status = CreateWindowExW(0, "msctls_statusbar32", "", 0x5000000c,
            10, 490, 200, 25, root, 46, 0, 0);
        if (status == 0)
        {
            throw new InvalidOperationException("専用STATUSBARを作成できません。");
        }

        int[] widths = [60, 120, -1];
        nint statusParts = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.Copy(widths, 0, statusParts, 3);
            if (SendMessageW(status, 0x404, 3, statusParts) == 0)
            {
                throw new InvalidOperationException("STATUSBARのpartを設定できません。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(statusParts);
        }

        string[] statusTexts = [" remote ", "", new string('長', 65535)];
        for (int index = 0; index < statusTexts.Length; index++)
        {
            nint text = Marshal.StringToHGlobalUni(statusTexts[index]);
            try
            {
                if (SendMessageW(status, 0x40b, index, text) == 0)
                {
                    throw new InvalidOperationException("STATUSBARの本文を設定できません。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }
        AddToolbar(root);
        Console.WriteLine($"{(long)root},{(long)tab}");
        Console.Out.Flush();
        while (GetMessageW(out Message message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }
}
