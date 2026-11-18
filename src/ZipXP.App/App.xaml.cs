using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ZipXP.Core;
using ZipXP.Core.Persistence;
using ZipXP.Core.Services;
using Forms = System.Windows.Forms;

namespace ZipXP.App;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private Forms.NotifyIcon? tray;
    private Store? store;
    private DispatcherTimer? savingsTimer;
    private bool hadPendingWork;
    private volatile bool checkingSavings;
    private long notifiedSavings;
    private DateTime lastCompletionBalloon = DateTime.MinValue;
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
            MainWindow = e.Args.Contains("--native-ui") ? new MainWindow() : new ReactWindow();
            CreateTray();
            WatchSavings();
            Engine.Start();
            if (!e.Args.Contains("--tray")) ShowMain();
            // Dev launcher (scripts/dev.sh) creates this file on Ctrl+C to ask for a clean shutdown.
            var quitIndex = Array.IndexOf(e.Args, "--quit-file");
            if (quitIndex >= 0 && quitIndex + 1 < e.Args.Length)
            {
                var quitFile = e.Args[quitIndex + 1];
                var quitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                quitTimer.Tick += async (_, _) =>
                {
                    bool requested; try { requested = File.Exists(quitFile); } catch (IOException) { requested = false; }
                    if (requested) { quitTimer.Stop(); await ExitAsync(); }
                };
                quitTimer.Start();
            }
            if (e.Args.Contains("--launch-check"))
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
                timer.Tick += async (_, _) => { timer.Stop(); await ExitAsync(); };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--launch-check")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "ZipXP couldn't start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try { using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/zipxp.ico"))!.Stream; return new System.Drawing.Icon(stream); }
        catch (Exception) { return System.Drawing.SystemIcons.Application; }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ZipXP", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
        var pause = menu.Items.Add("Pause", null, (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            try { await Engine.SetPausedAsync(!Engine.Paused); }
            catch (Exception ex) { tray?.ShowBalloonTip(5000, "Couldn't pause or resume", ex.Message, Forms.ToolTipIcon.Error); }
        }));
        menu.Opening += (_, _) => pause.Text = Engine.Paused ? "Resume" : "Pause";
        menu.Items.Add("Quit ZipXP", null, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()));
        tray = new Forms.NotifyIcon { Icon = LoadIcon(), Text = "ZipXP", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
    }

    /// <summary>Keeps the tray tooltip showing the running total and announces finished work and a weekly summary.</summary>
    private void WatchSavings()
    {
        notifiedSavings = store!.MeasuredSince(DateTime.MinValue).Delta;
        UpdateTrayTip(notifiedSavings);
        savingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        savingsTimer.Tick += (_, _) =>
        {
            if (Exiting || checkingSavings) return;
            checkingSavings = true;
            // Database reads can wait behind the engine, so keep them off the UI thread. Notifications are a
            // nicety: a transient database error must never disturb the app.
            _ = Task.Run(() => { try { return CheckSavings(); } catch (Exception) { return null; } })
                .ContinueWith(t => { checkingSavings = false; if (t.Result is { } apply && !Exiting) apply(); }, TaskScheduler.FromCurrentSynchronizationContext());
        };
        savingsTimer.Start();
    }

    private void UpdateTrayTip(long total)
    {
        if (tray != null) tray.Text = total > 0 ? $"ZipXP · {Display.Bytes(total)} saved" : "ZipXP";
    }

    /// <summary>Runs on a worker thread; returns the tray updates to apply on the UI thread.</summary>
    private Action? CheckSavings()
    {
        var pending = store!.HasActiveJobs();
        var total = store.MeasuredSince(DateTime.MinValue).Delta;
        var watching = false;
        Dispatcher.Invoke(() => watching = MainWindow is { IsVisible: true, IsActive: true });
        string? balloonTitle = null, balloonText = null;
        if (pending && !hadPendingWork) notifiedSavings = total; // measure each run from its own start
        if (hadPendingWork && !pending)
        {
            var gained = total - notifiedSavings;
            // Background auto-compression finishes many small runs, so it needs a much bigger gain to be worth a balloon.
            var bar = store.Roots().Any(r => r.Policy.Enabled) ? 100L * 1024 * 1024 : 1024 * 1024;
            if (gained >= bar && !watching && DateTime.UtcNow - lastCompletionBalloon > TimeSpan.FromMinutes(30))
            {
                balloonTitle = "ZipXP finished compressing"; balloonText = $"Saved {Display.Bytes(gained)}. Your files open as usual.";
                lastCompletionBalloon = DateTime.UtcNow;
            }
            notifiedSavings = total;
        }
        hadPendingWork = pending;

        // At most one summary a week, and only when ZipXP actually saved something.
        var last = DateTime.TryParse(store.Setting("lastDigestUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTime?)null;
        if (last == null) store.Setting("lastDigestUtc", DateTime.UtcNow.ToString("O"));
        else if (DateTime.UtcNow - last.Value >= TimeSpan.FromDays(7))
        {
            var week = store.MeasuredSince(DateTime.UtcNow.AddDays(-7)).Delta;
            store.Setting("lastDigestUtc", DateTime.UtcNow.ToString("O"));
            if (week > 0 && balloonTitle == null) { balloonTitle = "Your ZipXP weekly summary"; balloonText = $"ZipXP saved {Display.Bytes(week)} this week."; }
        }
        return () =>
        {
            UpdateTrayTip(total);
            if (balloonTitle != null) tray?.ShowBalloonTip(7000, balloonTitle, balloonText ?? "", Forms.ToolTipIcon.Info);
        };
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
        tray?.ShowBalloonTip(7000, "ZipXP is still running", "It keeps saving space in the background. To stop it, right-click this icon and choose Pause or Quit ZipXP.", Forms.ToolTipIcon.Info);
    }

    public async Task ExitAsync()
    {
        if (Exiting) return;
        Exiting = true;
        if (MainWindow is MainWindow window) window.ShowClosingStatus();
        if (MainWindow is ReactWindow reactWindow) reactWindow.ShowClosingStatus();
        savingsTimer?.Stop();
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
