using System.Text.Json.Serialization;

namespace ADM.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Pausing, Paused, Scheduled, Verifying, Completed, Failed }

public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Category { get; set; } = "Other";
    public bool UseServerFileName { get; set; }
    public string? AutoCategoryRoot { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public bool SupportsResume { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ActualSha256 { get; set; }
    public string? Error { get; set; }
    public int Connections { get; set; } = 4;
    public int SpeedLimitKbps { get; set; }
    public List<DownloadPart> Parts { get; set; } = [];
    [JsonIgnore] public double BytesPerSecond { get; set; }
    [JsonIgnore] public string FullPath => Path.Combine(Destination, FileName);
}

public sealed class DownloadPart
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long? End { get; set; }
}

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ADM");
    public bool AutoCategorize { get; set; } = true;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int ConnectionsPerDownload { get; set; } = 4;
    public int GlobalSpeedLimitKbps { get; set; }
    public int RetryCount { get; set; } = 3;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
}

public sealed class SavedState
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<DownloadItem> Downloads { get; set; } = [];
}

public record AddDownloadRequest(string Url, string? FileName = null, string? Folder = null,
    bool StartNow = true, DateTimeOffset? ScheduledAt = null, string? ExpectedSha256 = null);

public static class FileCategories
{
    public static readonly string[] Names = ["Documents", "Video", "Music", "Programs", "Archives", "Images", "Other"];
    private static readonly Dictionary<string, string> Extensions = Build();
    private static Dictionary<string, string> Build()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string list) { foreach (var ext in list.Split(' ')) map["." + ext] = category; }
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt csv md rtf odt ods epub mobi json xml");
        Add("Video", "mp4 mkv webm avi mov m4v wmv flv mpg mpeg ts");
        Add("Music", "mp3 flac wav aac ogg m4a opus wma aiff");
        Add("Programs", "exe msi msix appx apk dmg pkg deb rpm iso img");
        Add("Archives", "zip 7z rar tar gz bz2 xz tgz zst");
        Add("Images", "png jpg jpeg gif webp avif svg bmp tif tiff heic ico psd");
        return map;
    }
    public static string Get(string fileName) => Extensions.GetValueOrDefault(Path.GetExtension(fileName), "Other");
    public static string SafeName(string? input)
    {
        var name = (input ?? "download").Replace('\\', '/').Split('/').Last().Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        name = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        if (name.Length > 180) name = name[..160] + Path.GetExtension(name)[..Math.Min(Path.GetExtension(name).Length, 20)];
        return name;
    }
}
