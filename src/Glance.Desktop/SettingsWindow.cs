using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Core;

namespace Glance.Desktop;

public sealed class SettingsWindow : Window
{
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly PasswordBox key = new();
    private readonly CheckBox clearKey = new() { Content = "清除已保存 Key", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox thinking = new() { Content = "启用思考模式（响应时间与用量会增加）", Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Settings original;
    public SettingsWindow(Settings current, string root, BudgetLedger ledger, Func<Settings, Task> save)
    {
        original = current; Title = "知隙 · 设置"; Style = (Style)Application.Current.FindResource(typeof(Window)); Width = 540; Height = 600; MinWidth = 400; MinHeight = 360;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = "设置", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }; grid.Children.Add(title);
        var tabs = new TabControl(); Grid.SetRow(tabs, 1); grid.Children.Add(tabs);
        var basic = Tab(tabs, "连接与回答");
        Note(basic, "填写自己的 API Key。查询内容和回答指令会发送至所设接口；历史记录保存在本机。默认使用 DeepSeek 非思考模式。");
        Label(basic, current.EncryptedKey.Length > 0 ? "API Key · 已加密保存，留空保留" : "API Key");
        System.Windows.Automation.AutomationProperties.SetName(key, "API Key"); basic.Children.Add(key); basic.Children.Add(clearKey);
        Field(basic, "Endpoint", "接口基础地址 / 完整 chat/completions 地址", current.Endpoint);
        Field(basic, "Model", "模型 · 可填写接口支持的模型名称", current.Model);
        Field(basic, "Hotkey", "全局快捷键 · 示例 Ctrl+Alt+Space", current.Hotkey);
        Field(basic, "SystemPrompt", "回答方式 · 每次查询使用的指令", current.SystemPrompt, 120);
        thinking.IsChecked = current.Thinking; basic.Children.Add(thinking);
        Note(basic, "每次请求仅包含回答指令与当前问题。历史记录可手动复用。Esc 收起时会停止当前生成。");

        var advanced = Tab(tabs, "统一高级设置");
        Field(advanced, "MaxTokens", "最大输出 tokens · 1–32768", N(current.MaxTokens));
        Field(advanced, "Temperature", "Temperature · 0–2", N(current.Temperature));
        Field(advanced, "TopP", "Top P · 大于0，至多1", N(current.TopP));
        Field(advanced, "TimeoutSeconds", "请求超时 · 秒，10–600", N(current.TimeoutSeconds));
        Field(advanced, "ExtraJson", "附加 API 参数 · JSON 对象，例如 {\"stop\":[\"END\"]}", current.ExtraJson, 90);
        Note(advanced, "单轮、流式、模型、思考、输出长度使用专用字段。工具调用与多回答已禁用。兼容接口须支持 thinking 与 stream_options。参数的实际支持情况由所选模型决定。");
        Label(advanced, "费用边界 · USD");
        Note(advanced, "按下列自定义单价预留费用，再结合服务回执用量结算。默认参考 DeepSeek Flash 高峰单价，按输入缓存未命中估算。换模型或接口时请同步修改单价。");
        Field(advanced, "InputRate", "每百万输入 tokens · USD", N(current.InputRate));
        Field(advanced, "OutputRate", "每百万输出 tokens · USD", N(current.OutputRate));
        Field(advanced, "RequestLimit", "单次限额 · USD", N(current.RequestLimit));
        Field(advanced, "DailyLimit", "每日限额 · USD", N(current.DailyLimit));
        Field(advanced, "MonthlyLimit", "每月限额 · USD", N(current.MonthlyLimit));
        Note(advanced, $"本机今日累计 ${ledger.DayTotal(DateTimeOffset.Now):0.######}；本月累计 ${ledger.MonthTotal(DateTimeOffset.Now):0.######}。\n预留 = (输入 UTF-8 字节数 + 256) × 输入单价 + 最大输出 tokens × 输出单价，除以一百万。\n中断、超时、断网或缺少回执时保留全额预留；HTTP 拒绝释放预留。日/月按本机时间计算，历史删除后费用账本继续保留。限额适用于本程序的本机估算；服务账单以供应商记录为准。");

        var data = Tab(tabs, "本机数据");
        Label(data, "数据保存位置"); Note(data, root);
        Note(data, "settings.json：通用设置与加密 Key\nhistory/：逐条查询记录（明文）\nledger.json：独立费用账本\n\nKey 使用 Windows DPAPI 当前用户加密，迁移到其他账户后请重新填写。历史内容可通过历史窗口搜索、删除和导出。\n\n软件无遥测、无自动更新。模型查询仅访问设置中的接口。导出文件包含所选历史中的问题和回答，请按需要保存。");
        var bottom = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(bottom, 2); grid.Children.Add(bottom); bottom.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => Close();
        var submit = new Button { Content = "保存并生效", Style = (Style)FindResource("Primary") };
        var saving = false;
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        submit.Click += async (_, _) =>
        {
            var saved = false;
            try
            {
                var next = Read(); next.Validate(); saving = true;
                submit.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = false; message.Text = "正在保存…";
                await save(next); saved = true;
            }
            catch (Exception ex) { message.Text = ex is System.ComponentModel.Win32Exception ? ex.Message : App.SafeError(ex); }
            finally { saving = false; submit.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = true; }
            if (saved) Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(submit); bottom.Children.Add(buttons); Content = grid;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
    private static string N<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
    private string S(string name) => fields[name].Text.Trim();
    private int I(string name) => int.TryParse(S(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException("请填写有效整数：" + name);
    private double D(string name) => double.TryParse(S(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException("请填写有效数值：" + name);
    private decimal M(string name) => decimal.TryParse(S(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException("请填写有效金额：" + name);
    private Settings Read() => original with
    {
        EncryptedKey = clearKey.IsChecked == true ? "" : key.Password.Length > 0 ? KeyVault.Encrypt(key.Password.Trim()) : original.EncryptedKey,
        Endpoint = S("Endpoint"), Model = S("Model"), Hotkey = S("Hotkey"), SystemPrompt = S("SystemPrompt"), Thinking = thinking.IsChecked == true,
        MaxTokens = I("MaxTokens"), Temperature = D("Temperature"), TopP = D("TopP"), TimeoutSeconds = I("TimeoutSeconds"), ExtraJson = S("ExtraJson"),
        InputRate = M("InputRate"), OutputRate = M("OutputRate"), RequestLimit = M("RequestLimit"), DailyLimit = M("DailyLimit"), MonthlyLimit = M("MonthlyLimit")
    };
    private static StackPanel Tab(TabControl tabs, string header)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }); return panel;
    }
    private static void Label(StackPanel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 15, 0, 7) });
    private static void Note(StackPanel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 6, 0, 9), LineHeight = 21 });
    private void Field(StackPanel panel, string id, string label, string value, double height = double.NaN)
    {
        Label(panel, label); var box = new TextBox { Text = value, Height = height, MaxLength = 16000 };
        if (!double.IsNaN(height)) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.VerticalContentAlignment = VerticalAlignment.Top; }
        System.Windows.Automation.AutomationProperties.SetName(box, id); fields[id] = box; panel.Children.Add(box);
    }
}
