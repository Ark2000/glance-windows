using System;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Glance.Core;
using Forms = System.Windows.Forms;

namespace Glance.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly BudgetLedger ledger;
    private Settings settings;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private GlobalHotkey? hotkey;
    private Forms.NotifyIcon? tray;
    private CancellationTokenSource? cancellation;
    private bool quitting;
    private bool quitAfterRequest;
    private SettingsWindow? settingsWindow;
    private HistoryWindow? historyWindow;

    public MainWindow(LocalStore store, Settings settings)
    {
        InitializeComponent(); this.store = store; this.settings = settings; ledger = new BudgetLedger(store);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Collapse(); e.Handled = true; } };
        Closing += OnClosing;
        Loaded += (_, _) => { if (hotkey is null) InitializeNative(this, EventArgs.Empty); ClampToScreen(); Question.Focus(); AskButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text); UpdateLabels(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ClampToScreen);
    }
    private void InitializeNative(object? sender, EventArgs e)
    {
        hotkey = new GlobalHotkey(new WindowInteropHelper(this).Handle, () => { if (IsVisible && IsActive) Collapse(); else Reveal(); });
        try { hotkey.Set(settings.Hotkey); }
        catch (Win32Exception ex) { Status.Text = ex.Message; }
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Glance;component/Assets/glance.ico"))!.Stream;
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(resource), Text = "知隙 · Glance", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开知隙", null, (_, _) => Dispatcher.Invoke(Reveal));
        menu.Items.Add("查询历史", null, (_, _) => Dispatcher.Invoke(() => OpenHistory(this, new RoutedEventArgs())));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => OpenSettings(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
    }
    private void ClampToScreen()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        MaxWidth = screen.Width / dpi.DpiScaleX; MaxHeight = screen.Height / dpi.DpiScaleY;
        MinWidth = Math.Min(400, MaxWidth); MinHeight = Math.Min(420, MaxHeight);
        Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        Left = Math.Clamp(Left, screen.Left / dpi.DpiScaleX, Math.Max(screen.Left / dpi.DpiScaleX, screen.Right / dpi.DpiScaleX - ActualWidth));
        Top = Math.Clamp(Top, screen.Top / dpi.DpiScaleY, Math.Max(screen.Top / dpi.DpiScaleY, screen.Bottom / dpi.DpiScaleY - ActualHeight));
    }
    public void Reveal() { Show(); WindowState = WindowState.Normal; ClampToScreen(); Activate(); Question.Focus(); Question.SelectAll(); }
    private void Collapse() { cancellation?.Cancel(); settingsWindow?.Close(); historyWindow?.Close(); Hide(); }
    private void HideWindow(object sender, RoutedEventArgs e) => Collapse();
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; Collapse(); } }
    public void Quit()
    {
        if (cancellation is not null) { quitAfterRequest = true; cancellation.Cancel(); Status.Text = "正在停止请求并保存…"; return; }
        quitting = true; settingsWindow?.Close(); historyWindow?.Close(); hotkey?.Dispose(); tray?.Dispose(); http.Dispose(); Close(); Application.Current.Shutdown();
    }
    private void UpdateLabels()
    {
        ShortcutLabel.Text = hotkey?.Current.Length > 0 ? settings.Hotkey + " 唤起" : "快捷键待设置 · 托盘可打开";
        ModeLabel.Text = $"{settings.Model} · {(settings.Thinking ? "思考" : "非思考")} · 单轮";
    }
    private void QuestionChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (AskButton is not null) AskButton.IsEnabled = cancellation is null && !string.IsNullOrWhiteSpace(Question.Text); }
    private void QuestionKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !e.IsRepeat) { e.Handled = true; if (cancellation is null) _ = QueryAsync(); } }
    private async void Ask(object sender, RoutedEventArgs e) => await QueryAsync();
    private async Task QueryAsync()
    {
        var question = Question.Text.Trim(); if (question.Length == 0 || cancellation is not null) return;
        var snapshot = settings;
        string key;
        try { key = KeyVault.Decrypt(snapshot.EncryptedKey); }
        catch { Status.Text = "Key 读取失败，请在设置中重新填写。"; return; }
        if (string.IsNullOrWhiteSpace(key)) { Status.Text = "请先在设置中保存 API Key。"; OpenSettings(this, new RoutedEventArgs()); return; }
        Charge charge;
        try { snapshot.Validate(); charge = ledger.Reserve(snapshot, question, DateTimeOffset.Now); }
        catch (Exception ex) { Status.Text = App.SafeError(ex); return; }
        using var cts = new CancellationTokenSource();
        cancellation = cts;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(snapshot.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        AskButton.IsEnabled = false; Question.IsReadOnly = true; NewButton.IsEnabled = false; StopButton.Visibility = Visibility.Visible; CopyButton.IsEnabled = false;
        Answer.Clear(); AnswerLabel.Text = "正在理解…"; Status.Text = "正在连接模型…";
        var answer = new StringBuilder(); var progress = new CompletionProgress(); var resultStatus = "已完成";
        try
        {
            await new CompletionClient(http).StreamAsync(snapshot, key, question, text =>
            {
                if (answer.Length + text.Length > 262144) throw new ApiException("回答已达本机显示上限，已保留收到的内容。");
                answer.Append(text); Answer.AppendText(text); Status.Text = "正在回答…";
            }, progress, linked.Token);
            resultStatus = progress.FinishReason == "length" ? "已达输出上限" : answer.Length == 0 ? "服务返回空回答" : "已完成";
        }
        catch (OperationCanceledException) { resultStatus = cts.IsCancellationRequested ? "已停止" : "请求超时"; }
        catch (Exception ex) { resultStatus = App.SafeError(ex); }
        finally
        {
            try
            {
                charge = ledger.Settle(charge, snapshot, progress.Usage, progress.Rejected || !progress.Started);
                store.SaveHistory(new HistoryEntry(charge.Id, charge.Time, question, answer.ToString(), snapshot.Model, resultStatus, charge.Amount, charge.Estimated));
                Status.Text = $"{resultStatus} · {(charge.Estimated ? "预留" : "用量估算")} ${charge.Amount:0.######}";
            }
            catch { Status.Text = resultStatus + " · 本机保存失败，请检查磁盘；已预留费用保留。"; }
            AnswerLabel.Text = resultStatus;
            cancellation = null; Question.IsReadOnly = false; AskButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text); NewButton.IsEnabled = true; StopButton.Visibility = Visibility.Collapsed; CopyButton.IsEnabled = answer.Length > 0;
            if (quitAfterRequest) _ = Dispatcher.BeginInvoke(Quit);
        }
    }
    private void Stop(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Status.Text = "正在停止…"; }
    private void CopyAnswer(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Answer.Text); Status.Text = "已复制回答"; } catch { Status.Text = "剪贴板忙，请重试。"; } }
    private void NewQuery(object sender, RoutedEventArgs e) { Question.Clear(); Answer.Clear(); AnswerLabel.Text = "一个问题，一次理解"; Status.Text = "准备就绪"; CopyButton.IsEnabled = false; Question.Focus(); }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, store.Root, ledger, SaveSettings);
        settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    private void SaveSettings(Settings next)
    {
        var previous = settings.Hotkey;
        hotkey!.Set(next.Hotkey);
        try { store.Write("settings.json", next); }
        catch { hotkey.Set(previous); throw; }
        settings = next; UpdateLabels(); Status.Text = "设置已生效；进行中的请求沿用发起时设置。";
    }
    private void OpenHistory(object sender, RoutedEventArgs e)
    {
        if (historyWindow is not null) { historyWindow.Activate(); return; }
        historyWindow = new HistoryWindow(store, entry =>
        {
            if (cancellation is not null) return false;
            Reveal(); Question.Text = entry.Question; Answer.Text = entry.Answer; AnswerLabel.Text = "历史回顾 · " + entry.Model; Status.Text = entry.Status; CopyButton.IsEnabled = entry.Answer.Length > 0; return true;
        });
        historyWindow.Closed += (_, _) => historyWindow = null; historyWindow.Show();
    }
}
