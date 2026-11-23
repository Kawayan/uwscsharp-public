using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;

namespace UWSCSharp.UiaFixture;

internal static partial class Program
{
    private sealed class SelectionList(bool nested, bool combo = false,
        bool initiallyExpanded = false, bool supportsSelection = false) : ListBox
    {
        internal readonly List<double> ScrollCalls = [];
        internal readonly List<string> ExpandCalls = [];
        private bool expanded = initiallyExpanded;
        internal bool IsExpanded => expanded;
        private bool IsCombo => combo;
        private bool SupportsSelection => supportsSelection;
        private double position = 42;

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, nested);

        private sealed class Peer(SelectionList owner, bool nested) : FrameworkElementAutomationPeer(owner)
        {
            private readonly ListBoxAutomationPeer original = new(owner);

            protected override AutomationControlType GetAutomationControlTypeCore() =>
                owner.IsCombo ? AutomationControlType.ComboBox : AutomationControlType.List;

            protected override string GetClassNameCore() => "SelectionList";

            internal void RefreshChildren() => ResetChildrenCache();

            public override object GetPattern(PatternInterface patternInterface)
            {
                if (owner.SupportsSelection && patternInterface == PatternInterface.Selection)
                {
                    return original.GetPattern(patternInterface);
                }

                if (owner.IsCombo && patternInterface == PatternInterface.ExpandCollapse)
                {
                    return new Expanding(owner);
                }

                return patternInterface == PatternInterface.Scroll ? new Scrolling(owner) : base.GetPattern(patternInterface);
            }

            protected override List<AutomationPeer> GetChildrenCore()
            {
                if (owner.IsCombo && !owner.expanded && !owner.SupportsSelection)
                {
                    return [];
                }

                List<AutomationPeer> items = original.GetChildren() ?? [];
                return nested ? [new GroupPeer(new FrameworkElement(), items)] : items;
            }
        }

        private sealed class Expanding(SelectionList owner) : IExpandCollapseProvider
        {
            public ExpandCollapseState ExpandCollapseState => owner.expanded
                ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

            public void Expand()
            {
                owner.ExpandCalls.Add("Expand");
                owner.expanded = true;
                (UIElementAutomationPeer.FromElement(owner) as Peer)?.RefreshChildren();
            }

            public void Collapse()
            {
                owner.ExpandCalls.Add("Collapse");
                owner.expanded = false;
                (UIElementAutomationPeer.FromElement(owner) as Peer)?.RefreshChildren();
            }
        }

        private sealed class Scrolling(SelectionList owner) : IScrollProvider
        {
            public bool HorizontallyScrollable => false;
            public bool VerticallyScrollable => true;
            public double HorizontalScrollPercent => -1;
            public double VerticalScrollPercent => owner.position;
            public double HorizontalViewSize => 100;
            public double VerticalViewSize => 50;

            public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount) =>
                throw new InvalidOperationException("Scrollは使用しません。");

            public void SetScrollPercent(double horizontalPercent, double verticalPercent)
            {
                if (horizontalPercent != -1)
                {
                    throw new InvalidOperationException("水平位置の変更は禁止です。");
                }

                owner.ScrollCalls.Add(verticalPercent);
                owner.position = verticalPercent;
            }
        }

        private sealed class GroupPeer(FrameworkElement owner, List<AutomationPeer> children)
            : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

            protected override string GetClassNameCore() => "SelectionGroup";

            protected override List<AutomationPeer> GetChildrenCore() => children;
        }
    }

    private static int selectionCalls;
    private sealed class NoTableGrid : DataGrid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(NoTableGrid owner) : FrameworkElementAutomationPeer(owner)
        {
            private readonly DataGridAutomationPeer original = new(owner);

            protected override AutomationControlType GetAutomationControlTypeCore() =>
                AutomationControlType.DataGrid;

            protected override string GetClassNameCore() => "NoTableGrid";

            protected override List<AutomationPeer> GetChildrenCore() =>
                original.GetChildren() ?? [];

            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Table ? null!
                    : original.GetPattern(patternInterface);
        }
    }

    private sealed class FaultGrid(int failure) : FrameworkElement
    {
        internal int Failure { get; set; } = failure;

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(FaultGrid owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override bool IsControlElementCore() => true;

            protected override bool IsContentElementCore() => true;

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

            protected override string GetClassNameCore() => "FaultDataGrid";

            public override object GetPattern(PatternInterface patternInterface)
            {
                return patternInterface == PatternInterface.Selection
                    ? new Selection(owner) : base.GetPattern(patternInterface);
            }
        }

        private sealed class Selection(FaultGrid owner) : ISelectionProvider
        {
            public bool CanSelectMultiple => true;

            public bool IsSelectionRequired => false;

            public IRawElementProviderSimple[] GetSelection()
            {
                Interlocked.Increment(ref selectionCalls);
                throw new COMException("専用providerの検証用失敗です。", owner.Failure);
            }
        }
    }

    private sealed record Row(string First, string Second);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--getstr-target", string targetReceipt, string targetKind])
        {
            RunGetStrTarget(targetReceipt, targetKind);
            return;
        }

        if (args.Contains("--sliders"))
        {
            RunSliders();
            return;
        }

        if (args.Contains("--list-actions"))
        {
            RunListActions(args.Contains("--pointer-scroll"), args.Contains("--multiple"));
            return;
        }

        var application = new Application();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            EnableRowVirtualization = false,
            EnableColumnVirtualization = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            IsReadOnly = !args.Contains("--actions")
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "First", Binding = new Binding("First") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Second", Binding = new Binding("Second") });
        Row[] rows = [new(" first ", "日本語"), new("second", "")];
        grid.ItemsSource = rows;
        var window = new Window
        {
            Title = "UWSCSharp UIA DataGrid fixture",
            Width = 360,
            Height = 220,
            ShowActivated = false,
            Content = grid
        };
        if (args.Contains("--actions"))
        {
            window.Content = null;
            var panel = new StackPanel();
            panel.Children.Add(new Button { Content = "push" });
            panel.Children.Add(grid);
            window.Content = panel;
        }
        bool announced = false;
        window.ContentRendered += (_, _) =>
        {
            if (announced)
            {
                return;
            }

            announced = true;
            grid.SelectedItems.Add(rows[0]);
            grid.SelectedItems.Add(rows[1]);
            Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
            Console.Out.Flush();
        };
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is string command)
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (command == "clear")
                    {
                        grid.SelectedItems.Clear();
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                    else if (command == "disable")
                    {
                        grid.IsEnabled = false;
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                    else if (command == "exit")
                    {
                        window.Close();
                    }
                    else if (command is "fail-known" or "fail-unknown")
                    {
                        int failure = command == "fail-known"
                            ? unchecked((int)0x80040201) : unchecked((int)0x80004005);
                        if (window.Content is FaultGrid fault)
                        {
                            fault.Failure = failure;
                        }
                        else
                        {
                            window.Content = new FaultGrid(failure);
                        }
                        window.UpdateLayout();
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                    else if (command == "stats")
                    {
                        Console.WriteLine(Volatile.Read(ref selectionCalls));
                        Console.Out.Flush();
                    }
                    else if (command == "scrollstats" && window.Content is SelectionList selectionList)
                    {
                        Console.WriteLine(string.Join(",", selectionList.ScrollCalls));
                        Console.Out.Flush();
                    }
                    else if (command == "combostats" && window.Content is SelectionList comboList)
                    {
                        Console.WriteLine(string.Join(",", comboList.ExpandCalls)
                            + (comboList.IsExpanded ? ":Expanded" : ":Collapsed"));
                        Console.Out.Flush();
                    }
                    else if (command == "headers-no-table")
                    {
                        var noTable = new NoTableGrid
                        {
                            AutoGenerateColumns = false,
                            CanUserAddRows = false,
                            EnableRowVirtualization = false,
                            EnableColumnVirtualization = false,
                            IsReadOnly = true,
                            ItemsSource = rows
                        };
                        noTable.Columns.Add(new DataGridTextColumn
                        {
                            Header = "First",
                            Binding = new Binding("First")
                        });
                        noTable.Columns.Add(new DataGridTextColumn
                        {
                            Header = "Second",
                            Binding = new Binding("Second")
                        });
                        window.Content = noTable;
                        window.UpdateLayout();
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                    else if (command == "clear-list" && window.Content is SelectionList selectedList)
                    {
                        selectedList.SelectedItems.Clear();
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                    else if (command is "list-direct" or "list-nested" or "combo" or "combo-expanded"
                        or "combo-selection" or "combo-selection-expanded")
                    {
                        var list = new SelectionList(command == "list-nested",
                            command.StartsWith("combo", StringComparison.Ordinal),
                            command.EndsWith("expanded", StringComparison.Ordinal),
                            command.StartsWith("combo-selection", StringComparison.Ordinal))
                        {
                            SelectionMode = SelectionMode.Multiple
                        };
                        VirtualizingPanel.SetIsVirtualizing(list, false);
                        string[] names = ["", "日本語", "", "skip"];
                        for (int index = 0; index < names.Length; index++)
                        {
                            var item = new ListBoxItem { Content = new TextBlock { Text = names[index] } };
                            AutomationProperties.SetName(item, "parent" + index);
                            list.Items.Add(item);
                            item.IsSelected = index != 3;
                        }

                        window.Content = list;
                        window.UpdateLayout();
                        Console.WriteLine("OK");
                        Console.Out.Flush();
                    }
                });
            }

            await window.Dispatcher.InvokeAsync(window.Close);
        });
        application.Run(window);
    }
}
