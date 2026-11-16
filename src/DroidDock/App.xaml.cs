using System.Net.Http;
using System.Windows;

namespace DroidDock;

public partial class App : Application
{
    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DroidDock/0.1");
        return http;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "DroidDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
