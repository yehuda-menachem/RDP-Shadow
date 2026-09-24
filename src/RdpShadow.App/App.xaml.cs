using System.Windows;

namespace RdpShadow.App;

public partial class App : Application
{
    // Last resort for the async void handlers: an unexpected remote error must not close the app and its open shadow.
    public App() => DispatcherUnhandledException += (_, e) =>
    {
        // Base exception: a XAML failure's own message is only "Provide value on ... threw an exception".
        MessageBox.Show(e.Exception.GetBaseException().Message, "RDP Shadow", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        // Failed while building the main window: there is nothing to keep running, and no window would ever end the process.
        if (MainWindow is not { IsLoaded: true }) Shutdown(1);
    };
}
