using System.Globalization;
using System.Windows;
using StorageManager.Core;

namespace StorageManager.App;

public partial class PolicyWindow : Window
{
    private readonly FolderPolicy original;
    public FolderPolicy Policy { get; private set; }
    public PolicyWindow(ManagedFolder root, int defaults)
    {
        InitializeComponent(); original = Policy = root.Policy;
        FolderPath.Text = root.Path; EnableBox.IsChecked = original.Enabled; RecursiveBox.IsChecked = original.Recursive;
        BatteryBox.IsChecked = original.RunOnBattery;
        MinimumBox.Text = (original.MinimumBytes / 1024).ToString(); MaximumBox.Text = (original.MaximumBytes / 1048576).ToString();
        SavingBox.Text = (original.MinimumSavingBytes / 1024).ToString(); QuietBox.Text = original.QuietSeconds.ToString();
        CooldownBox.Text = original.CooldownSeconds.ToString(); ReserveBox.Text = (original.ReserveBytes / 1048576).ToString();
        StartBox.Text = original.StartHour.ToString(); EndBox.Text = original.EndHour.ToString(); ExclusionsBox.Text = original.Exclusions;
        InheritanceText.Text = $"Last scan found {defaults} active native directory default(s) in the inspected scope. Those can compress new files outside this selective policy, even when paused or closed. Clear native defaults through the separate folder action if you want only the worker to control compression.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            long Number(string text) => long.Parse(text, CultureInfo.InvariantCulture);
            Policy = original with
            {
                Enabled = EnableBox.IsChecked == true, Recursive = RecursiveBox.IsChecked == true, RunOnBattery = BatteryBox.IsChecked == true,
                MinimumBytes = checked(Number(MinimumBox.Text) * 1024), MaximumBytes = checked(Number(MaximumBox.Text) * 1048576),
                MinimumSavingBytes = checked(Number(SavingBox.Text) * 1024), ReserveBytes = checked(Number(ReserveBox.Text) * 1048576),
                QuietSeconds = checked((int)Number(QuietBox.Text)), CooldownSeconds = checked((int)Number(CooldownBox.Text)),
                StartHour = checked((int)Number(StartBox.Text)), EndHour = checked((int)Number(EndBox.Text)), Exclusions = ExclusionsBox.Text.Trim()
            };
            Policy.Validate(); DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { ErrorText.Text = ex.Message; }
    }
}
