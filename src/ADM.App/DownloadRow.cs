using System.ComponentModel;
using ADM.Core;

namespace ADM.App;

public sealed class DownloadRow : INotifyPropertyChanged
{
    public DownloadItem Item { get; private set; }
    public DownloadRow(DownloadItem item) => Item = item;
    public Guid Id => Item.Id;
    public string FileName => Item.FileName;
    public long TotalBytes => Item.TotalBytes ?? 0;
    public string SizeText => Item.TotalBytes.HasValue ? Display.Bytes(Item.TotalBytes.Value) : "Unknown";
    public string Subtitle => Item.Category + "  ·  " + new Uri(Item.Url).Host;
    public string Extension { get { var ext = Path.GetExtension(Item.FileName).TrimStart('.').ToUpperInvariant(); return ext.Length is > 0 and <= 5 ? ext : "FILE"; } }
    public double Progress => Item.Status == DownloadStatus.Completed ? 100 : Item.TotalBytes > 0 ? Math.Clamp(100.0 * Item.DownloadedBytes / Item.TotalBytes.Value, 0, 100) : 0;
    public string PercentText => Item.Status == DownloadStatus.Completed ? "" : Item.TotalBytes > 0 ? $"{Progress:0}%" : "";
    public string StatusText => Item.Status switch { DownloadStatus.Verifying => "Verifying", DownloadStatus.Failed => "Needs attention", DownloadStatus.Connecting => "Connecting", _ => Item.Status.ToString() };
    public string StatusColor => Item.Status switch { DownloadStatus.Completed => "#4A8A55", DownloadStatus.Downloading => "#148363", DownloadStatus.Failed => "#C45B4E", DownloadStatus.Paused or DownloadStatus.Pausing => "#B38934", _ => "#748D9F" };
    public string IconBackground => Item.Category switch { "Video" => "#EDE8F8", "Music" => "#F8E9EB", "Programs" => "#E6EFF9", "Archives" => "#FBF0D8", "Documents" => "#E5F1EC", _ => "#EDF0E9" };
    public string IconForeground => Item.Category switch { "Video" => "#8B73AC", "Music" => "#BA7B86", "Programs" => "#7198BD", "Archives" => "#B49A52", "Documents" => "#629981", _ => "#8C9981" };
    public string SpeedText => Item.Status == DownloadStatus.Downloading ? Display.Bytes(Item.BytesPerSecond) + "/s" : "—";
    public string EtaText => Item.Status == DownloadStatus.Completed ? "Done" : Item.Status == DownloadStatus.Downloading && Item.BytesPerSecond > 0 && Item.TotalBytes.HasValue ? Display.Duration((Item.TotalBytes.Value - Item.DownloadedBytes) / Item.BytesPerSecond) : "—";
    public bool IsIndeterminate => Item.Status == DownloadStatus.Connecting || (Item.Status == DownloadStatus.Downloading && Item.TotalBytes == null);
    public void Update(DownloadItem item) { Item = item; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("")); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Display
{
    public static string Bytes(double value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        if (seconds > 86400) return $"{seconds / 86400:0.0} d";
        if (seconds > 3600) return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m";
        return seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s" : $"{seconds:0}s";
    }
}
