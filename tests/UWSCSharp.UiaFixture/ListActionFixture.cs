using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace UWSCSharp.UiaFixture;

internal static partial class Program
{
    private static void RunListActions(bool pointerScroll = false, bool multiple = false)
    {
        var application = new Application();
        var list = new ListBox { Height = 100 };
        list.Items.Add(new ListBoxItem { Content = "alpha" });
        list.Items.Add(new ListBoxItem { Content = "shared" });
        list.Items.Add(new ListBoxItem { Content = "shared" });
        if (multiple)
        {
            list.SelectionMode = SelectionMode.Multiple;
            list.Items.Add(new ListBoxItem { Content = "beta" });
            list.Items.Add(new ListBoxItem { Content = "gamma" });
        }
        if (pointerScroll)
        {
            VirtualizingPanel.SetIsVirtualizing(list, false);
            for (int index = 0; index < 40; index++)
            {
                list.Items.Add(new ListBoxItem { Content = $"row{index}" });
            }

            list.Items.Add(new ListBoxItem { Content = "far" });
        }

        list.SelectedIndex = 0;
        var combo = new ComboBox();
        combo.Items.Add("first");
        combo.Items.Add("second");
        combo.Items.Add("shared");
        combo.SelectedIndex = 0;
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(combo);
        var window = new Window
        {
            Title = "UWSCSharp UIA list action fixture",
            Width = 360,
            Height = 220,
            ShowActivated = false,
            Content = panel
        };
        int systemMenuCommands = 0;
        HwndSourceHook menuHook = (nint handle, int message, nint parameter,
            nint data, ref bool handled) =>
        {
            if (message == 0x112 && ((long)parameter & 0xfff0) == 0xf100)
            {
                systemMenuCommands++;
            }
            return 0;
        };
        window.SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(menuHook);
        bool announced = false;
        window.ContentRendered += (_, _) =>
        {
            if (!announced)
            {
                announced = true;
                Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
                Console.Out.Flush();
            }
        };
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is string command)
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (command == "snapshot")
                    {
                        string visibility = "";
                        if (multiple)
                        {
                            visibility = ";selected=" + string.Join(",", list.SelectedItems
                                .Cast<ListBoxItem>().Select(item => item.Content));
                        }
                        if (pointerScroll)
                        {
                            var far = (ListBoxItem)list.Items[list.Items.Count - 1];
                            Point position = far.TranslatePoint(new Point(0, 0), list);
                            visibility = $";far-visible={position.Y >= 0 && position.Y + far.ActualHeight <= list.ActualHeight}";
                        }

                        Console.WriteLine($"list={list.SelectedIndex};combo={combo.SelectedIndex};expanded={combo.IsDropDownOpen}{visibility}");
                        Console.Out.Flush();
                    }
                    else if (command == "menu-stats")
                    {
                        Console.WriteLine($"system-menu={systemMenuCommands}");
                        Console.Out.Flush();
                    }
                    else if (command == "reset")
                    {
                        combo.IsDropDownOpen = false;
                        combo.SelectedIndex = 0;
                        if (multiple)
                        {
                            list.UnselectAll();
                        }
                        list.SelectedIndex = 0;
                        list.ScrollIntoView(list.Items[0]);
                        Keyboard.ClearFocus();
                        systemMenuCommands = 0;
                        Console.WriteLine($"reset:list={list.SelectedIndex};combo={combo.SelectedIndex};expanded={combo.IsDropDownOpen};selected={string.Join(",", list.SelectedItems.Cast<ListBoxItem>().Select(item => item.Content))}");
                        Console.Out.Flush();
                    }
                    else if (command == "exit")
                    {
                        window.Close();
                    }
                });
            }
        });
        application.Run(window);
    }
}
