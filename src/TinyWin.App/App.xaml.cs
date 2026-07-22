using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using TinyWin.App.Services;

namespace TinyWin.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Must happen on the UI thread: the view models capture its DispatcherQueue.
        AppServices.Initialize();

        _window = new MainWindow();
        AppServices.MainWindow = _window;
        _window.Activate();
    }

    /// <summary>
    /// Records a crash before the process goes down.
    /// </summary>
    /// <remarks>
    /// A tool that mounts images and loads offline hives must not fail silently: if it dies mid-build
    /// the user needs to know what happened, because a stale mount or a loaded hive may be left
    /// behind for them to clean up. Writing the detail to a file is the minimum; the log path is the
    /// first thing to ask for in a bug report.
    /// </remarks>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "TinyWin");
            Directory.CreateDirectory(directory);

            var text = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"TinyWin crash  {DateTimeOffset.Now:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"Message: {e.Message}")
                .AppendLine()
                .AppendLine(e.Exception?.ToString() ?? "(no exception object)")
                .ToString();

            File.AppendAllText(Path.Combine(directory, "crash.log"), text);
        }
        catch (IOException)
        {
            // Nothing useful left to do — the process is going down either way.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
