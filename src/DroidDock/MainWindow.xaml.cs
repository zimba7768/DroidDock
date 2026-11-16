using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DroidDock.Models;
using DroidDock.Services;
using Microsoft.Win32;

namespace DroidDock;

public enum DeviceStatus { Stopped, Starting, Booting, Running }

public sealed class DeviceRow(AvdInfo avd) : INotifyPropertyChanged
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x55, 0x5F, 0x6D));

    private DeviceStatus _status;

    public AvdInfo Avd { get; set; } = avd;
    public string Name => Avd.Name;
    public string DisplayName => Avd.DisplayName;
    public string? Serial { get; set; }

    public DeviceStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public string Subtitle => $"{AndroidVersions.Describe(Avd.ApiLevel)} · {Status switch
    {
        DeviceStatus.Starting => "starting…",
        DeviceStatus.Booting => "booting Android…",
        DeviceStatus.Running => "running",
        _ => "stopped",
    }}";

    public Brush StatusBrush => Status switch
    {
        DeviceStatus.Running => Green,
        DeviceStatus.Stopped => Grey,
        _ => Amber,
    };

    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record AppRow(string Package)
{
    public override string ToString() => Package;
}

public partial class MainWindow : Window
{
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(3);

    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly ObservableCollection<AppRow> _apps = [];
    private readonly Dictionary<string, string> _serialToAvd = [];
    private readonly HashSet<string> _bootedSerials = [];
    private readonly Dictionary<string, DateTime> _starting = [];
    private readonly DispatcherTimer _timer;

    private SdkPaths _sdk = SdkPaths.Locate();
    private AdbService _adb;
    private bool _refreshing;
    private string? _appsSerial;

    public MainWindow()
    {
        InitializeComponent();
        _adb = new AdbService(_sdk);
        DeviceList.ItemsSource = _devices;
        AppList.ItemsSource = _apps;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    private DeviceRow? Selected => DeviceList.SelectedItem as DeviceRow;

    // ---------------------------------------------------------------- startup

    private async Task OnLoadedAsync()
    {
        ShowSdk();
        await RefreshAsync();
        _timer.Start();

        if (!_sdk.IsReady)
        {
            Log("The Android Emulator is not installed yet. Opening setup…");
            OpenSetup();
            return;
        }

        var accel = await EmulatorService.AccelCheckAsync(_sdk);
        if (accel.Length > 0) Log("Hardware acceleration: " + accel);
    }

    private void ShowSdk()
    {
        SdkText.Text = _sdk.IsReady
            ? $"Android SDK: {_sdk.Root}"
            : "Android Emulator not installed — click “Android components…” to set it up";
    }

    private void OpenSetup()
    {
        var dialog = new SetupWindow(_sdk) { Owner = this };
        dialog.ShowDialog();
        _sdk = SdkPaths.Locate();
        _adb = new AdbService(_sdk);
        ShowSdk();
    }

    // ---------------------------------------------------------------- state refresh

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            SyncDeviceRows(AvdService.List());

            var running = new Dictionary<string, (string Serial, bool Booted)>();
            if (_sdk.HasAdb)
            {
                var online = (await _adb.DevicesAsync()).Where(d => d.IsEmulator && d.IsOnline).ToList();
                var serials = online.Select(d => d.Serial).ToHashSet();
                foreach (var gone in _serialToAvd.Keys.Where(s => !serials.Contains(s)).ToList())
                {
                    _serialToAvd.Remove(gone);
                    _bootedSerials.Remove(gone);
                }

                foreach (var device in online)
                {
                    if (!_serialToAvd.TryGetValue(device.Serial, out var name))
                    {
                        name = await _adb.EmulatorAvdNameAsync(device.Serial);
                        if (name == null) continue;
                        _serialToAvd[device.Serial] = name;
                    }

                    if (!_bootedSerials.Contains(device.Serial) && await _adb.IsBootedAsync(device.Serial))
                    {
                        _bootedSerials.Add(device.Serial);
                        Log($"{name} is ready.");
                    }
                    running[name] = (device.Serial, _bootedSerials.Contains(device.Serial));
                }
            }

            foreach (var row in _devices)
            {
                if (running.TryGetValue(row.Name, out var r))
                {
                    _starting.Remove(row.Name);
                    row.Serial = r.Serial;
                    row.Status = r.Booted ? DeviceStatus.Running : DeviceStatus.Booting;
                }
                else
                {
                    row.Serial = null;
                    var starting = _starting.TryGetValue(row.Name, out var since) && DateTime.UtcNow - since < StartGrace;
                    if (!starting) _starting.Remove(row.Name);
                    row.Status = starting ? DeviceStatus.Starting : DeviceStatus.Stopped;
                }
            }

            UpdateButtons();
            await SyncAppsAsync(force: false);
        }
        catch (Exception ex)
        {
            Log("Refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SyncDeviceRows(IReadOnlyList<AvdInfo> avds)
    {
        foreach (var stale in _devices.Where(d => avds.All(a => a.Name != d.Name)).ToList())
            _devices.Remove(stale);

        foreach (var avd in avds)
        {
            var row = _devices.FirstOrDefault(d => d.Name == avd.Name);
            if (row == null) _devices.Add(new DeviceRow(avd));
            else row.Avd = avd;
        }

        NoDevicesText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DeviceList.SelectedItem == null && _devices.Count > 0)
            DeviceList.SelectedIndex = 0;
    }

    private void UpdateButtons()
    {
        var row = Selected;
        StartButton.IsEnabled = row is { Status: DeviceStatus.Stopped };
        StopButton.IsEnabled = row is { Serial: not null };
        DeleteButton.IsEnabled = row is { Status: DeviceStatus.Stopped };
        InstallButton.IsEnabled = TargetDevice() != null;
    }

    /// <summary>The device app actions go to: the selected one if running, else the only running one.</summary>
    private DeviceRow? TargetDevice()
    {
        if (Selected is { Status: DeviceStatus.Running }) return Selected;
        var running = _devices.Where(d => d.Status == DeviceStatus.Running).ToList();
        return running.Count == 1 ? running[0] : null;
    }

    // ---------------------------------------------------------------- devices

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateButtons();
        _ = SyncAppsAsync(force: false);
    }

    private void DeviceList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { Status: DeviceStatus.Stopped }) Start_Click(sender, e);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row == null) return;
        if (!_sdk.IsReady)
        {
            OpenSetup();
            if (!_sdk.IsReady) return;
        }

        try
        {
            var name = row.Name;
            EmulatorService.Start(_sdk, name,
                line =>
                {
                    if (line.Contains("ERROR") || line.Contains("FATAL") || line.Contains("PANIC"))
                        Dispatcher.BeginInvoke(() => Log($"[{name}] {line}"));
                },
                code => Dispatcher.BeginInvoke(() =>
                {
                    _starting.Remove(name);
                    Log(code == 0 ? $"{name} stopped." : $"{name}: emulator exited with code {code}.");
                }));

            _starting[name] = DateTime.UtcNow;
            row.Status = DeviceStatus.Starting;
            UpdateButtons();
            Log($"Starting {name}… the Android window will open shortly.");
        }
        catch (Exception ex)
        {
            Log("Could not start the emulator: " + ex.Message);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row?.Serial == null) return;
        Log($"Stopping {row.Name}…");
        var result = await _adb.KillEmulatorAsync(row.Serial);
        if (!result.Ok) Log(result.Combined);
        await RefreshAsync();
    }

    private void NewDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_sdk.InstalledImages().Count == 0)
        {
            Log("No Android system image is installed yet. Opening setup…");
            OpenSetup();
            if (_sdk.InstalledImages().Count == 0) return;
        }

        var dialog = new CreateDeviceWindow(_sdk) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Created != null)
        {
            Log($"Created device {dialog.Created.Name}.");
            SyncDeviceRows(AvdService.List());
            DeviceList.SelectedItem = _devices.FirstOrDefault(d => d.Name == dialog.Created.Name);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row is not { Status: DeviceStatus.Stopped }) return;

        var answer = MessageBox.Show(this,
            $"Delete the virtual device “{row.DisplayName}”?\n\nIts apps and data will be permanently removed.",
            "Delete device", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            AvdService.Delete(row.Avd);
            Log($"Deleted {row.Name}.");
        }
        catch (Exception ex)
        {
            Log("Delete failed: " + ex.Message);
        }
        SyncDeviceRows(AvdService.List());
        UpdateButtons();
    }

    // ---------------------------------------------------------------- apps

    private async Task SyncAppsAsync(bool force)
    {
        var target = TargetDevice();
        if (target?.Serial == null)
        {
            _appsSerial = null;
            _apps.Clear();
            AppsTitle.Text = "APPS";
            AppsHint.Text = "Start a device to see and install its apps.";
            AppsHint.Visibility = Visibility.Visible;
            return;
        }

        if (!force && _appsSerial == target.Serial) return;
        _appsSerial = target.Serial;
        AppsTitle.Text = $"APPS ON {target.DisplayName.ToUpperInvariant()}";

        var packages = await _adb.ListPackagesAsync(target.Serial, SystemAppsCheck.IsChecked == true);
        if (_appsSerial != target.Serial) return;

        _apps.Clear();
        foreach (var p in packages) _apps.Add(new AppRow(p));
        AppsHint.Text = "No apps installed yet. Drop an .apk file here to install one.";
        AppsHint.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e) => await SyncAppsAsync(force: true);

    private async void SystemApps_Click(object sender, RoutedEventArgs e) => await SyncAppsAsync(force: true);

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Android packages (*.apk)|*.apk",
            Multiselect = true,
            Title = "Choose APK files to install",
        };
        if (dialog.ShowDialog(this) == true) await InstallApksAsync(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await InstallApksAsync(files);
    }

    private async Task InstallApksAsync(IEnumerable<string> files)
    {
        var apks = files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (apks.Count == 0)
        {
            Log("Only .apk files can be installed (.xapk / .apks bundles are not supported yet).");
            return;
        }

        var target = TargetDevice();
        if (target?.Serial == null)
        {
            Log("Start a device first (or select the running device you want to install to).");
            return;
        }

        foreach (var apk in apks)
        {
            SetBusy($"Installing {Path.GetFileName(apk)} on {target.DisplayName}…");
            var result = await _adb.InstallAsync(target.Serial, apk);
            Log(result.Ok && result.StdOut.Contains("Success")
                ? $"Installed {Path.GetFileName(apk)}."
                : $"Could not install {Path.GetFileName(apk)}: {result.Combined}");
        }

        SetBusy(null);
        await SyncAppsAsync(force: true);
    }

    private async void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRow app || _appsSerial == null) return;
        var result = await _adb.LaunchAsync(_appsSerial, app.Package);
        Log(result.Ok && !result.Combined.Contains("No activities found")
            ? $"Opened {app.Package}."
            : $"{app.Package} has no launcher screen to open.");
    }

    private async void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRow app || _appsSerial == null) return;

        var answer = MessageBox.Show(this, $"Uninstall {app.Package}?", "Uninstall app",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        SetBusy($"Uninstalling {app.Package}…");
        var result = await _adb.UninstallAsync(_appsSerial, app.Package);
        SetBusy(null);
        Log(result.Ok ? $"Uninstalled {app.Package}." : $"Could not uninstall {app.Package}: {result.Combined}");
        await SyncAppsAsync(force: true);
    }

    // ---------------------------------------------------------------- misc

    private void Setup_Click(object sender, RoutedEventArgs e) => OpenSetup();

    private void SetBusy(string? message)
    {
        Busy.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = message ?? "Ready";
    }

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message.Trim()}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
