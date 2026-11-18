using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ZipXP.App;

internal sealed class ReactWindow : Window
{
    private const string Origin = "https://zipxp.internal";
    private readonly WebView2 web = new();
    private readonly DesktopBridge bridge;
    private bool commandBusy;
    private bool closing;
    private bool initialized;

    public ReactWindow()
    {
        Title = "ZipXP"; Width = 1280; Height = Math.Min(940, SystemParameters.WorkArea.Height * .95);
        MinWidth = 900; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/zipxp.ico"));
        Content = new TextBlock { Text = "Starting ZipXP…", Margin = new Thickness(32) };
        bridge = new(this);
        Loaded += async (_, _) => { if (!initialized) { initialized = true; await InitializeAsync(); } };
        Closing += OnClosing;
        Closed += (_, _) => web.Dispose();
    }

    private static bool Trusted(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "zipxp.internal" && uri.IsDefaultPort
        && uri.AbsolutePath == "/index.html" && string.IsNullOrEmpty(uri.Query);

    private async Task InitializeAsync()
    {
        try
        {
            var ui = Path.Combine(AppContext.BaseDirectory, "UI");
            if (!File.Exists(Path.Combine(ui, "index.html"))) throw new IOException("The React interface is missing. Rebuild ZipXP with Node.js installed, or launch with --native-ui.");
            // Never put the Chromium profile beside the executable (which may be in Program Files).
            var profile = Path.Combine(Path.GetDirectoryName(App.Instance.Engine.Store.DatabasePath)!, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            Content = web;
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("zipxp.internal", ui, CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) => e.Cancel = !Trusted(e.Uri);
            web.CoreWebView2.FrameNavigationStarting += (_, e) => e.Cancel = true;
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) ShowError("The interface couldn't load: " + e.WebErrorStatus);
            };
            web.Source = new Uri(Origin + "/index.html");
        }
        catch (Exception ex)
        {
            if (Environment.GetCommandLineArgs().Contains("--launch-check")) { Console.Error.WriteLine(ex); await App.Instance.ExitAsync(); Environment.ExitCode = 1; }
            else ShowError("Couldn't start the React interface. Install the Microsoft Edge WebView2 Runtime, or launch ZipXP with --native-ui.\n\n" + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        var panel = new StackPanel { Margin = new Thickness(32) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var fallback = new Button { Content = "Open native interface", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8, 16, 8) };
        fallback.Click += (_, _) =>
        {
            var native = new MainWindow(); App.Instance.MainWindow = native; native.Show(); closing = true; Close();
        };
        panel.Children.Add(fallback); Content = panel;
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closing || App.Instance.Exiting || !Trusted(e.Source) || !Trusted(web.CoreWebView2.Source)) return;
        string? id = null;
        bool acquired = false;
        object response;
        try
        {
            if (e.WebMessageAsJson.Length > 65536) throw new ArgumentException("Desktop message is too large.");
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            id = message.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 64) throw new ArgumentException("Invalid request identifier.");
            var command = message.GetProperty("command").GetString() ?? "";
            // App lifecycle and shell settings must stay available during engine work.
            var requiresEngineLock = command is not ("snapshot" or "insights" or "showFile" or "quit" or "startup");
            if (requiresEngineLock)
            {
                if (commandBusy) throw new InvalidOperationException("Another action is still in progress.");
                acquired = commandBusy = true;
            }
            var result = await bridge.ExecuteAsync(command, message.GetProperty("args"));
            response = new { id, result };
        }
        catch (Exception ex) { response = new { id, error = Plain.Message(ex.Message) }; }
        finally { if (acquired) commandBusy = false; }
        if (!closing && !App.Instance.Exiting && Trusted(web.CoreWebView2.Source))
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response, DesktopBridge.Json));
    }

    public void ShowClosingStatus() { closing = true; web.IsEnabled = false; Title = "ZipXP — finishing the current file…"; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing || App.Instance.Exiting) return;
        e.Cancel = true; Hide(); App.Instance.ExplainTray();
    }
}
