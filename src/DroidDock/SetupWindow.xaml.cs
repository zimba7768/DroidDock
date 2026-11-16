using System.ComponentModel;
using System.IO;
using System.Windows;
using DroidDock.Services;

namespace DroidDock;

public partial class SetupWindow : Window
{
    private sealed record ImageChoice(RemotePackage? Package, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly SdkPaths _sdk;
    private SdkRepository? _repo;
    private RemotePackage? _emulator;
    private RemotePackage? _platformTools;
    private CancellationTokenSource? _cts;

    public SetupWindow(SdkPaths sdk)
    {
        InitializeComponent();
        _sdk = sdk;
        LocationText.Text = $"Install location: {sdk.Root}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _repo = await SdkRepository.FetchAsync(App.Http);
        }
        catch (Exception ex)
        {
            LicenseBox.Text = "Could not load the package list from Google. Check your internet connection and reopen this window.\n\n" + ex.Message;
            return;
        }

        _emulator = _repo.Latest("emulator");
        _platformTools = _repo.Latest("platform-tools");

        EmulatorCheck.Content = Describe("Android Emulator", _emulator, _sdk.HasEmulator);
        EmulatorCheck.IsChecked = _emulator != null && !_sdk.HasEmulator;
        EmulatorCheck.IsEnabled = _emulator != null;

        PlatformToolsCheck.Content = Describe("Platform tools (adb)", _platformTools, _sdk.HasAdb);
        PlatformToolsCheck.IsChecked = _platformTools != null && !_sdk.HasAdb;
        PlatformToolsCheck.IsEnabled = _platformTools != null;

        var installed = _sdk.InstalledImages();
        var choices = new List<ImageChoice>
        {
            new(null, installed.Count > 0
                ? $"Don't download another image ({installed.Count} already installed)"
                : "Don't download an image"),
        };
        foreach (var image in _repo.SystemImages())
        {
            var present = File.Exists(Path.Combine(SdkRepository.InstallDir(_sdk.Root, image.Path), "system.img"));
            choices.Add(new ImageChoice(image, image.ImageDisplayName + (present ? " — installed" : "")));
        }
        ImageCombo.ItemsSource = choices;
        ImageCombo.SelectedIndex = installed.Count == 0 && choices.Count > 1 ? 1 : 0;

        var licenseIds = _repo.Packages.Select(p => p.LicenseRef).Where(id => id == "android-sdk-license").Distinct();
        LicenseBox.Text = string.Join("\n\n", licenseIds.Select(id => _repo.Licenses.GetValueOrDefault(id) ?? ""));
        if (LicenseBox.Text.Length == 0)
            LicenseBox.Text = "See https://developer.android.com/studio/terms for the Android SDK License Agreement.";

        ComponentsPanel.IsEnabled = true;
        AcceptCheck.IsEnabled = true;
    }

    private static string Describe(string name, RemotePackage? pkg, bool installed) =>
        pkg == null
            ? $"{name} — not available for this PC"
            : $"{name} {pkg.Revision} · {pkg.SizeText}" + (installed ? " — already installed (tick to reinstall)" : "");

    private void Accept_Click(object sender, RoutedEventArgs e) =>
        InstallButton.IsEnabled = AcceptCheck.IsChecked == true && _cts == null;

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var packages = new List<RemotePackage>();
        if (EmulatorCheck.IsChecked == true && _emulator != null) packages.Add(_emulator);
        if (PlatformToolsCheck.IsChecked == true && _platformTools != null) packages.Add(_platformTools);
        if (ImageCombo.SelectedItem is ImageChoice { Package: not null } choice) packages.Add(choice.Package);

        if (packages.Count == 0)
        {
            StatusText.Text = "Nothing selected to download.";
            return;
        }
        if (packages.Any(p => p.LicenseRef != "android-sdk-license"))
        {
            StatusText.Text = "One of the selected packages uses a different license and can't be installed from here.";
            return;
        }

        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        ComponentsPanel.IsEnabled = false;
        AcceptCheck.IsEnabled = false;
        CloseButton.Content = "Cancel";

        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) Progress.Value = (double)p.Done / p.Total;
            StatusText.Text = p.Total > 0
                ? $"{p.Stage}  {p.Done / 1048576.0:0} / {p.Total / 1048576.0:0} MB"
                : p.Stage;
        });

        try
        {
            Directory.CreateDirectory(_sdk.Root);
            foreach (var pkg in packages)
                await SdkRepository.InstallAsync(App.Http, pkg, _sdk.Root, progress, _cts.Token);

            _cts = null;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Finish("Cancelled.");
        }
        catch (Exception ex)
        {
            Finish("Install failed: " + ex.Message);
        }
    }

    private void Finish(string message)
    {
        _cts = null;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        StatusText.Text = message;
        ComponentsPanel.IsEnabled = true;
        AcceptCheck.IsEnabled = true;
        InstallButton.IsEnabled = AcceptCheck.IsChecked == true;
        CloseButton.Content = "Close";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) _cts.Cancel();
        else Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cts == null) return;
        _cts.Cancel();
        e.Cancel = true;
    }
}
