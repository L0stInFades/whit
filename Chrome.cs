using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Whit;

sealed partial class Browser
{
    const double IconSize = 16;
    const double ToolbarButtonSize = 32;
    const double AddressHeight = 36;
    const int DwmBorderColor = 34;
    const string Shield = "\uEA18";
    const string Search = "\uE721";

    readonly Grid root = new() { KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden };
    readonly Grid webHost = new();
    readonly Grid startPage = new() { Visibility = Visibility.Collapsed };
    readonly TabView tabs = new()
    {
        Height = 36,
        Padding = new(4, 4, 0, 0),
        TabWidthMode = TabViewWidthMode.Equal,
        CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.OnPointerOver,
        CanTearOutTabs = true, CanReorderTabs = true,
    };
    readonly AutoSuggestBox address = new()
    {
        MinWidth = 0, FlowDirection = FlowDirection.LeftToRight,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly AutoSuggestBox startSearch = new()
    {
        PlaceholderText = "搜索或输入地址", MaxWidth = 520, FontSize = 16,
    };
    readonly InfoBar notice = new() { IsClosable = true, Severity = InfoBarSeverity.Warning };
    readonly Button back = Button("\uE72B", "后退");
    readonly Button forward = Button("\uE72A", "前进");
    readonly Button reload = Button("\uE72C", "刷新");
    readonly Button site = Button(Search, "搜索");
    readonly Button star = Button("\uE734", "添加书签");
    readonly TextBlock zoom = new()
    {
        Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center, MinWidth = 38, FontSize = 12,
    };
    readonly Border privateLabel = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock wordmark = new()
    {
        Text = "whit.", FontSize = 76, CharacterSpacing = -30,
        FontFamily = new("Segoe UI Variable Display"), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
    };
    readonly StackPanel privateMark = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    readonly SolidColorBrush clear = new(Microsoft.UI.Colors.Transparent);
    readonly SolidColorBrush canvas = new(), ink = new(), muted = new();

    TextBox? AddressEditor => VisualTreeHelper.GetChildrenCount(address) > 0 &&
        VisualTreeHelper.GetChild(address, 0) is Grid template ? template.Children.OfType<TextBox>().FirstOrDefault() : null;

    void ConfigureChrome()
    {
        tabs.Resources["TabViewItemHeaderIconSize"] = 20d;
        SystemBackdrop = DesktopAcrylicController.IsSupported() ? new NeutralAcrylic() : new DesktopAcrylicBackdrop();
        var more = Button("\uE712", "菜单");
        more.Flyout = Menu();
        foreach (var button in new[] { back, forward, reload, site, star, more })
            button.Resources["ButtonBackgroundDisabled"] = clear;
        back.Click += (_, _) => { if (ActiveTab?.Core is { CanGoBack: true } web) web.GoBack(); };
        forward.Click += (_, _) => { if (ActiveTab?.Core is { CanGoForward: true } web) web.GoForward(); };
        reload.Click += (_, _) => Reload();
        site.Click += (_, _) =>
        {
            if (ActiveTab?.Url?.Scheme is "http" or "https") ShowSite();
            else FocusAddress();
        };
        star.Click += (_, _) => ToggleBookmark();

        AutomationProperties.SetName(address, "地址");
        AutomationProperties.SetLiveSetting(zoom, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var addressField = Columns(new(40), new(1, GridUnitType.Star), GridLength.Auto, new(40));
        addressField.Height = AddressHeight;
        addressField.VerticalAlignment = VerticalAlignment.Center;
        Place(addressField, site, 0);
        Place(addressField, address, 1);
        Place(addressField, zoom, 2);
        Place(addressField, star, 3);
        var toolbar = Columns(GridLength.Auto, GridLength.Auto, GridLength.Auto,
            new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
        toolbar.Height = 44;
        toolbar.Padding = new(8, 0, 8, 0);
        Place(toolbar, back, 0);
        Place(toolbar, forward, 1);
        Place(toolbar, reload, 2);
        Place(toolbar, addressField, 3);
        Place(toolbar, privateLabel, 4);
        Place(toolbar, more, 5);
        ConfigurePrivacyLabel();
        ConfigureStart();

        var viewport = new Grid();
        viewport.Children.Add(webHost);
        viewport.Children.Add(startPage);
        foreach (FrameworkElement element in new FrameworkElement[] { tabs, toolbar, notice, viewport })
        {
            var row = root.RowDefinitions.Count;
            root.RowDefinitions.Add(new() { Height = row == 3 ? new(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(element, row);
            root.Children.Add(element);
        }
        Content = root;
        ConfigureTitleBar();

        tabs.AddTabButtonClick += (_, _) => AddTab(focus: true);
        tabs.TabCloseRequested += (_, e) => CloseTab(documents[e.Tab]);
        tabs.TabTearOutWindowRequested += (_, e) =>
        {
            app.TearOutWindow = app.OpenWindow(forTearOut: true);
            e.NewWindowId = app.TearOutWindow.AppWindow.Id;
        };
        tabs.TabTearOutRequested += (_, e) =>
        {
            if (app.TearOutWindow is { } target && e.Items[0] is TabViewItem item)
                MoveTab(item, target, 0);
        };
        tabs.ExternalTornOutTabsDropping += (_, e) =>
            e.AllowDrop = e.Items[0] is TabViewItem item && app.Windows.Any(w => w != this && w.documents.ContainsKey(item));
        tabs.ExternalTornOutTabsDropped += (_, e) =>
        {
            if (e.Items[0] is not TabViewItem item) return;
            var source = app.Windows.First(w => w.documents.ContainsKey(item));
            app.TearOutWindow = source;
            source.MoveTab(item, this, e.DropIndex);
        };
        Tab? shown = null;
        tabs.SelectionChanged += (_, _) =>
        {
            if (shown?.Owner == this && shown.Web is { } previous) previous.Visibility = Visibility.Collapsed;
            shown = ActiveTab;
            if (shown?.Web is { } view) view.Visibility = Visibility.Visible;
            startSearch.Text = "";
            notice.IsOpen = false;
            Refresh(true);
            LoadSelected();
        };
        address.Loaded += (_, _) =>
        {
            if (AddressEditor is { } editor) editor.MaxLength = 8192;
        };
        address.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) Refresh(true); };
        address.TextChanged += (box, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = box.Text.Trim();
            List<string>? matches = null;
            if (query.Length > 0 && ActiveTab is { Private: false })
                Save(() => matches = store.Suggest(query));
            box.ItemsSource = matches;
        };
        address.QuerySubmitted += async (_, e) => await Submit(e.ChosenSuggestion as string ?? e.QueryText);
        startSearch.QuerySubmitted += async (_, e) => await Submit(e.QueryText);
        Shortcut(VirtualKey.L, FocusAddress);
        Shortcut(VirtualKey.T, () => AddTab(focus: true));
        Shortcut(VirtualKey.N, () => app.OpenWindow());
        Shortcut(VirtualKey.W, () => { if (ActiveTab is { } tab) CloseTab(tab); });
        Shortcut(VirtualKey.Tab, () => Switch(1));
        Shortcut(VirtualKey.Tab, () => Switch(-1), VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Shortcut(VirtualKey.N, () => AddTab(isPrivate: true, focus: true), VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Shortcut(VirtualKey.D, ToggleBookmark);
        Shortcut(VirtualKey.H, async () => await Library(false));
        Shortcut(VirtualKey.J, async () => await Downloads());
        Shortcut(VirtualKey.R, Reload);
        Shortcut(VirtualKey.F5, Reload, VirtualKeyModifiers.None);
        Shortcut(VirtualKey.Left, () => { if (back.IsEnabled) ActiveTab?.Core?.GoBack(); }, VirtualKeyModifiers.Menu);
        Shortcut(VirtualKey.Right, () => { if (forward.IsEnabled) ActiveTab?.Core?.GoForward(); }, VirtualKeyModifiers.Menu);
    }

    void ConfigureTitleBar()
    {
        var drag = new Grid { Background = clear, MinWidth = 64, Height = tabs.Height };
        tabs.TabStripFooter = drag;
        ExtendsContentIntoTitleBar = true;

        var window = AppWindow;
        var caption = window.TitleBar;
        caption.PreferredHeightOption = TitleBarHeightOption.Standard;
        caption.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        caption.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        var nonClient = InputNonClientPointerSource.GetForWindowId(window.Id);
        var appearance = ThemeSettings.CreateForWindowId(window.Id);
        var systemColors = new UISettings();
        var dragRect = new RectInt32();
        root.LayoutUpdated += UpdateDragArea;
        appearance.Changed += AppearanceChanged;
        Closed += (_, _) =>
        {
            root.LayoutUpdated -= UpdateDragArea;
            appearance.Changed -= AppearanceChanged;
        };
        root.Loaded += (_, _) =>
        {
            root.XamlRoot.Changed += (_, _) => CaptionInset();
            CaptionInset();
            UpdateTheme();
            DispatcherQueue.TryEnqueue(() =>
            {
                LoadSelected();
                if (ActiveTab is { Url: null }) FocusNewTab();
            });
        };
        root.SizeChanged += (_, _) => CaptionInset();
        root.ActualThemeChanged += (_, _) => UpdateTheme();

        void AppearanceChanged(ThemeSettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateTheme);

        void UpdateTheme()
        {
            if (root.XamlRoot is null) return;
            var dark = root.ActualTheme == ElementTheme.Dark;
            var contrast = appearance.HighContrast;
            canvas.Color = contrast ? systemColors.GetColorValue(UIColorType.Background) : Gray(dark ? 0 : 255);
            ink.Color = contrast ? systemColors.GetColorValue(UIColorType.Foreground) : Gray(dark ? 255 : 0);
            muted.Color = contrast ? ink.Color : Gray(dark ? 174 : 91);
            caption.ButtonForegroundColor = ink.Color;
            caption.ButtonInactiveForegroundColor = muted.Color;
            var border = contrast ? uint.MaxValue : dark ? 0x393939u : 0xD4D4D4u;
            DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), DwmBorderColor, in border, sizeof(uint));
        }

        void UpdateDragArea(object? sender, object args)
        {
            if (drag.XamlRoot is not { } xaml || drag.ActualWidth == 0 ||
                drag.TransformToVisual(root) is not { } transform) return;
            var scale = xaml.RasterizationScale;
            var offset = transform.TransformPoint(new(0, 0));
            var left = (int)Math.Ceiling(offset.X * scale);
            var width = Math.Max(0, window.ClientSize.Width - caption.RightInset - left);
            var bounds = new RectInt32(left, 0, width, (int)Math.Round(tabs.Height * scale));
            if (bounds.Equals(dragRect)) return;
            // TabView maintains a separate caption region for native tab tear-out.
            var regions = nonClient.GetRegionRects(NonClientRegionKind.Caption).Where(rect => !rect.Equals(dragRect)).ToList();
            if (width > 0) regions.Add(bounds);
            dragRect = bounds;
            nonClient.SetRegionRects(NonClientRegionKind.Caption, regions.ToArray());
        }
    }

    void CaptionInset()
    {
        if (root.XamlRoot is not { } xaml) return;
        var caption = AppWindow.TitleBar;
        var scale = xaml.RasterizationScale;
        var inset = new Thickness(caption.LeftInset / scale, 0, caption.RightInset / scale, 0);
        if (!tabs.Margin.Equals(inset)) tabs.Margin = inset;
        var minimum = (int)Math.Ceiling(440 * scale);
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.PreferredMinimumWidth != minimum)
            presenter.PreferredMinimumWidth = minimum;
    }

    void ConfigureStart()
    {
        wordmark.Foreground = ink;
        wordmark.HorizontalAlignment = HorizontalAlignment.Center;
        privateMark.HorizontalAlignment = HorizontalAlignment.Center;
        var symbol = Icon(Shield, 25);
        symbol.Foreground = canvas;
        privateMark.Children.Add(new Border { Width = 48, Height = 48, Background = ink, Child = symbol });
        privateMark.Children.Add(new TextBlock
        {
            Text = "隐私", FontSize = 38, Foreground = ink, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var identity = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 24) };
        identity.Children.Add(wordmark);
        identity.Children.Add(privateMark);
        startSearch.QueryIcon = new SymbolIcon(Symbol.Find);
        startSearch.Margin = new(24, 0, 24, 0);
        AutomationProperties.SetName(startSearch, "新标签搜索");
        AutomationProperties.SetAutomationId(startSearch, "StartSearch");
        var receiver = new Grid { Background = clear, IsHitTestVisible = false };
        Grid.SetRowSpan(receiver, 3);
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        startSearch.Shadow = shadow;
        startSearch.Translation = new Vector3(0, 0, 12);
        startPage.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        startPage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        startPage.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        startPage.Children.Add(receiver);
        startPage.Children.Add(identity);
        Grid.SetRow(startSearch, 1);
        startPage.Children.Add(startSearch);
        startPage.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Height < 250;
            identity.Visibility = e.NewSize.Height < 150 ? Visibility.Collapsed : Visibility.Visible;
            identity.Margin = new(0, 0, 0, compact ? 16 : 24);
            wordmark.FontSize = compact ? 48 : 76;
        };
    }

    void ConfigurePrivacyLabel()
    {
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var shield = Icon(Shield, 12);
        shield.Foreground = canvas;
        badge.Children.Add(shield);
        badge.Children.Add(new TextBlock
        {
            Text = "隐私", FontSize = 12, Foreground = canvas, VerticalAlignment = VerticalAlignment.Center,
        });
        privateLabel.Margin = new(0, 0, 8, 0);
        privateLabel.Padding = new(7, 3, 7, 3);
        privateLabel.Background = ink;
        privateLabel.VerticalAlignment = VerticalAlignment.Center;
        privateLabel.Child = badge;
        AutomationProperties.SetName(privateLabel, "隐私浏览");
    }

    async Task Submit(string query)
    {
        var input = query.Trim();
        if (input.Length > 0 && ActiveTab is { } tab) await Navigate(tab, Address(input));
    }

    void Refresh(bool updateAddress = false)
    {
        if (closed) return;
        var tab = ActiveTab;
        if (updateAddress)
        {
            address.ItemsSource = null;
            address.Text = tab?.Url?.AbsoluteUri ?? "";
        }
        var privateMode = tab?.Private == true;
        privateLabel.Visibility = privateMode ? Visibility.Visible : Visibility.Collapsed;
        privateMark.Visibility = privateMode ? Visibility.Visible : Visibility.Collapsed;
        wordmark.Visibility = privateMode ? Visibility.Collapsed : Visibility.Visible;
        startPage.Visibility = tab is { Url: null, Loading: false } ? Visibility.Visible : Visibility.Collapsed;
        ShowZoom(tab);
        back.IsEnabled = tab?.Core?.CanGoBack == true;
        forward.IsEnabled = tab?.Core?.CanGoForward == true;
        reload.IsEnabled = tab?.Url is not null || tab?.Loading == true;
        Glyph(reload, tab?.Loading == true ? "\uE711" : "\uE72C", tab?.Loading == true ? "停止" : "刷新");
        if (tab?.Url is null) Glyph(site, Search, "搜索");
        else if (tab.Secure) Glyph(site, "\uE72E", "连接安全");
        else Glyph(site, "\uE774", "连接未确认安全");
        if (updateAddress) RefreshBookmark();
    }

    void FocusAddress()
    {
        if (AddressEditor is not { } editor) return;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    void FocusNewTab()
    {
        if (startPage.Visibility == Visibility.Visible) startSearch.Focus(FocusState.Programmatic);
        else FocusAddress();
    }

    void Shortcut(VirtualKey key, Action run, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += (_, e) =>
        {
            if (dialog is not null) return;
            e.Handled = true;
            run();
        };
        root.KeyboardAccelerators.Add(shortcut);
    }

    static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid();
        foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width });
        return grid;
    }

    static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    static FontIcon Icon(string glyph, double size = IconSize) => new()
    {
        Glyph = glyph, FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    static Button Button(string glyph, string label, Action? run = null)
    {
        var button = new Button
        {
            Width = ToolbarButtonSize, Height = ToolbarButtonSize, Padding = new(0), BorderThickness = new(0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new(4), Background = new SolidColorBrush(),
        };
        Glyph(button, glyph, label);
        if (run is not null) button.Click += (_, _) => run();
        return button;
    }

    static void Glyph(Button button, string glyph, string label)
    {
        if (button.Content is FontIcon icon)
        {
            if (icon.Glyph == glyph && AutomationProperties.GetName(button) == label) return;
            icon.Glyph = glyph;
        }
        else button.Content = Icon(glyph);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    static Color Gray(int shade) => Color.FromArgb(255, (byte)shade, (byte)shade, (byte)shade);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, in uint value, int size);

    sealed partial class NeutralAcrylic : SystemBackdrop
    {
        DesktopAcrylicController? controller;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(target, xamlRoot);
            controller = new DesktopAcrylicController { TintOpacity = .35f, LuminosityOpacity = .72f };
            Apply(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
            controller.AddSystemBackdropTarget(target);
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(
            ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) =>
            Apply(GetDefaultSystemBackdropConfiguration(target, xamlRoot));

        void Apply(SystemBackdropConfiguration configuration)
        {
            if (controller is null) return;
            var dark = configuration.Theme == SystemBackdropTheme.Dark;
            var fallback = Gray(dark ? 32 : 250);
            controller.TintColor = Gray(dark ? 26 : 245);
            controller.FallbackColor = configuration.IsHighContrast
                ? configuration.HighContrastBackgroundColor.GetValueOrDefault(fallback) : fallback;
            controller.SetSystemBackdropConfiguration(configuration);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            controller?.Dispose();
            controller = null;
            base.OnTargetDisconnected(target);
        }
    }
}
