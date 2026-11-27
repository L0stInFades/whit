using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Whit;

sealed partial class Browser
{
    CoreWebView2TrackingPreventionLevel TrackingLevel => strict
        ? CoreWebView2TrackingPreventionLevel.Strict : CoreWebView2TrackingPreventionLevel.Balanced;

    MenuFlyout Menu()
    {
        var menu = new MenuFlyout();
        Add("隐私标签", Shield, () => { AddTab(isPrivate: true, focus: true); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("书签", "\uE734", () => Library(true));
        Add("历史", "\uE81C", () => Library(false));
        Add("下载", "\uE896", Downloads);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("隐私与安全", "\uE72E", Privacy);
        return menu;

        void Add(string label, string icon, Func<Task> run)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Icon(icon) };
            item.Click += async (_, _) => await run();
            menu.Items.Add(item);
        }
    }

    void RefreshBookmark()
    {
        star.IsEnabled = ActiveTab is { Private: false, Url: not null };
        var saved = false;
        if (star.IsEnabled) Save(() => saved = store.Bookmarked(ActiveTab!.Url!.AbsoluteUri));
        Glyph(star, saved ? "\uE735" : "\uE734", saved ? "移除书签" : "添加书签");
    }

    void ToggleBookmark()
    {
        if (ActiveTab is not { Private: false, Url: { } uri } tab) return;
        var url = uri.AbsoluteUri;
        Save(() =>
        {
            if (store.Bookmarked(url)) store.Unbookmark(url);
            else store.Bookmark(url, PageTitle(tab));
        });
        RefreshBookmark();
    }

    async Task Library(bool bookmarks)
    {
        if (dialog is not null || ActiveTab is not { } tab) return;
        var search = new AutoSuggestBox { PlaceholderText = "搜索", QueryIcon = Icon("\uE721"), MaxSuggestionListHeight = 0 };
        AutomationProperties.SetName(search, bookmarks ? "搜索书签" : "搜索历史");
        var rows = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer
        {
            Content = rows, MaxHeight = Math.Max(104, Math.Min(root.ActualHeight - 240, 440)),
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        var previous = Button("\uE72B", "上一页");
        var next = Button("\uE72A", "下一页");
        var paging = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        paging.Children.Add(previous);
        paging.Children.Add(next);
        var body = new StackPanel { Spacing = 8, Width = Math.Max(250, Math.Min(root.ActualWidth - 120, 432)) };
        body.Children.Add(search);
        body.Children.Add(scroll);
        body.Children.Add(paging);

        var panel = new ContentDialog { Title = bookmarks ? "书签" : "历史", Content = body, CloseButtonText = "关闭" };
        var cursors = new Stack<long>();
        var cursor = long.MaxValue;
        var nextCursor = 0L;
        var query = "";
        Uri? selected = null;

        search.QuerySubmitted += (_, e) =>
        {
            query = e.QueryText.Trim();
            cursors.Clear();
            cursor = long.MaxValue;
            Fill();
        };
        previous.Click += (_, _) => { cursor = cursors.Pop(); Fill(); };
        next.Click += (_, _) => { cursors.Push(cursor); cursor = nextCursor; Fill(); };
        Fill();
        await Show(panel);
        if (selected is not null && !tab.Closed) await Navigate(tab, selected);

        void Fill()
        {
            rows.Children.Clear();
            List<Store.Page> pages = [];
            Save(() => pages = store.Browse(bookmarks, cursor, query));
            if (pages.Count == 0) rows.Children.Add(new TextBlock { Text = "无记录", Margin = new(6, 12, 6, 12) });
            previous.IsEnabled = cursors.Count != 0;
            next.IsEnabled = pages.Count > 40;
            paging.Visibility = previous.IsEnabled || next.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (next.IsEnabled) nextCursor = pages[39].Id;
            foreach (var page in pages.Take(40))
            {
                var url = WebUrl(page.Url);
                if (url is null) continue;
                var label = page.Title.Length == 0 ? url.Host : page.Title;
                var details = bookmarks ? url.IdnHost :
                    $"{url.IdnHost}   {DateTimeOffset.FromUnixTimeSeconds(page.Stamp).ToLocalTime():g}";
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock
                {
                    Text = details, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Opacity = .68,
                });
                var open = new Button
                {
                    Content = text, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new(0),
                    Background = new SolidColorBrush(), Padding = new(8, 4, 8, 4),
                };
                AutomationProperties.SetName(open, label);
                ToolTipService.SetToolTip(open, page.Url);
                open.Click += (_, _) => { selected = url; panel.Hide(); };
                var remove = Button("\uE74D", (bookmarks ? "移除书签 " : "删除记录 ") + label, () =>
                {
                    if (Save(() => { if (bookmarks) store.Unbookmark(page.Url); else store.Forget(page.Id); }))
                    {
                        RefreshBookmark();
                        Fill();
                    }
                });
                remove.VerticalAlignment = VerticalAlignment.Center;
                var row = new Grid();
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                Place(row, open, 0);
                Place(row, remove, 1);
                rows.Children.Add(row);
            }
            scroll.ChangeView(null, 0, null, true);
        }
    }

    async Task Downloads()
    {
        if (ActiveTab is not { } tab) return;
        try
        {
            if (await Ready(tab) is { } web && ActiveTab == tab) web.OpenDefaultDownloadDialog();
        }
        catch (Exception) { Note("下载面板无法打开"); }
    }

    async void ShowSite()
    {
        if (ActiveTab is not { Core: { } web, Url: { } url } tab) return;
        var origin = url.GetLeftPart(UriPartial.Authority);
        var connection = tab.Secure ? "安全连接" : url.Scheme == "http" ? "HTTP · 未加密" : "安全状态未确认";
        var panel = new ContentDialog
        {
            Title = "站点信息", Content = connection + "\n" + origin,
            PrimaryButtonText = "重置站点权限", CloseButtonText = "关闭",
        };
        if (await Show(panel) != ContentDialogResult.Primary) return;
        try { await ResetPermissions(web.Profile, origin); }
        catch (Exception) { Note("站点权限重置失败"); }
    }

    async Task Privacy()
    {
        if (dialog is not null) return;
        var https = new ToggleSwitch { Header = "仅 HTTPS 导航", IsOn = httpsOnly, OnContent = "开", OffContent = "关" };
        var tracking = new ToggleSwitch { Header = "严格防跟踪", IsOn = strict, OnContent = "开", OffContent = "关" };
        var wipe = new Button { Content = "清除浏览数据" };
        var permissions = new Button { Content = "重置站点权限" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(wipe);
        actions.Children.Add(permissions);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(https);
        body.Children.Add(tracking);
        body.Children.Add(actions);

        var panel = new ContentDialog
        {
            Title = "隐私与安全", Content = body, PrimaryButtonText = "保存", CloseButtonText = "关闭",
        };
        Func<Task>? next = null;
        wipe.Click += (_, _) => { next = ClearBrowsing; panel.Hide(); };
        permissions.Click += (_, _) => { next = ClearPermissions; panel.Hide(); };
        if (await Show(panel) == ContentDialogResult.Primary &&
            Save(() => store.SetPrivacy(https.IsOn, tracking.IsOn)))
        {
            httpsOnly = https.IsOn;
            strict = tracking.IsOn;
            try
            {
                foreach (var tab in documents.Values)
                    if (tab.Core is { } web) web.Profile.PreferredTrackingPreventionLevel = TrackingLevel;
            }
            catch (Exception) { Note("防跟踪设置将在下次打开时生效"); }
        }
        if (next is not null) await next();
    }

    async Task<CoreWebView2Profile?> RegularProfile()
    {
        var tab = documents.Values.FirstOrDefault(item => !item.Private) ?? AddTab();
        return (await Ready(tab))?.Profile;
    }

    async Task ClearBrowsing()
    {
        var prompt = new ContentDialog
        {
            Title = "清除浏览数据？", Content = "历史、下载记录、缓存和站点数据",
            PrimaryButtonText = "清除", CloseButtonText = "取消",
        };
        if (await Show(prompt) != ContentDialogResult.Primary || clearing) return;
        clearing = true;
        try
        {
            if (await RegularProfile() is not { } profile) return;
            await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory |
                CoreWebView2BrowsingDataKinds.DownloadHistory | CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.AllSite);
            if (Save(store.ClearHistory)) Note("浏览数据已清除");
        }
        catch (Exception) { Note("浏览数据清除失败"); }
        finally { clearing = false; }
    }

    async Task ClearPermissions()
    {
        var prompt = new ContentDialog
        {
            Title = "重置站点权限？", PrimaryButtonText = "重置", CloseButtonText = "取消",
        };
        if (await Show(prompt) != ContentDialogResult.Primary) return;
        try
        {
            if (await RegularProfile() is { } profile) await ResetPermissions(profile);
        }
        catch (Exception) { Note("站点权限重置失败"); }
    }

    static async Task ResetPermissions(CoreWebView2Profile profile, string? origin = null)
    {
        foreach (var setting in await profile.GetNonDefaultPermissionSettingsAsync())
            if (origin is null || string.Equals(origin, setting.PermissionOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                await profile.SetPermissionStateAsync(setting.PermissionKind, setting.PermissionOrigin, CoreWebView2PermissionState.Default);
    }

    async Task<ContentDialogResult> Show(ContentDialog panel)
    {
        if (dialog is not null) return ContentDialogResult.None;
        panel.XamlRoot = root.XamlRoot;
        dialog = panel;
        try { return await panel.ShowAsync(); }
        finally { dialog = null; }
    }
}
