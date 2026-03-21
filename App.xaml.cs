using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace RAM;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrash("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception("Unknown AppDomain exception");
        ShowCrash("AppDomain.CurrentDomain.UnhandledException", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ShowCrash("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void ShowCrash(string source, Exception ex)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine("RAM startup/runtime crash")
                .AppendLine()
                .AppendLine($"Source: {source}")
                .AppendLine()
                .AppendLine(ex.ToString())
                .ToString();

            MessageBox.Show(text, "RAM Crash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // If even MessageBox fails, humanity wins another round.
        }
    }
}