using System.Windows;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

/// <summary>
/// 保存履歴をページ単位で読み、選択した発生の詳細だけを表示します。
/// </summary>
internal sealed class SchedulerHistoryWindow : Window
{
    private readonly SchedulerHistoryBrowser browser;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ListBox rows = new();
    private readonly TextBox details = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Stack<SchedulerHistoryCursor?> previous = new();
    private readonly Button reload;
    private readonly Button back;
    private readonly Button next;
    private SchedulerHistoryCursor? cursor;
    private bool loading;
    private bool closed;
    private CancellationTokenSource? selection;

    internal SchedulerHistoryWindow(SchedulerManagedStorage storage)
    {
        Title = "予約の実行履歴";
        Width = 920;
        Height = 700;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        MinHeight = Math.Min(480, MaxHeight);
        MinWidth = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        browser = new SchedulerHistoryBrowser((before, token) => Task.Run(
            () => storage.ReadHistoryPage(before, 50, token), token),
            (id, token) => Task.Run(() => storage.ReadHistoryRecord(id), token));
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(new TextBlock
        {
            Text = "保存済み履歴を受付UTCと発生識別子の降順で50件ずつ表示します。未完了の保存記録が、現在も実行中であるとは限りません。長い題名は一覧で省略し、詳細で全文を表示します。",
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new WrapPanel();
        top.Children.Add(actions);
        reload = SchedulerEntryWindow.Button(actions, "最新へ再読込み", "HistoryReload");
        back = SchedulerEntryWindow.Button(actions, "前のページ", "HistoryBack");
        next = SchedulerEntryWindow.Button(actions, "次のページ", "HistoryNext");
        SchedulerEntryWindow.Identify(status, "HistoryStatus");
        top.Children.Add(status);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.Children.Add(grid);
        SchedulerEntryWindow.Identify(rows, "HistoryRows");
        grid.Children.Add(rows);
        SchedulerEntryWindow.Identify(details, "HistoryDetails");
        Grid.SetRow(details, 1);
        details.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(details);
        reload.Click += async (_, _) => await LoadAsync(null, () => previous.Clear());
        next.Click += async (_, _) =>
        {
            if (browser.Current?.NextCursor is { } target)
            {
                SchedulerHistoryCursor? old = cursor;
                await LoadAsync(target, () => previous.Push(old));
            }
        };
        back.Click += async (_, _) =>
        {
            if (previous.TryPeek(out SchedulerHistoryCursor? target))
            {
                await LoadAsync(target, () => previous.Pop());
            }
        };
        rows.SelectionChanged += async (_, _) => await SelectAsync();
        Loaded += async (_, _) => await LoadAsync(null, () => previous.Clear());
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            selection?.Cancel();
            selection?.Dispose();
            lifetime.Dispose();
        };
        RefreshButtons();
    }

    private async Task LoadAsync(SchedulerHistoryCursor? target, Action onSuccess)
    {
        if (loading || closed)
        {
            return;
        }
        loading = true;
        RefreshButtons();
        status.Text = "履歴を読込み中…";
        bool accepted = await browser.LoadPageAsync(target, lifetime.Token);
        if (closed)
        {
            return;
        }
        if (accepted)
        {
            cursor = target;
            onSuccess();
            details.Text = "";
            rows.Items.Clear();
            foreach (SchedulerHistorySummary value in browser.Current!.Items)
            {
                rows.Items.Add(new ListBoxItem { Content = SchedulerHistoryPresentation.Summary(value), Tag = value });
            }
            status.Text = $"{rows.Items.Count}件。詳細は一覧から選択してください。";
        }
        else if (browser.PageError is { } error)
        {
            status.Text = "履歴を読み込めません。" + (browser.Current is null ? "" : "直前の一覧を保持しています。")
                + Environment.NewLine + error.Message;
        }
        loading = false;
        RefreshButtons();
    }

    private async Task SelectAsync()
    {
        CancellationTokenSource? previousSelection = selection;
        selection = null;
        previousSelection?.Cancel();
        previousSelection?.Dispose();
        details.Text = "";
        if (closed || rows.SelectedItem is not ListBoxItem { Tag: SchedulerHistorySummary selected })
        {
            return;
        }
        selection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        details.Text = "詳細を読込み中…";
        bool accepted = await browser.SelectAsync(selected.OccurrenceId, selection.Token);
        if (closed || rows.SelectedItem is not ListBoxItem { Tag: SchedulerHistorySummary current }
            || current.OccurrenceId != selected.OccurrenceId)
        {
            return;
        }
        if (accepted)
        {
            details.Text = SchedulerHistoryPresentation.Details(browser.Detail!);
        }
        else if (browser.DetailError is { } error)
        {
            details.Text = "詳細を読み込めません。保存済みの記録が整理された可能性があります。"
                + Environment.NewLine + error.Message;
        }
    }

    private void RefreshButtons()
    {
        reload.IsEnabled = !loading && !closed;
        back.IsEnabled = !loading && !closed && previous.Count > 0;
        next.IsEnabled = !loading && !closed && browser.Current?.HasMore == true;
    }
}
