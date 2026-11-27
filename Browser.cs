using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace Whit;

public sealed partial class App : Application
{
    readonly AppInstance instance;
    Browser? window;

    [STAThread]
    static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey("whit");
        if (!instance.IsCurrent)
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(async () => await instance.RedirectActivationToAsync(args)).GetAwaiter().GetResult();
            return;
        }

        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(instance);
        });
    }

    App(AppInstance instance)
    {
        this.instance = instance;
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var browser = window = new Browser(ApplicationData.Current.LocalFolder.Path);
        var dispatcher = browser.DispatcherQueue;
        instance.Activated += (_, _) => dispatcher.TryEnqueue(browser.Activate);
        browser.Activate();
    }
}

sealed partial class Browser : Window
{
    sealed class Tab(bool isPrivate)
    {
        public readonly bool Private = isPrivate;
        public readonly TabViewItem Item = new()
        {
            Header = isPrivate ? "隐私标签" : "新标签",
            IconSource = new FontIconSource { Glyph = isPrivate ? Shield : "\uE774", FontSize = 14 },
        };
        public readonly WebView2 Web = new()
        {
            Visibility = Visibility.Collapsed, DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
        };
        public Task<CoreWebView2?>? Starting;
        public CoreWebView2? Core;
        public Uri? Url;
        public ulong Navigation;
        public bool Loading, Secure, Closed;
    }

    readonly Dictionary<TabViewItem, Tab> documents = [];
    readonly string profilePath;
    readonly Store store;
    Task<CoreWebView2Environment>? environment;
    ContentDialog? dialog;
    bool httpsOnly, strict, clearing;

    Tab? ActiveTab => tabs.SelectedItem is TabViewItem item ? documents.GetValueOrDefault(item) : null;

    public Browser(string dataPath)
    {
        Title = "whit.";
        store = new(Path.Combine(dataPath, "whit.db"));
        httpsOnly = store.Setting("https");
        strict = store.Setting("strict");
        profilePath = Path.Combine(dataPath, "WebView2");

        Closed += (_, _) => store.Dispose();
        ConfigureChrome();
        AddTab();
    }

    Tab AddTab(bool isPrivate = false, bool focus = false)
    {
        var tab = new Tab(isPrivate);
        if (isPrivate) AutomationProperties.SetHelpText(tab.Item, "隐私标签");
        documents.Add(tab.Item, tab);
        webHost.Children.Add(tab.Web);
        tabs.TabItems.Add(tab.Item);
        tabs.SelectedItem = tab.Item;
        if (focus) FocusNewTab();
        return tab;
    }

    void CloseTab(Tab tab)
    {
        if (documents.Count == 1)
        {
            Close();
            return;
        }
        tab.Closed = true;
        tabs.TabItems.Remove(tab.Item);
        documents.Remove(tab.Item);
        webHost.Children.Remove(tab.Web);
        tab.Web.Close();
        Refresh(true);
    }

    void Switch(int offset)
    {
        var count = tabs.TabItems.Count;
        if (count < 2) return;
        tabs.SelectedIndex = (tabs.SelectedIndex + offset + count) % count;
    }

    async Task<CoreWebView2?> Ready(Tab tab)
    {
        if (tab.Closed) return null;
        var web = await (tab.Starting ??= Initialize(tab));
        if (web is null) tab.Starting = null;
        return tab.Closed ? null : web;
    }

    async Task<CoreWebView2?> Initialize(Tab tab)
    {
        try
        {
            environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, profilePath,
                new CoreWebView2EnvironmentOptions { EnableTrackingPrevention = true }).AsTask();
            var env = await environment;
            if (tab.Closed) return null;
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = tab.Private;
            await tab.Web.EnsureCoreWebView2Async(env, options);
            if (tab.Closed) return null;
            var web = tab.Core = tab.Web.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.IsReputationCheckingRequired = true;
            web.Profile.PreferredTrackingPreventionLevel = TrackingLevel;
            web.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            web.PermissionRequested += (_, e) => { if (tab.Private) e.SavesInProfile = false; };
            web.NavigationStarting += (_, e) =>
            {
                var uri = WebUrl(e.Uri);
                if ((uri is null && e.Uri != "about:blank") || uri is not null && !Allowed(tab, uri))
                {
                    e.Cancel = true;
                    return;
                }
                tab.Web.DefaultBackgroundColor = uri is null ? Microsoft.UI.Colors.Transparent : Microsoft.UI.Colors.White;
                tab.Loading = true;
                tab.Navigation = e.NavigationId;
                if (ActiveTab == tab) notice.IsOpen = false;
                Refresh();
            };
            web.SourceChanged += (_, e) =>
            {
                tab.Url = WebUrl(web.Source);
                if (e.IsNewDocument) tab.Secure = false;
                if (ActiveTab == tab) Refresh(true);
            };
            web.HistoryChanged += (_, _) => Refresh();
            web.DocumentTitleChanged += (_, _) => SetTabTitle(tab);
            web.NavigationCompleted += (_, e) =>
            {
                if (tab.Closed || e.NavigationId != tab.Navigation) return;
                tab.Loading = false;
                if (e.IsSuccess) tab.Secure = tab.Url?.Scheme == "https";
                SetTabTitle(tab);
                if (e.IsSuccess && !clearing && !tab.Private && tab.Url is { } url)
                    Save(() => store.Visit(url.AbsoluteUri, PageTitle(tab)));
                Refresh(ActiveTab == tab && !e.IsSuccess);
            };
            web.NewWindowRequested += async (_, e) =>
            {
                e.Handled = true;
                if (!e.IsUserInitiated || (e.Uri != "about:blank" && WebUrl(e.Uri) is not { })) return;
                if (WebUrl(e.Uri) is { } target && !Allowed(tab, target)) return;
                var deferral = e.GetDeferral();
                try
                {
                    var popup = AddTab(tab.Private);
                    if (await Ready(popup) is { } targetWeb) e.NewWindow = targetWeb;
                }
                catch (Exception) { Note("新标签无法打开"); }
                finally { deferral.Complete(); }
            };
            Refresh();
            return web;
        }
        catch (Exception)
        {
            if (environment?.IsFaulted == true) environment = null;
            if (!tab.Closed) Note("WebView2 无法启动");
            return null;
        }
    }

    async Task Navigate(Tab tab, Uri url)
    {
        if (!Allowed(tab, url)) return;
        try
        {
            if (await Ready(tab) is not { } web) return;
            if (ActiveTab == tab)
            {
                address.Text = url.AbsoluteUri;
                tab.Web.Focus(FocusState.Programmatic);
            }
            web.Navigate(url.AbsoluteUri);
        }
        catch (Exception) { if (!tab.Closed) Note("无法打开地址"); }
    }

    bool Allowed(Tab tab, Uri uri)
    {
        if (!httpsOnly || uri.Scheme == "https") return true;
        if (ActiveTab == tab) Note("已阻止 HTTP 导航");
        return false;
    }

    static string PageTitle(Tab tab)
    {
        var title = tab.Core?.DocumentTitle?.Trim();
        return string.IsNullOrEmpty(title) ? tab.Url?.Host ?? "" : title[..Math.Min(title.Length, 256)];
    }

    static void SetTabTitle(Tab tab)
    {
        var title = PageTitle(tab);
        tab.Item.Header = title.Length == 0 ? tab.Private ? "隐私标签" : "新标签" : title;
        ToolTipService.SetToolTip(tab.Item, tab.Private ? "隐私 · " + title : title);
    }

    void Reload()
    {
        if (ActiveTab?.Core is not { } web) return;
        if (ActiveTab.Loading) web.Stop();
        else if (ActiveTab.Url is { } uri && Allowed(ActiveTab, uri)) web.Reload();
    }

    static Uri? WebUrl(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;

    static Uri Address(string input)
    {
        if (WebUrl(input) is { } explicitUrl) return explicitUrl;
        if (input[0] != '[' && Uri.CheckHostName(input) == UriHostNameType.IPv6) input = $"[{input}]";
        if (WebUrl("http://" + input) is { } bare)
        {
            if (bare.HostNameType == UriHostNameType.IPv6 ||
                bare.HostNameType == UriHostNameType.IPv4 && input.StartsWith(bare.Host, StringComparison.Ordinal))
                return bare;
            if (bare.HostNameType == UriHostNameType.Dns && (bare.IsLoopback || bare.Host.Contains('.')))
                return bare.IsLoopback ? bare : new("https://" + input);
        }
        return new("https://www.bing.com/search?q=" + Uri.EscapeDataString(input));
    }

    void Note(string message)
    {
        notice.Message = message;
        notice.IsOpen = true;
    }

    bool Save(Action action)
    {
        try { action(); return true; }
        catch (IOException) { Note("无法保存浏览数据"); return false; }
    }
}
