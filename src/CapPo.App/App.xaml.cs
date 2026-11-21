using CapPo.Core.Detection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CapPo.App;

public partial class App : Application
{
    private readonly LaunchOptions _options;
    private readonly Engine _engine;
    private readonly DetectionResult? _first;

    internal App(LaunchOptions options, Engine engine, DetectionResult? first)
    {
        _options = options;
        _engine = engine;
        _first = first;
        InitializeComponent();
        // Windows come and go; CapPo lives on in the notification area until it's told to exit.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => Log.Error(e.Exception, "Unhandled");
    }

    internal static AppController Controller { get; private set; } = null!;

    internal static DispatcherQueue Dispatcher { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        Controller = new AppController(_engine);
        Controller.Start(_options, _first);
    }
}
