using System.Runtime.InteropServices;
using Nadfny.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Nadfny;

public sealed class MainForm : Form
{
    private const string Host = "app.nadfny";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
    private RpcHost? _rpc;

    public MainForm()
    {
        Text = "Nadfny";
        MinimumSize = new Size(940, 620);
        Size = new Size(1240, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = IsDarkMode() ? Color.FromArgb(20, 22, 27) : Color.White;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    private async Task InitAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nadfny", "WebView");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);

        _rpc = new RpcHost(Post, PickFolder, SetClipboard);
        core.WebMessageReceived += (_, args) =>
        {
            try { _rpc.Handle(args.TryGetWebMessageAsString()); }
            catch (ArgumentException) { /* non-string message: ignore */ }
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.Navigate($"https://{Host}/index.html");
    }

    private void Post(string json)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Post(json)); return; }
        _web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    private string? PickFolder()
    {
        if (InvokeRequired) return (string?)Invoke(PickFolder);
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder to scan for worktrees and build output", UseDescriptionForTitle = true };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    /// <summary>The clipboard needs the UI (STA) thread.</summary>
    private bool SetClipboard(string text)
    {
        if (InvokeRequired) return (bool)Invoke(() => SetClipboard(text));
        if (string.IsNullOrEmpty(text)) return false;
        Clipboard.SetText(text);
        return true;
    }

    private void ApplyTitleBarTheme()
    {
        var dark = IsDarkMode() ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var round = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
    }

    private static bool IsDarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
