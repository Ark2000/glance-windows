using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Glance.Core;
using Microsoft.Win32;

namespace Glance.Desktop;

public sealed class HistoryWindow : Window
{
    private readonly LocalStore store;
    private List<HistoryEntry> all = [];
    private List<HistoryEntry> filtered = [];
    private readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox preview = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    public HistoryWindow(LocalStore store, Func<HistoryEntry, bool> reuse)
    {
        this.store = store; Title = "知隙 · 本机历史"; Style = (Style)Application.Current.FindResource(typeof(Window)); Width = 680; Height = 720; MinWidth = 430; MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { Margin = new Thickness(22) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = h });
        grid.Children.Add(new TextBlock { Text = "把理解留在本机", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索历史"); search.ToolTip = "搜索问题与回答";
        Grid.SetRow(search, 1); grid.Children.Add(search); Grid.SetRow(list, 2); grid.Children.Add(list); Grid.SetRow(preview, 3); grid.Children.Add(preview);
        var footer = new StackPanel(); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(buttons, "导出搜索结果", Export);
        AddButton(buttons, "删除所选", (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: HistoryEntry entry }) return;
            if (MessageBox.Show(this, "删除这条本机查询记录？费用账本继续保留。", "删除记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { store.DeleteHistory(entry.Id); Reload(); } catch (Exception ex) { status.Text = App.SafeError(ex); }
        });
        AddButton(buttons, "复用所选", (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: HistoryEntry entry }) { if (reuse(entry)) Close(); else status.Text = "请先停止进行中的查询。"; } });
        footer.Children.Add(buttons); footer.Children.Add(status); Content = grid;
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) =>
        {
            preview.Text = list.SelectedItem is ListBoxItem { Tag: HistoryEntry entry } ? $"{entry.Question}\n\n{entry.Answer}\n\n{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.Model} · {entry.Status}\n{(entry.Estimated ? "预留" : "用量估算")} ${entry.Cost:0.######}" : "选择记录后查看完整回答。";
        };
        Activated += (_, _) => Reload();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
    private static void AddButton(Panel panel, string text, RoutedEventHandler handler) { var button = new Button { Content = text }; button.Click += handler; panel.Children.Add(button); }
    private void Reload() { try { all = store.History(); Filter(); } catch (Exception ex) { status.Text = App.SafeError(ex); } }
    private void Filter()
    {
        var selected = (list.SelectedItem as ListBoxItem)?.Tag as HistoryEntry;
        var term = search.Text.Trim(); filtered = all.Where(e => e.Question.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Answer.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Items.Clear();
        foreach (var entry in filtered)
        {
            var text = new StackPanel { Margin = new Thickness(7) };
            text.Children.Add(new TextBlock { Text = entry.Question, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{entry.Time.ToLocalTime():MM-dd HH:mm}   ·   {entry.Model}   ·   {entry.Status}", FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new ListBoxItem { Content = text, Tag = entry }; list.Items.Add(item); if (entry.Id == selected?.Id) list.SelectedItem = item;
        }
        status.Text = filtered.Count == 0 ? "暂无匹配记录。" : $"{filtered.Count} 条记录 · 导出包含当前搜索结果";
    }
    private void Export(object sender, RoutedEventArgs e)
    {
        if (filtered.Count == 0) { status.Text = "当前搜索结果为空。"; return; }
        var dialog = new SaveFileDialog { FileName = "Glance-history-" + DateTime.Now.ToString("yyyyMMdd"), Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = dialog.FilterIndex == 2 ? HistoryExporter.Json(filtered) : HistoryExporter.Markdown(filtered);
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false)); status.Text = $"已导出 {filtered.Count} 条记录。";
        }
        catch (Exception ex) { status.Text = App.SafeError(ex); }
    }
}
