using System.Windows;
using DroidDock.Models;
using DroidDock.Services;

namespace DroidDock;

public partial class CreateDeviceWindow : Window
{
    private sealed record Choice(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private readonly SdkPaths _sdk;

    public AvdInfo? Created { get; private set; }

    public CreateDeviceWindow(SdkPaths sdk)
    {
        InitializeComponent();
        _sdk = sdk;

        ImageCombo.ItemsSource = sdk.InstalledImages();
        ImageCombo.SelectedIndex = 0;
        ProfileCombo.ItemsSource = DeviceProfile.All;
        ProfileCombo.SelectedIndex = 0;

        RamCombo.ItemsSource = new[] { new Choice("2 GB", 2048), new Choice("4 GB", 4096), new Choice("8 GB", 8192) };
        RamCombo.SelectedIndex = 1;

        var maxCores = Math.Max(2, Environment.ProcessorCount / 2);
        CoresCombo.ItemsSource = new[] { 2, 4, 6, 8 }.Where(c => c <= maxCores).Select(c => new Choice(c.ToString(), c)).ToList();
        CoresCombo.SelectedIndex = Math.Min(1, CoresCombo.Items.Count - 1);

        StorageCombo.ItemsSource = new[] { new Choice("8 GB", 8), new Choice("16 GB", 16), new Choice("32 GB", 32) };
        StorageCombo.SelectedIndex = 1;

        NameBox.Text = SuggestName();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private static string SuggestName()
    {
        var existing = AvdService.List().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains("DroidDock")) return "DroidDock";
        for (var i = 2; ; i++)
            if (!existing.Contains($"DroidDock_{i}")) return $"DroidDock_{i}";
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim().Replace(' ', '_');
        if (!AvdService.IsValidName(name))
        {
            ShowError("Use only letters, digits, spaces, '.', '_' and '-' in the name.");
            return;
        }
        if (ImageCombo.SelectedItem is not SystemImage image)
        {
            ShowError("Choose an Android version.");
            return;
        }

        try
        {
            var spec = new AvdSpec(
                name,
                image,
                (DeviceProfile)ProfileCombo.SelectedItem,
                ((Choice)RamCombo.SelectedItem).Value,
                ((Choice)CoresCombo.SelectedItem).Value,
                ((Choice)StorageCombo.SelectedItem).Value);
            Created = AvdService.Create(spec, _sdk.Root);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
