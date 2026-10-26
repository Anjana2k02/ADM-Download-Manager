using System.Globalization;
using ADM.Core;

namespace ADM.App;

public abstract class FormWindow : Window
{
    protected readonly StackPanel Fields = new();
    protected readonly TextBlock Error = new() { Foreground = new SolidColorBrush(Color.FromRgb(180, 64, 49)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0) };
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
    protected FormWindow(string title, string subtitle, double width = 560)
    {
        Title = "ADM · " + title; Width = width; SizeToContent = SizeToContent.Height; MaxHeight = Math.Min(850, SystemParameters.WorkArea.Height - 40);
        Style = (Style)FindResource(typeof(Window));
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var shell = new StackPanel { Margin = new Thickness(28) };
        shell.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        shell.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        shell.Children.Add(Fields); shell.Children.Add(Error); shell.Children.Add(Actions);
        Content = new ScrollViewer { Content = shell, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    protected void Field(string label, FrameworkElement control)
    {
        Fields.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
        Fields.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
    }
    protected static TextBox TextField(string value = "") => new() { Text = value };
    protected void Note(string text) => Fields.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    protected Button Action(string label, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        Actions.Children.Add(button); return button;
    }
    protected void CancelButton() { var cancel = Action("Cancel"); cancel.IsCancel = true; }
    protected void FolderField(string label, TextBox text)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a download folder", Multiselect = false };
            if (Directory.Exists(text.Text)) dialog.InitialDirectory = text.Text;
            if (dialog.ShowDialog(this) == true) text.Text = dialog.FolderName;
        };
        Field(label, grid);
        System.Windows.Automation.AutomationProperties.SetName(text, label);
    }
    protected static int Number(TextBox box, string name, int min, int max)
    {
        if (!int.TryParse(box.Text.Trim(), out var value) || value < min || value > max) throw new ArgumentException($"{name} must be between {min:N0} and {max:N0}.");
        return value;
    }
}

public sealed class AddDownloadWindow : FormWindow
{
    private readonly TextBox urls = TextField();
    private readonly TextBox fileName = TextField();
    private readonly TextBox folder = TextField();
    private readonly TextBox hash = TextField();
    private readonly CheckBox schedule = new() { Content = "Schedule for later" };
    private readonly CheckBox start = new() { Content = "Start downloading immediately", IsChecked = true };
    private readonly DatePicker day = new() { SelectedDate = DateTime.Today, Width = 185, IsEnabled = false };
    private readonly TextBox time = TextField(DateTime.Now.AddHours(1).ToString("HH:mm"));

    public AddDownloadWindow(DownloadManager manager, string initial) : base("New download", "Paste a direct file link, or add several links on separate lines.")
    {
        urls.Text = initial; urls.AcceptsReturn = true; urls.Height = 96; urls.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; urls.TextWrapping = TextWrapping.Wrap;
        Field("DOWNLOAD URL(S)", urls);
        Field("FILE NAME  ·  optional, single download only", fileName);
        FolderField("SAVE TO  ·  leave blank for automatic organization", folder);
        Note(manager.Settings.AutoCategorize ? "Default: " + manager.Settings.DownloadFolder + "\\[Category]" : "Default: " + manager.Settings.DownloadFolder);
        Field("EXPECTED SHA-256  ·  optional, single download only", hash);
        Fields.Children.Add(start); Fields.Children.Add(schedule);
        var schedulePanel = new StackPanel { Orientation = Orientation.Horizontal }; schedulePanel.Children.Add(day);
        time.Width = 90; time.Margin = new Thickness(10, 0, 0, 0); time.IsEnabled = false;
        schedulePanel.Children.Add(time); schedulePanel.Children.Add(new TextBlock { Text = "Local time · HH:mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 11 });
        Fields.Children.Add(schedulePanel);
        schedule.Checked += (_, _) => { day.IsEnabled = time.IsEnabled = true; start.IsEnabled = false; };
        schedule.Unchecked += (_, _) => { day.IsEnabled = time.IsEnabled = false; start.IsEnabled = true; };
        Note("Scheduled downloads run while ADM is open and your computer is awake.");
        CancelButton();
        var add = Action("Add download", true);
        add.Click += async (_, _) =>
        {
            var added = 0;
            try
            {
                var links = urls.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                if (links.Length == 0) throw new ArgumentException("Paste at least one direct download URL.");
                if (links.Any(link => !Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))) throw new ArgumentException("Each line must be a complete HTTP or HTTPS URL.");
                if (links.Length > 1 && (!string.IsNullOrWhiteSpace(fileName.Text) || !string.IsNullOrWhiteSpace(hash.Text))) throw new ArgumentException("File name and checksum apply to one download. Clear these fields to add a batch.");
                DateTimeOffset? when = null;
                if (schedule.IsChecked == true)
                {
                    if (day.SelectedDate == null || !TimeSpan.TryParseExact(time.Text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out var clock)) throw new ArgumentException("Choose a date and a time in HH:mm format.");
                    var local = day.SelectedDate.Value.Date.Add(clock);
                    if (TimeZoneInfo.Local.IsInvalidTime(local)) throw new ArgumentException("This time falls within a daylight-saving clock change. Choose another time.");
                    when = new DateTimeOffset(local);
                    if (when <= DateTimeOffset.Now) throw new ArgumentException("Choose a future time for this download.");
                }
                IsEnabled = false;
                foreach (var link in links)
                {
                    await manager.AddAsync(new AddDownloadRequest(link, fileName.Text, folder.Text, start.IsChecked == true, when, hash.Text));
                    added++;
                }
                DialogResult = true;
            }
            catch (Exception ex)
            {
                if (added > 0) urls.Text = string.Join(Environment.NewLine, urls.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Skip(added));
                Error.Text = (added > 0 ? $"Added {added} download(s). " : "") + ex.Message;
            }
            finally { IsEnabled = true; }
        };
        Loaded += (_, _) => urls.Focus();
    }
}

public sealed class SettingsWindow : FormWindow
{
    public SettingsWindow(DownloadManager manager) : base("Preferences", "Your download library, your rules.")
    {
        var settings = manager.Settings;
        var folder = TextField(settings.DownloadFolder);
        var categorize = new CheckBox { Content = "Automatically sort new downloads into category folders", IsChecked = settings.AutoCategorize };
        var concurrent = TextField(settings.MaxConcurrentDownloads.ToString());
        var connections = TextField(settings.ConnectionsPerDownload.ToString());
        var speed = TextField(settings.GlobalSpeedLimitKbps.ToString());
        var retries = TextField(settings.RetryCount.ToString());
        var tray = new CheckBox { Content = "Hide in the system tray when minimized", IsChecked = settings.MinimizeToTray };
        var notifications = new CheckBox { Content = "Notify me when a download completes", IsChecked = settings.NotifyOnCompletion };
        FolderField("DEFAULT DOWNLOAD FOLDER", folder); Fields.Children.Add(categorize);
        Note("Documents · Video · Music · Programs · Archives · Images · Other\nChanges apply to new downloads. Existing destinations stay as assigned.");
        Field("SIMULTANEOUS DOWNLOADS  ·  1–8", concurrent);
        Field("CONNECTIONS PER NEW DOWNLOAD  ·  1–16", connections);
        Note("Large files use multiple connections when the server supports byte ranges.");
        Field("TOTAL SPEED LIMIT (KiB/s)  ·  0 = unlimited", speed);
        Field("RETRY ATTEMPTS  ·  0–8", retries);
        Fields.Children.Add(tray); Fields.Children.Add(notifications);
        Note("Closing ADM pauses active downloads and exits. Minimize it to keep downloads running. Queue data is stored locally in %LocalAppData%\\ADM.");
        CancelButton(); var save = Action("Save preferences", true); save.IsDefault = true;
        save.Click += async (_, _) =>
        {
            try
            {
                settings.DownloadFolder = folder.Text.Trim(); settings.AutoCategorize = categorize.IsChecked == true;
                settings.MaxConcurrentDownloads = Number(concurrent, "Simultaneous downloads", 1, 8);
                settings.ConnectionsPerDownload = Number(connections, "Connections", 1, 16);
                settings.GlobalSpeedLimitKbps = Number(speed, "Speed limit", 0, 1_000_000);
                settings.RetryCount = Number(retries, "Retry attempts", 0, 8);
                settings.MinimizeToTray = tray.IsChecked == true; settings.NotifyOnCompletion = notifications.IsChecked == true;
                await manager.UpdateSettingsAsync(settings); DialogResult = true;
            }
            catch (Exception ex) { Error.Text = ex.Message; }
        };
    }
}

public sealed class SpeedLimitWindow : FormWindow
{
    public int Limit { get; private set; }
    public SpeedLimitWindow(int current) : base("Download speed limit", "Set a cap for the selected file. The total speed limit still applies.", 440)
    {
        var value = TextField(current.ToString()); Field("LIMIT (KiB/s)  ·  0 = unlimited", value);
        CancelButton(); var save = Action("Apply", true); save.IsDefault = true;
        save.Click += (_, _) => { try { Limit = Number(value, "Speed limit", 0, 1_000_000); DialogResult = true; } catch (Exception ex) { Error.Text = ex.Message; } };
    }
}
