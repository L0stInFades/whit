using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace Whit;

sealed partial class Browser : Window
{
    sealed class Tab(Browser owner, bool isPrivate)
    {
        public Browser Owner = owner;
        public Browser? WebHost;
        public readonly bool Private = isPrivate;
        public readonly TabViewItem Item = new()
        {
            Header = isPrivate ? "隐私标签" : "新标签",
            IconSource = TabIcon(isPrivate),
        };
        public WebView2? Web;
        public Task<CoreWebView2?>? Starting;
        public CoreWebView2? Core;
        public Uri? Url;
        public ulong Navigation;
        public int FaviconVersion;
        public string? FaviconUri;
        public byte[]? IconData;
        public int Zoom;
        public bool ReadingZoom, ZoomChanged;
        public bool Loading, Secure, Closed, Popup;
    }

    readonly Dictionary<TabViewItem, Tab> documents = [];
    readonly App app;
    Store store => app.Store;
    ContentDialog? dialog;
    bool closed, retired;
    int hostedWebViews;

    Tab? ActiveTab => tabs.SelectedItem is TabViewItem item ? documents.GetValueOrDefault(item) : null;

    public Browser(App app, IEnumerable<Store.SessionTab>? session, bool forTearOut)
    {
        this.app = app;
        Title = "whit.";
        Closed += (_, _) => CloseContents();
        ConfigureChrome();
        Tab? selected = null;
        if (session is not null)
            foreach (var entry in session)
            {
                var tab = AddTab(select: false);
                tab.Url = SavedUrl(DocumentUrl(entry.Url));
                SetTabTitle(tab, entry.Title);
                if (entry.Icon is { } png) RestoreIcon(tab, png);
                if (entry.Selected) selected = tab;
            }
        if (tabs.TabItems.Count > 0) tabs.SelectedItem = selected?.Item ?? tabs.TabItems[0];
        else if (!forTearOut) AddTab();
        Refresh(true);
    }

    Tab AddTab(bool isPrivate = false, bool focus = false, bool select = true)
    {
        var tab = new Tab(this, isPrivate);
        if (isPrivate) AutomationProperties.SetHelpText(tab.Item, "隐私标签");
        documents.Add(tab.Item, tab);
        tabs.TabItems.Add(tab.Item);
        if (select) tabs.SelectedItem = tab.Item;
        if (focus) FocusNewTab();
        return tab;
    }

    void FillMoveMenu(MenuFlyoutSubItem move)
    {
        move.Items.Clear();
        if (ActiveTab is not { } tab) return;
        if (documents.Count > 1) AddDestination("移至新窗口", null);
        foreach (var window in app.Windows)
            if (window != this && window.ActiveTab is { } active)
                AddDestination("窗口 · " + active.Item.Header, window);
        move.IsEnabled = move.Items.Count > 0;

        void AddDestination(string name, Browser? destination)
        {
            var command = new MenuFlyoutItem { Text = name };
            command.Click += (_, _) =>
            {
                if (tab.Closed || tab.Owner != this || destination?.closed == true) return;
                var target = destination ?? app.OpenWindow(forTearOut: true);
                MoveTab(tab.Item, target, target.tabs.TabItems.Count);
                target.Activate();
                if (documents.Count == 0) CloseBrowser();
            };
            move.Items.Add(command);
        }
    }

    void CloseTab(Tab tab)
    {
        DisposeTab(tab);
        tabs.TabItems.Remove(tab.Item);
        documents.Remove(tab.Item);
        if (tab.Web is { } view)
        {
            webHost.Children.Remove(view);
        }
        if (documents.Count == 0)
        {
            if (app.Windows.Count > 1 || app.SaveSession(this)) CloseBrowser();
            else AddTab();
            return;
        }
        Refresh(true);
    }

    static void DisposeTab(Tab tab)
    {
        if (tab.Closed) return;
        tab.Closed = true;
        tab.Web?.Close();
        if (tab.WebHost is not { } host) return;
        tab.WebHost = null;
        if (--host.hostedWebViews == 0 && host.retired) host.DispatcherQueue.TryEnqueue(host.Close);
    }

    void CloseContents()
    {
        if (closed) return;
        closed = true;
        foreach (var tab in documents.Values) DisposeTab(tab);
        documents.Clear();
    }

    void CloseBrowser()
    {
        if (!KeepWebHost()) Close();
    }

    internal bool KeepWebHost()
    {
        // WinUI moves the composition visual, but WebView2 still owns a child of its original HWND.
        if (closed) return retired && hostedWebViews > 0;
        if (hostedWebViews <= documents.Values.Count(tab => tab.WebHost == this)) return false;
        retired = true;
        AppWindow.Hide();
        CloseContents();
        webHost.Children.Clear();
        tabs.TabItems.Clear();
        root.Children.Clear();
        app.ForgetWindow(this);
        return true;
    }

    void MoveTab(TabViewItem item, Browser target, int index)
    {
        if (target == this || !documents.Remove(item, out var tab)) return;
        if (tab.Web is { } view) webHost.Children.Remove(view);
        tabs.TabItems.Remove(item);
        tab.Owner = target;
        target.documents.Add(item, tab);
        if (tab.Web is { } content) target.webHost.Children.Add(content);
        target.tabs.TabItems.Insert(Math.Clamp(index, 0, target.tabs.TabItems.Count), item);
        target.tabs.SelectedItem = item;
        target.Refresh(true);
    }

    public IEnumerable<Store.SessionTab> Session(int window)
    {
        foreach (var item in tabs.TabItems.OfType<TabViewItem>())
        {
            var tab = documents[item];
            var url = SavedUrl(tab.Url);
            if (tab.Private || (tab.Url is not null && url is null)) continue;
            yield return new(window, url?.AbsoluteUri ?? "", url is null ? "" : (string)item.Header,
                tab == ActiveTab, url is null ? null : tab.IconData);
        }
    }

    public void Open(Uri url)
    {
        var tab = ActiveTab is { Private: false, Url: null, Starting: null } blank ? blank : AddTab();
        _ = Navigate(tab, url);
    }

    void LoadSelected()
    {
        if (root.IsLoaded && ActiveTab is { Url: { } url, Starting: null, Closed: false } tab)
            _ = Navigate(tab, url);
    }

    void Switch(int offset)
    {
        var count = tabs.TabItems.Count;
        if (count < 2) return;
        tabs.SelectedIndex = (tabs.SelectedIndex + offset + count) % count;
    }

    static async Task<CoreWebView2?> Ready(Tab tab)
    {
        if (tab.Closed) return null;
        var web = await (tab.Starting ??= Initialize(tab));
        if (web is null) tab.Starting = null;
        return tab.Closed ? null : web;
    }

    static async Task<CoreWebView2?> Initialize(Tab tab)
    {
        try
        {
            var env = await tab.Owner.app.WebEnvironment();
            if (tab.Closed) return null;
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = tab.Private;
            if (tab.Web is null)
            {
                tab.Web = new()
                {
                    Visibility = tab.Owner.ActiveTab == tab ? Visibility.Visible : Visibility.Collapsed,
                    DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
                };
                tab.Owner.webHost.Children.Add(tab.Web);
                tab.WebHost = tab.Owner;
                tab.WebHost.hostedWebViews++;
            }
            await tab.Web.EnsureCoreWebView2Async(env, options);
            if (tab.Closed) return null;
            var web = tab.Core = tab.Web.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.IsReputationCheckingRequired = true;
            web.Profile.PreferredTrackingPreventionLevel = tab.Owner.TrackingLevel;
            web.LaunchingExternalUriScheme += (_, e) =>
                e.Cancel = !e.IsUserInitiated || ExternalUrl(e.Uri) is null;
            web.PermissionRequested += (_, e) => { if (tab.Private) e.SavesInProfile = false; };
            web.NavigationStarting += (_, e) =>
            {
                var uri = DocumentUrl(e.Uri);
                if (uri is null)
                {
                    e.Cancel = ExternalUrl(e.Uri) is null;
                    return;
                }
                if (!tab.Owner.Allowed(tab, uri)) { e.Cancel = true; return; }
                tab.Web.DefaultBackgroundColor = Microsoft.UI.Colors.White;
                tab.Loading = true;
                tab.Navigation = e.NavigationId;
                if (tab.Owner.ActiveTab == tab) tab.Owner.notice.IsOpen = false;
                tab.Owner.Refresh();
            };
            web.SourceChanged += (_, e) =>
            {
                tab.Url = DocumentUrl(web.Source);
                if (e.IsNewDocument) tab.Secure = false;
                if (tab.Owner.ActiveTab == tab) tab.Owner.Refresh(true);
            };
            web.HistoryChanged += (_, _) => tab.Owner.Refresh();
            web.DocumentTitleChanged += (_, _) => SetTabTitle(tab);
            web.FaviconChanged += async (_, _) => await SetFavicon(tab, web);
            web.NavigationCompleted += (sender, e) =>
            {
                if (tab.Closed || e.NavigationId != tab.Navigation) return;
                tab.Loading = false;
                if (e.IsSuccess) tab.Secure = tab.Url?.Scheme == "https";
                SetTabTitle(tab);
                var owner = tab.Owner;
                if (e.IsSuccess && !owner.app.Clearing && !tab.Private && SavedUrl(tab.Url) is { } url)
                    owner.Save(() => owner.store.Visit(url.AbsoluteUri, PageTitle(tab)));
                owner.Refresh(owner.ActiveTab == tab && !e.IsSuccess);
                _ = SetFavicon(tab, web);
                _ = ReadZoom(tab, web);
            };
            web.NewWindowRequested += async (_, e) =>
            {
                e.Handled = true;
                var uri = DocumentUrl(e.Uri.Length == 0 ? "about:blank" : e.Uri);
                if (uri is null)
                {
                    if (e.IsUserInitiated && ExternalUrl(e.Uri) is { } external)
                        tab.Owner.DispatcherQueue.TryEnqueue(() => { if (!tab.Closed) web.Navigate(external.AbsoluteUri); });
                    return;
                }
                if (!tab.Owner.Allowed(tab, uri)) return;
                var deferral = e.GetDeferral();
                Tab? popup = null;
                try
                {
                    popup = tab.Owner.AddTab(tab.Private, select: tab.Owner.ActiveTab == tab);
                    popup.Popup = true;
                    if (await Ready(popup) is not { } targetWeb)
                    {
                        if (!popup.Closed) popup.Owner.CloseTab(popup);
                        return;
                    }
                    if (uri.Scheme == "about") popup.Url = uri;
                    e.NewWindow = targetWeb;
                    popup.Owner.Refresh(popup.Owner.ActiveTab == popup);
                }
                catch (Exception)
                {
                    if (popup is { Closed: false }) popup.Owner.CloseTab(popup);
                    tab.Owner.Note("新标签无法打开");
                }
                finally { deferral.Complete(); }
            };
            web.WindowCloseRequested += (_, _) =>
            {
                if (tab.Popup && !tab.Closed) tab.Owner.CloseTab(tab);
            };
            await TrackZoom(tab, web);
            tab.Owner.Refresh();
            return web;
        }
        catch (Exception)
        {
            if (!tab.Closed) tab.Owner.Note("WebView2 无法启动");
            return null;
        }
    }

    async Task Navigate(Tab tab, Uri url)
    {
        if (!Allowed(tab, url)) return;
        try
        {
            if (await Ready(tab) is not { } web) return;
            if (tab.Owner.ActiveTab == tab)
            {
                tab.Owner.address.Text = url.AbsoluteUri;
                tab.Web?.Focus(FocusState.Programmatic);
            }
            web.Navigate(url.AbsoluteUri);
        }
        catch (Exception) { if (!tab.Closed) tab.Owner.Note("无法打开地址"); }
    }

    bool Allowed(Tab tab, Uri uri)
    {
        if (!app.HttpsOnly || uri.Scheme != "http") return true;
        if (ActiveTab == tab) Note("已阻止 HTTP 导航");
        return false;
    }

    static string PageTitle(Tab tab)
    {
        var title = tab.Core?.DocumentTitle?.Trim();
        if (!string.IsNullOrEmpty(title)) return title[..Math.Min(title.Length, 256)];
        return tab.Url is { IsFile: true } file ? Path.GetFileName(file.LocalPath) : tab.Url?.Host ?? "";
    }

    static void SetTabTitle(Tab tab, string? title = null)
    {
        if (tab.Closed) return;
        title = string.IsNullOrEmpty(title) ? PageTitle(tab) : title;
        tab.Item.Header = title.Length == 0 ? tab.Private ? "隐私标签" : "新标签" : title;
        ToolTipService.SetToolTip(tab.Item, tab.Private ? "隐私 · " + title : title);
    }

    static FontIconSource TabIcon(bool isPrivate) => new() { Glyph = isPrivate ? Shield : "\uE774", FontSize = 14 };

    static void RestoreIcon(Tab tab, byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png).AsRandomAccessStream();
            var image = new BitmapImage();
            image.SetSource(stream);
            tab.Item.IconSource = new ImageIconSource { ImageSource = image };
            tab.IconData = png;
        }
        catch (Exception) { tab.Item.IconSource = TabIcon(tab.Private); }
    }

    static async Task SetFavicon(Tab tab, CoreWebView2 web)
    {
        if (tab.Closed) return;
        var uri = web.FaviconUri;
        if (uri == tab.FaviconUri) return;
        tab.FaviconUri = uri;
        var version = ++tab.FaviconVersion;
        tab.IconData = null;
        if (string.IsNullOrEmpty(uri))
        {
            tab.Item.IconSource = TabIcon(tab.Private);
            return;
        }
        try
        {
            using var stream = await web.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (tab.Closed || version != tab.FaviconVersion) return;
            byte[]? png = null;
            if (!tab.Private && stream.Size is > 0 and <= Store.MaxIconBytes)
            {
                png = new byte[(int)stream.Size];
                await stream.AsStreamForRead().ReadExactlyAsync(png);
                stream.Seek(0);
            }
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (!tab.Closed && version == tab.FaviconVersion)
            {
                tab.IconData = png;
                tab.Item.IconSource = new ImageIconSource { ImageSource = image };
            }
        }
        catch (Exception)
        {
            if (!tab.Closed && version == tab.FaviconVersion)
            {
                tab.FaviconUri = null;
                tab.Item.IconSource = TabIcon(tab.Private);
            }
        }
    }

    static async Task TrackZoom(Tab tab, CoreWebView2 web)
    {
        try
        {
            // WinUI's WebView2 exposes the page, but not its controller's ZoomFactorChanged event.
            web.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled").DevToolsProtocolEventReceived +=
                async (_, e) =>
                {
                    using var signal = JsonDocument.Parse(e.ParameterObjectAsJson);
                    if (signal.RootElement.GetProperty("name").GetString() == "whitZoomChanged") await ReadZoom(tab, web);
                };
            await web.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await web.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await web.CallDevToolsProtocolMethodAsync("Runtime.addBinding",
                """{"name":"whitZoomChanged","executionContextName":"whit.zoom"}""");
            await web.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", """
                {"worldName":"whit.zoom","runImmediately":true,
                 "source":"if (self === top) { let last; const report = () => { const scale = devicePixelRatio * visualViewport.scale; if (last !== scale) { last = scale; whitZoomChanged(''); } }; visualViewport.addEventListener('resize', report); window.addEventListener('resize', report); report(); }"}
                """);
            await ReadZoom(tab, web);
        }
        catch (Exception) { /* The tab can close while the page domain is starting. */ }
    }

    static async Task ReadZoom(Tab tab, CoreWebView2 web)
    {
        if (tab.Closed) return;
        tab.ZoomChanged = true;
        if (tab.ReadingZoom) return;
        tab.ReadingZoom = true;
        try
        {
            while (tab.ZoomChanged && !tab.Closed)
            {
                tab.ZoomChanged = false;
                using var metrics = JsonDocument.Parse(await web.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
                var viewport = metrics.RootElement.GetProperty("cssVisualViewport");
                tab.Zoom = (int)Math.Round(100 * viewport.GetProperty("zoom").GetDouble() * viewport.GetProperty("scale").GetDouble());
                if (!tab.Closed && tab.Owner.ActiveTab == tab) tab.Owner.ShowZoom(tab);
            }
        }
        catch (Exception) { /* Navigation can replace the renderer between the event and the response. */ }
        finally { tab.ReadingZoom = false; }
    }

    void ShowZoom(Tab? tab)
    {
        if (tab is not { Url: not null, Zoom: > 0 }) { zoom.Visibility = Visibility.Collapsed; return; }
        var percent = $"{tab.Zoom}%";
        if (zoom.Text != percent)
        {
            zoom.Text = percent;
            AutomationProperties.SetName(zoom, "页面缩放 " + percent);
        }
        zoom.Visibility = Visibility.Visible;
    }

    void Reload()
    {
        if (ActiveTab?.Core is not { } web) return;
        if (ActiveTab.Loading) web.Stop();
        else if (ActiveTab.Url is { } uri && Allowed(ActiveTab, uri)) web.Reload();
    }

    internal static Uri? WebUrl(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;

    static Uri? DocumentUrl(string input) => WebUrl(input) ??
        (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Scheme is "file" or "blob" || uri.AbsoluteUri == "about:blank") ? uri : null);

    static Uri? SavedUrl(Uri? url) => url?.Scheme is "http" or "https" or "file" ? url : null;

    static Uri? ExternalUrl(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is not
            ("http" or "https" or "file" or "blob" or "about" or "data" or "javascript" or
             "chrome" or "edge" or "chrome-extension" or "devtools" or "view-source" or "filesystem") ? uri : null;

    internal static readonly (string Name, string SearchUrl)[] SearchEngines =
    [
        ("Google", "https://www.google.com/search?q="),
        ("Bing", "https://www.bing.com/search?q="),
        ("Baidu", "https://www.baidu.com/s?wd="),
        ("DuckDuckGo", "https://duckduckgo.com/?q="),
    ];

    Uri Address(string input)
    {
        if (DocumentUrl(input) is { } explicitUrl) return explicitUrl;
        if (input[0] != '[' && Uri.CheckHostName(input) == UriHostNameType.IPv6) input = $"[{input}]";
        if (WebUrl("http://" + input) is { } bare)
        {
            if (bare.HostNameType == UriHostNameType.IPv6 ||
                bare.HostNameType == UriHostNameType.IPv4 && input.StartsWith(bare.Host, StringComparison.Ordinal))
                return bare;
            if (bare.HostNameType == UriHostNameType.Dns && (bare.IsLoopback || bare.Host.Contains('.')))
                return bare.IsLoopback ? bare : new("https://" + input);
        }
        return new(SearchEngines[app.SearchEngine].SearchUrl + Uri.EscapeDataString(input));
    }

    void Note(string message)
    {
        if (closed) return;
        notice.Message = message;
        notice.IsOpen = true;
    }

    public bool Save(Action action)
    {
        if (closed) return false;
        try { action(); return true; }
        catch (IOException) { Note("无法保存浏览数据"); return false; }
    }
}
