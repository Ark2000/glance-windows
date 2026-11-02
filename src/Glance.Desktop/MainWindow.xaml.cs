using System;
using System.ComponentModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Glance.Core;
using Forms = System.Windows.Forms;

namespace Glance.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly BudgetLedger ledger;
    private Settings settings;
    private readonly HttpClient http;
    private GlobalHotkey? hotkey;
    private Forms.NotifyIcon? tray;
    private CancellationTokenSource? cancellation;
    private bool quitting;
    private bool quitAfterRequest;
    private bool savingSettings;
    private SettingsWindow? settingsWindow;
    private HistoryWindow? historyWindow;
    private double expandedHeight = 280;

    public MainWindow(LocalStore store, Settings settings, HttpClient? client = null)
    {
        InitializeComponent(); this.store = store; this.settings = settings; ledger = new BudgetLedger(store);
        http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
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
        MinWidth = Math.Min(340, MaxWidth);
        MinHeight = Math.Min(AnswerPanel.Visibility == Visibility.Visible ? 196 : 116, MaxHeight);
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
        if (savingSettings) { quitAfterRequest = true; Status.Text = "正在保存设置…"; return; }
        quitting = true; settingsWindow?.Close(); historyWindow?.Close(); hotkey?.Dispose(); tray?.Dispose(); http.Dispose(); Close(); Application.Current.Shutdown();
    }
    private void UpdateLabels()
    {
        ShortcutLabel.Text = hotkey?.Current.Length > 0 ? settings.Hotkey : "托盘可打开";
        ModeLabel.Text = $"{settings.Model} · {(settings.Thinking ? "思考" : "直答")}";
        ModeLabel.ToolTip = $"{settings.Model} · {(settings.Thinking ? "思考" : "非思考")} · 单轮流式回答";
    }
    private void ExpandAnswer()
    {
        if (AnswerPanel.Visibility == Visibility.Visible) return;
        AnswerPanel.Visibility = Visibility.Visible;
        MinHeight = Math.Min(196, MaxHeight);
        Height = Math.Min(expandedHeight, MaxHeight);
        ClampToScreen();
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
        try { snapshot.Validate(); }
        catch (Exception ex) { Status.Text = App.SafeError(ex); return; }
        using var cts = new CancellationTokenSource();
        cancellation = cts;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(snapshot.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        AskButton.IsEnabled = false; Question.IsReadOnly = true; NewButton.Visibility = Visibility.Collapsed; StopButton.Visibility = Visibility.Visible; CopyButton.Visibility = Visibility.Collapsed;
        Answer.Clear(); ExpandAnswer(); AnswerLabel.Text = "正在理解…"; Status.Text = "正在连接…";
        Charge? charge = null;
        var answer = new StreamingTextBuffer(); var progress = new CompletionProgress(); var resultStatus = "已完成";
        var renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        bool Flush()
        {
            var pending = answer.Drain(4096);
            if (pending.Length == 0) return false;
            Answer.AppendText(pending);
            Status.Text = "正在回答…";
            return true;
        }
        renderTimer.Tick += (_, _) => Flush();
        renderTimer.Start();
        var firstChunk = 0;
        try
        {
            charge = await Task.Run(() => ledger.Reserve(snapshot, question, DateTimeOffset.Now), linked.Token);
            await Task.Run(() => new CompletionClient(http).StreamAsync(snapshot, key, question, text =>
            {
                answer.Append(text);
                if (Interlocked.Exchange(ref firstChunk, 1) == 0)
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (cancellation == cts) Flush(); });
            }, progress, linked.Token), linked.Token);
            resultStatus = progress.FinishReason == "length" ? "已达输出上限" : answer.Length == 0 ? "服务返回空回答" : "已完成";
        }
        catch (OperationCanceledException) { resultStatus = cts.IsCancellationRequested ? "已停止" : "请求超时"; }
        catch (Exception ex) { resultStatus = App.SafeError(ex); }
        finally
        {
            renderTimer.Stop();
            while (Flush()) await Dispatcher.Yield(DispatcherPriority.Background);
            try
            {
                if (charge is not null)
                {
                    Status.Text = "正在保存…";
                    charge = await Task.Run(() =>
                    {
                        var settled = ledger.Settle(charge, snapshot, progress.Usage, progress.Rejected || !progress.Started);
                        store.SaveHistory(new HistoryEntry(settled.Id, settled.Time, question, answer.Text, snapshot.Model, resultStatus, settled.Amount, settled.Estimated));
                        return settled;
                    });
                    Status.Text = $"{(charge.Estimated ? "预留" : "估算")} ${charge.Amount:0.######}";
                }
                else Status.Text = resultStatus;
            }
            catch { Status.Text = resultStatus + " · 本机保存失败，请检查磁盘；已预留费用保留。"; }
            AnswerLabel.Text = resultStatus;
            cancellation = null; Question.IsReadOnly = false; AskButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text); NewButton.Visibility = Visibility.Visible; StopButton.Visibility = Visibility.Collapsed; CopyButton.Visibility = answer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (quitAfterRequest) _ = Dispatcher.BeginInvoke(Quit);
        }
    }
    private void Stop(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Status.Text = "正在停止…"; }
    private void CopyAnswer(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Answer.Text); Status.Text = "已复制回答"; } catch { Status.Text = "剪贴板忙，请重试。"; } }
    private void NewQuery(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        expandedHeight = ActualHeight;
        Question.Clear(); Answer.Clear(); AnswerLabel.Text = ""; Status.Text = "Enter 查询 · Esc 收起";
        CopyButton.Visibility = NewButton.Visibility = AnswerPanel.Visibility = Visibility.Collapsed;
        MinHeight = Math.Min(116, MaxHeight); Height = MinHeight; Question.Focus();
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, store.Root, ledger, SaveSettingsAsync);
        settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    private async Task SaveSettingsAsync(Settings next)
    {
        savingSettings = true;
        try
        {
            var previous = hotkey!.Current;
            hotkey!.Set(next.Hotkey);
            try { await Task.Run(() => store.Write("settings.json", next)); }
            catch { if (previous.Length == 0) hotkey.Clear(); else hotkey.Set(previous); throw; }
            settings = next; UpdateLabels(); Status.Text = "设置已生效；进行中的请求沿用发起时设置。";
        }
        finally
        {
            savingSettings = false;
            if (quitAfterRequest && cancellation is null) _ = Dispatcher.BeginInvoke(Quit);
        }
    }
    private void OpenHistory(object sender, RoutedEventArgs e)
    {
        if (historyWindow is not null) { historyWindow.Activate(); return; }
        historyWindow = new HistoryWindow(store, Review);
        historyWindow.Closed += (_, _) => historyWindow = null; historyWindow.Show();
    }
    public bool Review(HistoryEntry entry)
    {
        if (cancellation is not null) return false;
        Reveal(); ExpandAnswer(); Question.Text = entry.Question; Answer.Text = entry.Answer;
        AnswerLabel.Text = "历史 · " + entry.Model; Status.Text = entry.Status;
        CopyButton.Visibility = entry.Answer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewButton.Visibility = Visibility.Visible;
        return true;
    }
}
