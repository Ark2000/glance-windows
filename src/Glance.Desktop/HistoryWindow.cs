using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Glance.Core;
using Microsoft.Win32;

namespace Glance.Desktop;

public sealed record HistoryRow(HistoryEntry Entry)
{
    public string Question => Entry.Question;
    public string Details => $"{Entry.Time.ToLocalTime():MM-dd HH:mm} · {Entry.Model} · {Entry.Status}";
}

public sealed class HistoryWindow : Window
{
    private readonly LocalStore store;
    private List<HistoryRow> all = [];
    private List<HistoryRow> filtered = [];
    private long loadedRevision = -1;
    private int filterVersion;
    private bool loading;
    private bool closed;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? pendingFilter;
    private readonly DispatcherTimer searchTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(9, 6, 9, 6) };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox preview = new() { IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };

    public HistoryWindow(LocalStore store, Func<HistoryEntry, bool> reuse)
    {
        this.store = store; Title = "知隙 · 本机历史"; Style = (Style)Application.Current.FindResource(typeof(Window));
        Width = 560; Height = 520; MinWidth = 380; MinHeight = 360;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { Margin = new Thickness(14) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var refresh = new Button { Content = "刷新", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 4, 8, 4) };
        refresh.Click += async (_, _) => await ReloadAsync(true); DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        header.Children.Add(new TextBlock { Text = "本机历史", FontSize = 18, FontWeight = FontWeights.SemiBold }); grid.Children.Add(header);
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索历史"); search.ToolTip = "搜索问题与回答";
        Grid.SetRow(search, 1); grid.Children.Add(search); Grid.SetRow(list, 2); grid.Children.Add(list); Grid.SetRow(preview, 3); grid.Children.Add(preview);
        list.ItemTemplate = (DataTemplate)FindResource("HistoryItem");
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var footer = new StackPanel(); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(buttons, "导出结果", Export);
        AddButton(buttons, "删除所选", async (_, _) =>
        {
            if (list.SelectedItem is not HistoryRow row) return;
            if (MessageBox.Show(this, "删除这条本机查询记录？费用账本继续保留。", "删除记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { await Task.Run(() => store.DeleteHistory(row.Entry.Id)); await ReloadAsync(true); }
            catch (Exception ex) { if (!closed) status.Text = App.SafeError(ex); }
        });
        AddButton(buttons, "复用所选", (_, _) =>
        {
            if (list.SelectedItem is not HistoryRow row) return;
            if (reuse(row.Entry)) Close(); else status.Text = "请先停止进行中的查询。";
        });
        footer.Children.Add(buttons); footer.Children.Add(status); Content = grid;
        search.TextChanged += (_, _) =>
        {
            filterVersion++; pendingFilter?.Cancel(); searchTimer.Stop(); searchTimer.Start();
        };
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); await FilterAsync(); };
        list.SelectionChanged += (_, _) =>
        {
            preview.Text = list.SelectedItem is HistoryRow row
                ? $"{row.Entry.Question}\n\n{row.Entry.Answer}\n\n{row.Entry.Time.ToLocalTime():yyyy-MM-dd HH:mm} · {row.Entry.Model} · {row.Entry.Status}\n{(row.Entry.Estimated ? "预留" : "用量估算")} ${row.Entry.Cost:0.######}"
                : "选择记录后查看完整回答。";
        };
        Activated += async (_, _) => await ReloadAsync();
        Closed += (_, _) => { closed = true; searchTimer.Stop(); pendingFilter?.Cancel(); lifetime.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private static void AddButton(Panel panel, string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5) }; button.Click += handler; panel.Children.Add(button);
    }

    private async Task ReloadAsync(bool force = false)
    {
        if (closed || loading || (!force && loadedRevision == store.HistoryRevision)) return;
        loading = true; filterVersion++; pendingFilter?.Cancel(); status.Text = "正在读取历史…";
        var revision = store.HistoryRevision;
        try
        {
            var rows = await Task.Run(() => store.History(cancellationToken: lifetime.Token).Select(entry => new HistoryRow(entry)).ToList(), lifetime.Token);
            if (closed) return;
            all = rows; loadedRevision = revision; await FilterAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) status.Text = App.SafeError(ex); }
        finally { loading = false; }
    }

    private async Task FilterAsync()
    {
        if (closed) return;
        var version = ++filterVersion;
        pendingFilter?.Cancel();
        using var filter = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pendingFilter = filter;
        var rows = all; var term = search.Text.Trim();
        try
        {
            var matches = await Task.Run(() =>
            {
                if (term.Length == 0) return rows;
                var result = new List<HistoryRow>();
                foreach (var row in rows)
                {
                    filter.Token.ThrowIfCancellationRequested();
                    if (row.Entry.Question.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Entry.Answer.Contains(term, StringComparison.OrdinalIgnoreCase)) result.Add(row);
                }
                return result;
            }, filter.Token);
            if (closed || version != filterVersion) return;
            var selectedId = (list.SelectedItem as HistoryRow)?.Entry.Id;
            filtered = matches;
            list.ItemsSource = filtered;
            if (selectedId is not null) list.SelectedItem = filtered.FirstOrDefault(row => row.Entry.Id == selectedId);
            status.Text = filtered.Count == 0 ? "暂无匹配记录。" : $"{filtered.Count} 条记录 · 导出当前搜索结果";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) status.Text = App.SafeError(ex); }
        finally { if (pendingFilter == filter) pendingFilter = null; }
    }

    private async void Export(object sender, RoutedEventArgs e)
    {
        searchTimer.Stop(); await FilterAsync();
        if (closed) return;
        var rows = filtered;
        if (rows.Count == 0) { status.Text = "当前搜索结果为空。"; return; }
        var dialog = new SaveFileDialog { FileName = "Glance-history-" + DateTime.Now.ToString("yyyyMMdd"), Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var json = dialog.FilterIndex == 2; var path = dialog.FileName;
        var button = (Button)sender; button.IsEnabled = false; status.Text = "正在导出…";
        try
        {
            await Task.Run(() =>
            {
                var entries = rows.Select(row => row.Entry);
                var text = json ? HistoryExporter.Json(entries) : HistoryExporter.Markdown(entries);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, text, new UTF8Encoding(false));
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            if (!closed) status.Text = $"已导出 {rows.Count} 条记录。";
        }
        catch (Exception ex) { if (!closed) status.Text = App.SafeError(ex); }
        finally { button.IsEnabled = true; }
    }
}
