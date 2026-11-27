using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Whit;

public sealed partial class App : Application
{
    readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    readonly List<Browser> windows = [];
    readonly ConcurrentQueue<Uri?> pending;
    readonly Uri? initialUrl;
    readonly string dataPath = ApplicationData.Current.LocalFolder.Path;
    Task<CoreWebView2Environment>? environment;
    Browser? active;
    internal Browser? TearOutWindow;
    bool quitting;

    internal Store Store { get; private set; } = null!;
    internal IReadOnlyList<Browser> Windows => windows;
    internal bool HttpsOnly, Strict, Clearing;
    internal int SearchEngine;

    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey("whit");
        if (!instance.IsCurrent)
        {
            Task.Run(async () => await instance.RedirectActivationToAsync(activation)).GetAwaiter().GetResult();
            return;
        }

        var pending = new ConcurrentQueue<Uri?>();
        App? app = null;
        instance.Activated += (_, e) =>
        {
            pending.Enqueue(ActivationUrl(e));
            if (Volatile.Read(ref app) is { } running) running.dispatcher.TryEnqueue(running.OpenPending);
        };
        var url = args.Select(Browser.WebUrl).FirstOrDefault(uri => uri is not null) ?? ActivationUrl(activation);
        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            Volatile.Write(ref app, new App(url, pending));
        });
    }

    App(Uri? url, ConcurrentQueue<Uri?> pending)
    {
        initialUrl = url;
        this.pending = pending;
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Store = new(Path.Combine(dataPath, "whit.db"));
        HttpsOnly = Store.Setting("https") != 0;
        Strict = Store.Setting("strict") != 0;
        var engine = Store.Setting("search");
        if (engine >= 0 && engine < Browser.SearchEngines.Length) SearchEngine = (int)engine;
        foreach (var session in Store.Session().GroupBy(tab => tab.Window)) OpenWindow(session);
        if (windows.Count == 0) OpenWindow();
        if (initialUrl is not null) windows[^1].Open(initialUrl);
        OpenPending();
    }

    internal Browser OpenWindow(IEnumerable<Store.SessionTab>? session = null, bool forTearOut = false)
    {
        var window = new Browser(this, session, forTearOut);
        windows.Add(window);
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) active = window;
        };
        window.AppWindow.Closing += (_, e) =>
        {
            if (!quitting && window.KeepWebHost()) { e.Cancel = true; return; }
            if (!quitting && windows.Count == 1 && windows[0] == window) e.Cancel = !SaveSession(window);
        };
        window.Closed += (_, _) => ForgetWindow(window);
        if (!forTearOut) window.Activate();
        return window;
    }

    internal void ForgetWindow(Browser window)
    {
        if (!windows.Remove(window)) return;
        if (active == window) active = null;
        if (TearOutWindow == window) TearOutWindow = null;
        if (windows.Count == 0) Store.Dispose();
    }

    internal void Quit(Browser window)
    {
        if (!SaveSession(window)) return;
        quitting = true;
        foreach (var open in windows.ToArray()) open.Close();
    }

    internal bool SaveSession(Browser window) =>
        window.Save(() => Store.SetSession(windows.SelectMany((open, index) => open.Session(index))));

    internal Task<CoreWebView2Environment> WebEnvironment()
    {
        if (environment is null || environment.IsFaulted)
            environment = CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(dataPath, "WebView2"),
                new CoreWebView2EnvironmentOptions
                {
                    EnableTrackingPrevention = true, AdditionalBrowserArguments = "--disk-cache-size=16777216",
                }).AsTask();
        return environment;
    }

    void OpenPending()
    {
        if (windows.Count == 0 || quitting) return;
        while (pending.TryDequeue(out var url))
        {
            var window = active ?? windows[^1];
            window.Activate();
            if (url is not null) window.Open(url);
        }
    }

    static Uri? ActivationUrl(AppActivationArguments args)
    {
        if (args.Data is IProtocolActivatedEventArgs protocol) return Browser.WebUrl(protocol.Uri.AbsoluteUri);
        if (args.Data is not ILaunchActivatedEventArgs { Arguments.Length: > 0 } launch) return null;
        var argv = CommandLineToArgvW(launch.Arguments, out var count);
        if (argv == 0) return null;
        try
        {
            for (var i = 0; i < count; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) is { } value &&
                    Browser.WebUrl(value) is { } url) return url;
            return null;
        }
        finally { LocalFree(argv); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string commandLine, out int count);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
