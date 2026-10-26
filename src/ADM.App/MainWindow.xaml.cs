using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ADM.Core;
using Forms = System.Windows.Forms;

namespace ADM.App;

public partial class MainWindow : Window
{
    private readonly DownloadManager manager;
    private readonly ObservableCollection<DownloadRow> rows = [];
    private readonly ICollectionView view;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Forms.NotifyIcon tray;
    private readonly Queue<double> speedHistory = new(30);
    private string filter = "All";
    private bool exiting;
    private bool disposed;
    private readonly string[] arguments;
    private readonly LinkReceiver linkReceiver;
    private DownloadRow? Selected => DownloadsGrid.SelectedItem as DownloadRow;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        VersionLabel.Text = "ADM " + typeof(MainWindow).Assembly.GetName().Version + "  /  WINDOWS";
        if (args.Contains("--compact")) { Width = MinWidth; Height = MinHeight; }
        arguments = args;
        var preview = args.Contains("--preview");
        var dataFolder = preview || args.Contains("--screenshot") ? Path.Combine(Path.GetTempPath(), "ADM-preview-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADM");
        manager = new DownloadManager(dataFolder);
        linkReceiver = new LinkReceiver(link => Dispatcher.BeginInvoke(() => { if (exiting) return; ShowFromTray(); if (link != "activate") ShowAdd(link); }));
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = Matches;
        DownloadsGrid.ItemsSource = view;
        tray = new Forms.NotifyIcon
        {
            Text = "ADM · Download manager",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ADM", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Pause all downloads", null, (_, _) => Dispatcher.Invoke(async () => await Perform(manager.PauseAllAsync)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit ADM", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        manager.DownloadCompleted += id => Dispatcher.BeginInvoke(() =>
        {
            if (manager.Settings.NotifyOnCompletion && !exiting)
            {
                var item = manager.Snapshot().FirstOrDefault(x => x.Id == id);
                tray.ShowBalloonTip(4000, "Download complete", item?.FileName ?? "Your file is ready.", Forms.ToolTipIcon.Info);
            }
        });
        timer.Tick += (_, _) => Refresh();
        PreviewKeyDown += KeyboardShortcut;
        Loaded += async (_, _) =>
        {
            if (preview) LoadPreview(); else { Refresh(); manager.StartScheduler(); timer.Start(); }
            if (manager.RecoveryNotice != null) MessageBox.Show(this, manager.RecoveryNotice, "ADM · Queue recovery");
            var addIndex = Array.IndexOf(arguments, "--add");
            if (addIndex >= 0 && arguments.Length > addIndex + 1) ShowAdd(arguments[addIndex + 1]);
            var screenshotIndex = Array.IndexOf(arguments, "--screenshot");
            if (screenshotIndex >= 0 && arguments.Length > screenshotIndex + 1)
            {
                var dialogIndex = Array.IndexOf(arguments, "--capture-dialog");
                Window? dialog = null;
                if (dialogIndex >= 0 && arguments.Length > dialogIndex + 1)
                {
                    dialog = arguments[dialogIndex + 1] switch
                    {
                        "new" => new AddDownloadWindow(manager, ""),
                        "preferences" => new SettingsWindow(manager),
                        "limit" => new SpeedLimitWindow(0),
                        _ => null
                    };
                    if (dialog != null) { dialog.Owner = this; dialog.Show(); }
                }
                await Task.Delay(700);
                UpdateLayout();
                var surface = (FrameworkElement)(dialog?.Content ?? Content);
                var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                // Include the window backdrop when rendering transparent layout containers.
                var backdrop = new DrawingVisual();
                using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 247, 244)), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                bitmap.Render(backdrop);
                bitmap.Render(surface);
                var bars = Descendants(surface).OfType<ProgressBar>().Select(bar => $"Value={bar.Value}; Max={bar.Maximum}; Width={bar.ActualWidth}; Foreground={bar.Foreground}; Indicator={((FrameworkElement?)bar.Template.FindName("PART_Indicator", bar))?.ActualWidth}; Track={((FrameworkElement?)bar.Template.FindName("PART_Track", bar))?.ActualWidth}");
                File.WriteAllLines(arguments[screenshotIndex + 1] + ".layout.txt", bars);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(arguments[screenshotIndex + 1])) encoder.Save(file);
                dialog?.Close();
                exiting = true; Close();
            }
        };
    }

    private bool Matches(object obj)
    {
        if (obj is not DownloadRow row) return false;
        var d = row.Item;
        bool categoryMatch = filter switch
        {
            "All" => true,
            "Active" => d.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Verifying,
            "Queue" => d.Status is DownloadStatus.Queued or DownloadStatus.Scheduled,
            "Paused" => d.Status is DownloadStatus.Paused or DownloadStatus.Pausing,
            "Completed" => d.Status == DownloadStatus.Completed,
            "Failed" => d.Status == DownloadStatus.Failed,
            _ => d.Category == filter
        };
        var search = SearchBox.Text.Trim();
        return categoryMatch && (search.Length == 0 || (d.FileName + " " + d.Url + " " + d.Category).Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private void Refresh()
    {
        var snapshot = manager.Snapshot();
        var ids = snapshot.Select(d => d.Id).ToHashSet();
        foreach (var row in rows.Where(r => !ids.Contains(r.Id)).ToList()) rows.Remove(row);
        foreach (var item in snapshot.OrderByDescending(d => d.AddedAt))
        {
            var row = rows.FirstOrDefault(r => r.Id == item.Id);
            if (row == null) rows.Insert(0, new DownloadRow(item)); else row.Update(item);
        }
        UpdateStatistics(snapshot);
        view.Refresh();
        UpdateEmptyState();
        UpdateDetails();
        var settings = manager.Settings;
        LimitStatus.Text = (settings.GlobalSpeedLimitKbps == 0 ? "No speed limit" : $"Limit {settings.GlobalSpeedLimitKbps:N0} KiB/s") + "  ·  Auto categories " + (settings.AutoCategorize ? "on" : "off");
        StatusBar.Text = manager.RecoveryNotice != null ? "!  Queue save needs attention" : snapshot.Any(d => d.Status == DownloadStatus.Downloading) ? "●  Transfers running" : "●  Ready";
        StatusBar.ToolTip = manager.RecoveryNotice;
    }

    private void UpdateStatistics(IReadOnlyList<DownloadItem> snapshot)
    {
        var speed = snapshot.Sum(d => d.BytesPerSecond);
        var active = snapshot.Count(d => d.Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Verifying);
        var queued = snapshot.Count(d => d.Status is DownloadStatus.Queued or DownloadStatus.Scheduled);
        SpeedStat.Text = Display.Bytes(speed) + "/s";
        ActiveStat.Text = $"{active} download{(active == 1 ? "" : "s")}";
        ConnectionStat.Text = active == 0 ? "Ready when you are" : $"Across {active} active transfer{(active == 1 ? "" : "s")}";
        QueueStat.Text = queued == 0 ? "Your queue is clear" : $"{queued} waiting or scheduled";
        TotalStat.Text = Display.Bytes(snapshot.Where(d => d.Status == DownloadStatus.Completed).Sum(d => d.DownloadedBytes));
        CompletedStat.Text = $"{snapshot.Count(d => d.Status == DownloadStatus.Completed)} completed files";
        speedHistory.Enqueue(speed); if (speedHistory.Count > 30) speedHistory.Dequeue();
        var points = speedHistory.ToArray(); var max = Math.Max(1, points.Max());
        SpeedGraph.Points = new PointCollection(points.Select((v, i) => new Point(i * 72.0 / Math.Max(1, points.Length - 1), 30 - v / max * 26)));
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        var isFresh = rows.Count == 0;
        EmptyTitle.Text = isFresh ? "A fresh start for your downloads" : "Nothing here just yet";
        EmptySubtitle.Text = isFresh ? "Add a link. ADM will take care of the organization." : "Try another filter or add a new download.";
        EmptyAdd.Content = isFresh ? "＋  Add your first download" : "＋  New download";
    }

    private void UpdateDetails()
    {
        var item = Selected?.Item;
        ResumeButton.IsEnabled = item?.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Scheduled;
        PauseButton.IsEnabled = item?.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued or DownloadStatus.Scheduled or DownloadStatus.Verifying;
        if (item == null)
        {
            DetailHeading.Text = "A place for every file";
            DetailText.Text = manager.Settings.AutoCategorize ? "Automatic categories keep your download folder tidy." : "Choose a destination for every download, or turn on automatic categories.";
            DetailPath.Text = manager.Settings.DownloadFolder; return;
        }
        DetailHeading.Text = item.FileName;
        DetailPath.Text = item.FullPath;
        DetailText.Text = item.Error ?? (item.Status == DownloadStatus.Completed
            ? "Download complete · SHA-256: " + item.ActualSha256
            : item.Status == DownloadStatus.Scheduled ? "Scheduled for " + item.ScheduledAt?.ToLocalTime().ToString("f")
            : $"{Display.Bytes(item.DownloadedBytes)} received  ·  {(item.SupportsResume ? "Resume supported" : "Resume unconfirmed / may restart")}  ·  {Math.Max(1, item.Parts.Count)} connection(s)" + (item.SpeedLimitKbps > 0 ? $"  ·  {item.SpeedLimitKbps} KiB/s limit" : ""));
        DetailText.ToolTip = DetailText.Text;
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (Button)sender; filter = (string)clicked.Tag;
        foreach (var button in LibraryNav.Children.OfType<Button>().Concat(CategoryNav.Children.OfType<Button>()))
        { button.Background = new SolidColorBrush(button == clicked ? Color.FromRgb(44, 75, 63) : Colors.Transparent); button.Foreground = new SolidColorBrush(button == clicked ? Colors.White : Color.FromRgb(183, 199, 193)); }
        PageTitle.Text = filter switch { "All" => "All downloads", "Active" => "Downloading", "Queue" => "Queue & scheduled", "Failed" => "Needs attention", _ => filter };
        view.Refresh(); UpdateEmptyState();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (SearchHint == null || view == null) return; SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed; view.Refresh(); UpdateEmptyState(); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { if (manager != null) UpdateDetails(); }

    private async Task Perform(Func<Task> action)
    {
        try { await action(); Refresh(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ADM · Couldn't complete that action", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Add_Click(object sender, RoutedEventArgs e) => ShowAdd();
    private void Paste_Click(object sender, RoutedEventArgs e) => ShowAdd(ReadClipboard());
    private string ReadClipboard() { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return ""; } }
    private void ShowAdd(string initial = "")
    { var dialog = new AddDownloadWindow(manager, initial) { Owner = this }; dialog.ShowDialog(); Refresh(); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    { new SettingsWindow(manager) { Owner = this }.ShowDialog(); Refresh(); }
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) await Perform(() => manager.ResumeAsync(row.Id)); }
    private async void Pause_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) await Perform(() => manager.PauseAsync(row.Id)); }
    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await Perform(manager.PauseAllAsync);
    private void More_Click(object sender, RoutedEventArgs e) { MoreButton.ContextMenu.PlacementTarget = MoreButton; MoreButton.ContextMenu.IsOpen = true; }
    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenSelectedFile();
    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e) { if (Selected?.Item.Status == DownloadStatus.Completed) OpenSelectedFile(); }
    private void OpenSelectedFile()
    {
        if (Selected?.Item is not { Status: DownloadStatus.Completed } item) return;
        if (!File.Exists(item.FullPath)) { MessageBox.Show(this, "This file was moved or removed. Check its destination folder.", "ADM"); return; }
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ADM"); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Selected?.Item.Destination ?? manager.Settings.DownloadFolder;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ADM"); }
    }
    private void CopyUrl_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) Clipboard.SetText(row.Item.Url); }
    private void CopyHash_Click(object sender, RoutedEventArgs e) { if (Selected?.Item.ActualSha256 is { } hash) Clipboard.SetText(hash); }
    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || row.Item.Status == DownloadStatus.Completed) return;
        if (MessageBox.Show(this, "Discard this download's temporary parts and start again?", "Restart download", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await Perform(() => manager.RestartAsync(row.Id));
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (MessageBox.Show(this, "Remove this download from ADM and discard any temporary parts? Completed files stay in your download folder.", "Remove download", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await Perform(() => manager.RemoveAsync(row.Id));
    }
    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    { await Perform(async () => { foreach (var item in manager.Snapshot().Where(d => d.Status == DownloadStatus.Completed)) await manager.RemoveAsync(item.Id); }); }
    private async void ItemSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var prompt = new SpeedLimitWindow(row.Item.SpeedLimitKbps) { Owner = this };
        if (prompt.ShowDialog() == true) await Perform(() => manager.SetSpeedLimitAsync(row.Id, prompt.Limit));
    }
    private void KeyboardShortcut(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { ShowAdd(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && Keyboard.FocusedElement is not TextBox) { ShowAdd(ReadClipboard()); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); e.Handled = true; }
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Window_StateChanged(object sender, EventArgs e)
    { if (WindowState == WindowState.Minimized && manager != null && manager.Settings.MinimizeToTray) Hide(); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (disposed) return;
        e.Cancel = true;
        if (exiting && !IsEnabled) return;
        exiting = true; IsEnabled = false; timer.Stop();
        try { await manager.DisposeAsync(); }
        catch (Exception ex) { MessageBox.Show(this, "Queue save failed: " + ex.Message, "ADM"); }
        linkReceiver.Dispose(); tray.Visible = false; tray.Dispose(); disposed = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void LoadPreview()
    {
        // Explicit design-preview mode only: these fixtures never enter the real queue.
        var examples = new[]
        {
            new DownloadItem { FileName = "Design systems handbook.pdf", Category = "Documents", Url = "https://example.com/handbook.pdf", TotalBytes = 24_500_000, DownloadedBytes = 24_500_000, Status = DownloadStatus.Completed },
            new DownloadItem { FileName = "Coastline — short film.mp4", Category = "Video", Url = "https://example.com/film.mp4", TotalBytes = 1_200_000_000, DownloadedBytes = 816_000_000, BytesPerSecond = 7_800_000, Status = DownloadStatus.Downloading, SupportsResume = true },
            new DownloadItem { FileName = "Studio assets.zip", Category = "Archives", Url = "https://example.com/assets.zip", TotalBytes = 345_000_000, DownloadedBytes = 142_000_000, Status = DownloadStatus.Paused, SupportsResume = true },
            new DownloadItem { FileName = "Ambient sessions.flac", Category = "Music", Url = "https://example.com/sessions.flac", TotalBytes = 89_000_000, DownloadedBytes = 89_000_000, Status = DownloadStatus.Completed }
        };
        foreach (var item in examples) { item.Destination = Path.Combine(manager.Settings.DownloadFolder, item.Category); rows.Add(new DownloadRow(item)); }
        UpdateStatistics(examples); EmptyState.Visibility = Visibility.Collapsed;
        DownloadsGrid.SelectedIndex = 1;
        StatusBar.Text = "●  Design preview · sample data";
    }
}
