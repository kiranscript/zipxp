using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StorageManager.Core;

namespace StorageManager.App;

public partial class PolicyWindow : Window
{
    private readonly FolderPolicy original;
    public FolderPolicy Policy { get; private set; }
    public PolicyWindow(ManagedFolder root, int defaults)
    {
        InitializeComponent(); original = Policy = root.Policy;
        // Never taller than the screen, so Save and Cancel stay reachable when Advanced options is open.
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        // Fixed sizes that switch with the Advanced section: the window never has to re-measure itself around a changing banner.
        AdvancedExpander.Expanded += (_, _) => Height = Math.Min(860, MaxHeight);
        AdvancedExpander.Collapsed += (_, _) => Height = 520;
        FolderTitle.Text = root.Name; FolderPath.Text = root.Path; EnableBox.IsChecked = original.Enabled; RecursiveBox.IsChecked = original.Recursive;
        BatteryBox.IsChecked = original.RunOnBattery;
        for (var hour = 0; hour < 24; hour++)
        {
            var label = DateTime.Today.AddHours(hour).ToString("t");
            StartBox.Items.Add(label); EndBox.Items.Add(label);
        }
        LoadAdvanced(original);
        foreach (var box in new[] { MinimumBox, MaximumBox, SavingBox, QuietBox, CooldownBox, ReserveBox }) box.TextChanged += Field_Changed;
        foreach (var box in new[] { StartBox, EndBox }) box.SelectionChanged += Field_Changed;
        LiveCheck();
        if (defaults > 0)
        {
            InheritanceHost.Visibility = Visibility.Visible;
            InheritanceText.Text = $"{Plain.BuiltIn} is switched on in {Plain.Count(defaults, "folder")} here. Windows compresses new files in those folders by itself, whatever you choose on this page, even while ZipXP is paused or closed. To let only ZipXP decide, close this window and choose More, then \"Turn off {Plain.BuiltIn}\".";
        }
    }

    private (CheckBox Box, string[] Patterns)[] Categories => [
        (PhotosBox, ["*.jpg", "*.png"]), (VideosBox, ["*.mp4"]), (ArchivesBox, ["*.zip", "*.7z"]), (DevBox, ["node_modules/*", ".git/*"])];

    /// <summary>Fills the Advanced section from a policy; also used to put the recommended values back.</summary>
    private void LoadAdvanced(FolderPolicy p)
    {
        MinimumBox.Text = (p.MinimumBytes / 1024).ToString(); MaximumBox.Text = (p.MaximumBytes / 1048576).ToString();
        SavingBox.Text = (p.MinimumSavingBytes / 1024).ToString(); QuietBox.Text = p.QuietSeconds.ToString();
        CooldownBox.Text = p.CooldownSeconds.ToString(); ReserveBox.Text = (p.ReserveBytes / 1048576).ToString();
        StartBox.SelectedIndex = p.StartHour; EndBox.SelectedIndex = p.EndHour;
        AnyTimeBox.IsChecked = p.StartHour == p.EndHour;
        // Common kinds of file are checkboxes; anything else the user typed stays in the text box.
        var remaining = p.Exclusions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var (box, patterns) in Categories)
        {
            var on = patterns.All(x => remaining.Contains(x, StringComparer.OrdinalIgnoreCase));
            box.IsChecked = on; if (on) remaining.RemoveAll(r => patterns.Contains(r, StringComparer.OrdinalIgnoreCase));
        }
        ExclusionsBox.Text = string.Join("; ", remaining);
    }
    private void Restore_Click(object sender, RoutedEventArgs e) { ClearFlag(); LoadAdvanced(new FolderPolicy()); LiveCheck(); }

    private void AnyTime_Changed(object sender, RoutedEventArgs e) { if (HoursPanel != null) HoursPanel.IsEnabled = AnyTimeBox.IsChecked != true; }

    private Control? flagged;
    private void ShowError(string text, Control? field = null)
    {
        ClearFlag();
        ErrorText.Text = text; ErrorHost.Visibility = Visibility.Visible;
        if (field == null) return;
        flagged = field;
        field.SetResourceReference(Control.BorderBrushProperty, "SystemFillColorCriticalBrush");
        field.BorderThickness = new Thickness(2);
    }
    private void ClearFlag()
    {
        if (flagged == null) return;
        flagged.ClearValue(Control.BorderBrushProperty); flagged.ClearValue(Control.BorderThicknessProperty); flagged = null;
        ErrorHost.Visibility = Visibility.Hidden;
    }
    private void Field_Changed(object sender, EventArgs e)
    {
        if (flagged != null && ReferenceEquals(sender, flagged) && flagged.IsKeyboardFocusWithin) ClearFlag();
        LiveCheck();
    }

    /// <summary>Marks a range-limited field and its hint in red as soon as the value is out of range, before Save is pressed.</summary>
    private readonly Dictionary<Control, string> liveMessages = [];
    private void LiveCheck()
    {
        if (MinHint == null) return;
        liveMessages.Clear();
        foreach (var (box, msg, valid, message) in new (TextBox, TextBlock, Func<long, bool>, string)[]
        {
            (MinimumBox, MinMsg, v => v >= 64, "Use 64 KB or more. Smaller files rarely save any space."),
            (MaximumBox, MaxMsg, v => v is >= 1 and <= 256, "Use between 1 and 256 MB."),
            (ReserveBox, ReserveMsg, v => v >= 256, "Keep at least 256 MB of the drive free, so Windows always has room to work.")
        })
        {
            var good = long.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && valid(number);
            box.Tag = good ? null : "Invalid";
            if (!good) liveMessages[box] = message;
            msg.Text = good ? "" : message; msg.Visibility = good ? Visibility.Collapsed : Visibility.Visible;
            if (ReferenceEquals(box, flagged)) continue;
            if (good) { box.ClearValue(Control.BorderBrushProperty); box.ClearValue(Control.BorderThicknessProperty); }
            else { box.SetResourceReference(Control.BorderBrushProperty, "SystemFillColorCriticalBrush"); box.BorderThickness = new Thickness(2); }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            long Number(string text) => long.Parse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            // Same limits the engine enforces, explained one at a time.
            long smallest = Number(MinimumBox.Text), largest = Number(MaximumBox.Text), reserve = Number(ReserveBox.Text);
            long start = AnyTimeBox.IsChecked == true ? 0 : StartBox.SelectedIndex, end = AnyTimeBox.IsChecked == true ? 0 : EndBox.SelectedIndex;
            (string Text, Control Field)? problem =
                smallest < 64 ? ("\"Skip files smaller than\" must be at least 64 KB. Smaller files rarely save any space.", MinimumBox)
                : largest > 256 ? ("\"Skip files larger than\" can be at most 256 MB for now.", MaximumBox)
                : smallest > largest * 1024 ? ("The smallest file size is bigger than the largest. Change one of them.", MinimumBox)
                : reserve < 256 ? ("Keep at least 256 MB of the drive free, so Windows always has room to work.", ReserveBox)
                : AnyTimeBox.IsChecked != true && start == end ? ("Choose different start and end times, or tick \"Work at any time of day\".", StartBox)
                : null;
            if (problem is { } found) { AdvancedExpander.IsExpanded = true; ShowError(liveMessages.ContainsKey(found.Field) ? "Fix the highlighted setting to save." : found.Text, found.Field); found.Field.Focus(); found.Field.BringIntoView(); return; }
            Policy = original with
            {
                Enabled = EnableBox.IsChecked == true, Recursive = RecursiveBox.IsChecked == true, RunOnBattery = BatteryBox.IsChecked == true,
                MinimumBytes = checked(Number(MinimumBox.Text) * 1024), MaximumBytes = checked(Number(MaximumBox.Text) * 1048576),
                MinimumSavingBytes = checked(Number(SavingBox.Text) * 1024), ReserveBytes = checked(Number(ReserveBox.Text) * 1048576),
                QuietSeconds = checked((int)Number(QuietBox.Text)), CooldownSeconds = checked((int)Number(CooldownBox.Text)),
                StartHour = (int)start, EndHour = (int)end, Exclusions = string.Join(";", Categories.Where(c => c.Box.IsChecked == true).SelectMany(c => c.Patterns).Concat(ExclusionsBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            };
            Policy.Validate(); DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { ShowError("Use whole numbers only in the number boxes: no letters, decimals or minus signs."); }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }
}
