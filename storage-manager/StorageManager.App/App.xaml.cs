using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using StorageManager.Core.Persistence;
using StorageManager.Core.Services;
using Forms = System.Windows.Forms;

namespace StorageManager.App;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private Forms.NotifyIcon? tray;
    private Store? store;
    public Engine Engine { get; private set; } = null!;
    public bool Exiting { get; private set; }
    public static App Instance => (App)Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataIndex = Array.IndexOf(e.Args, "--data-dir");
            var data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipXP", "StorageManager");
            data = Path.GetFullPath(data);
            var scope = WindowsIdentity.GetCurrent().User!.Value + ":" + data.ToUpperInvariant();
            var name = "Local\\ZipXP.StorageManager." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
            mutex = new Mutex(true, name, out var owns);
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
            if (!owns) { activation.Set(); mutex.Dispose(); mutex = null; Shutdown(); return; }
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);
            store = new Store(Path.Combine(data, "storage.sqlite3"));
            Engine = new(store);
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            MainWindow = new MainWindow();
            CreateTray();
            Engine.Start();
            if (!e.Args.Contains("--tray")) ShowMain();
            if (e.Args.Contains("--launch-check"))
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                timer.Tick += async (_, _) => { timer.Stop(); await ExitAsync(); };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--launch-check")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "Storage Manager startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open storage dashboard", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
        var pause = menu.Items.Add("Pause all / resume", null, (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            try { await Engine.SetPausedAsync(!Engine.Paused); }
            catch (Exception ex) { tray?.ShowBalloonTip(5000, "Pause setting failed", ex.Message, Forms.ToolTipIcon.Error); }
        }));
        menu.Opening += (_, _) => pause.Text = Engine.Paused ? "Resume automation" : "Pause after current file";
        menu.Items.Add("Exit after current file", null, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "ZipXP Storage Manager", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
    }

    public void ShowMain()
    {
        if (MainWindow == null || Exiting) return;
        MainWindow.Show(); MainWindow.WindowState = WindowState.Normal; MainWindow.Activate();
    }

    public void ExplainTray()
    {
        if (store?.Setting("trayExplained") == "true") return;
        store?.Setting("trayExplained", "true");
        tray?.ShowBalloonTip(7000, "Storage Manager is still running", "Closing the window keeps enabled policies running in the tray. Use Pause or Exit from the tray menu to stop new work.", Forms.ToolTipIcon.Info);
    }

    public async Task ExitAsync()
    {
        if (Exiting) return;
        Exiting = true;
        if (MainWindow is MainWindow window) window.ShowClosingStatus();
        if (Engine != null) await Engine.DisposeAsync();
        tray?.Dispose(); tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose(); activationWait?.Unregister(null); activation?.Dispose(); store?.Dispose();
        if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); }
        base.OnExit(e);
    }
}
