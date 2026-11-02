using System;
using System.IO;
using System.Threading;
using System.Windows;
using Glance.Core;

namespace Glance.Desktop;

public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var testRoot = Environment.GetEnvironmentVariable("GLANCE_DATA_DIR");
        mutex = new Mutex(true, "Local\\Glance.Desktop." + Environment.UserName + (testRoot is null ? "" : ".dev"), out var first);
        if (!first) { MessageBox.Show("知隙已在运行，可通过托盘或快捷键打开。", "知隙"); Shutdown(); return; }
        try
        {
            var root = testRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance");
            var store = new LocalStore(root);
            var settings = store.Read("settings.json", new Settings());
            settings.Validate();
            var main = new MainWindow(store, settings);
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败，请检查本机数据目录的文件与权限。\n" + SafeError(ex), "知隙");
            Shutdown(1);
        }
    }
    public static string SafeError(Exception ex) => ex is System.Text.Json.JsonException ? "JSON 格式有误，请检查附加参数。" : ex is ArgumentException or IOException or InvalidOperationException or ApiException ? ex.Message : "操作失败，请检查本机文件、网络或设置后重试。";
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
