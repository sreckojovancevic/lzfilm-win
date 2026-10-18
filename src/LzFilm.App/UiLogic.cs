using System.Globalization;
using System.Text;

namespace LzFilm.App;

/// <summary>UI helpers without any WinForms dependency (they are unit-tested on every platform).</summary>
internal static class UiLogic
{
    /// <summary>Splits a chosen archive path into folder and base name (".lzs", ".lzs.001", ".exe" are removed).</summary>
    public static (string Directory, string BaseName) SplitArchivePath(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        string name = Path.GetFileName(path);
        int k = name.LastIndexOf(".lzs.", StringComparison.OrdinalIgnoreCase);
        if (k > 0 && name.Length - k - 5 == 3 && name[(k + 5)..].All(char.IsAsciiDigit))
            name = name[..k];
        else if (name.EndsWith(".lzs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return (dir, name.TrimEnd(' ', '.'));
    }

    /// <summary>First file of the archive for the given settings (what the user will see / double-click).</summary>
    public static string FirstVolumeName(string baseName, bool split, bool sfx) =>
        sfx ? baseName + ".exe" : split ? baseName + ".lzs.001" : baseName + ".lzs";

    /// <summary>
    /// Parses a volume size such as "100", "100 MB", "650M", "4.7 GB", "512 KB". A number without a unit
    /// means MB. Units are binary (1 MB = 1 048 576 bytes), as in 7-Zip.
    /// </summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim();
        int paren = s.IndexOf('(');
        if (paren > 0)
            s = s[..paren].Trim();
        s = s.Replace(" ", "").Replace(',', '.');
        int i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
            return null;
        long mul = s[i..].ToUpperInvariant() switch
        {
            "" or "M" or "MB" or "MIB" => 1L << 20,
            "B" => 1,
            "K" or "KB" or "KIB" => 1L << 10,
            "G" or "GB" or "GIB" => 1L << 30,
            "T" or "TB" or "TIB" => 1L << 40,
            _ => 0,
        };
        if (mul == 0)
            return null;
        double bytes = v * mul;
        if (bytes > long.MaxValue / 2)
            return null;
        return (long)Math.Round(bytes);
    }

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v >= 100 ? $"{v:F0} {u[i]}" : v >= 10 ? $"{v:F1} {u[i]}" : $"{v:F2} {u[i]}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>
    /// Recognizes image formats that are not flat (raw) images. Such files are compressed, sparse or
    /// only descriptors, so successive versions do not line up block by block and LzFilm gains little.
    /// </summary>
    public static string? DetectImageWarning(string path)
    {
        byte[] head = new byte[512];
        int n;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            n = f.Read(head, 0, head.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var h = head[..n];
        bool Starts(string magic) => h.Length >= magic.Length && h.AsSpan(0, magic.Length).SequenceEqual(Encoding.ASCII.GetBytes(magic));

        if (Starts("KDMV"))
            return "This is a sparse VMDK. Convert it to a flat image first, for example:\n" +
                   "vmware-vdiskmanager -r disk.vmdk -t 2 disk-flat.vmdk";
        if (Starts("# Disk DescriptorFile"))
            return "This is only a VMDK descriptor (a small text file). Add the \"-flat.vmdk\" file it points to instead.";
        if (Starts("vhdxfile"))
            return "This is a VHDX. Dynamic VHDX files are not block-aligned between versions; convert to a fixed VHD/VHDX or raw image for best results.";
        if (h.Length >= 4 && h[0] == (byte)'Q' && h[1] == (byte)'F' && h[2] == (byte)'I' && h[3] == 0xFB)
            return "This is a QCOW2 image. Convert it to raw first: qemu-img convert -O raw disk.qcow2 disk.raw";
        if (h.Length >= 68 && Encoding.ASCII.GetString(h, 0, 64).Contains("<<< Oracle VM VirtualBox Disk Image >>>"))
            return "This is a dynamic VirtualBox VDI. Convert it to raw first: VBoxManage clonemedium disk.vdi disk.raw --format RAW";
        return null;
    }
}

/// <summary>Speed and remaining time of the current phase, averaged over the last few seconds.</summary>
internal sealed class RateMeter
{
    private readonly Queue<(double T, long Done)> _samples = new();
    private readonly double _window;
    private object? _key;

    public RateMeter(double windowSeconds = 8) => _window = windowSeconds;

    public double BytesPerSecond { get; private set; }
    public TimeSpan? Remaining { get; private set; }

    /// <param name="key">Phase identity; a new key restarts the measurement.</param>
    public void Update(object key, long done, long total, double nowSeconds)
    {
        if (!Equals(key, _key))
        {
            _key = key;
            _samples.Clear();
            BytesPerSecond = 0;
            Remaining = null;
        }
        _samples.Enqueue((nowSeconds, done));
        while (_samples.Count > 2 && nowSeconds - _samples.Peek().T > _window)
            _samples.Dequeue();
        var first = _samples.Peek();
        double dt = nowSeconds - first.T;
        if (dt >= 1 && done >= first.Done)
        {
            BytesPerSecond = (done - first.Done) / dt;
            Remaining = BytesPerSecond > 0 && total > done ? TimeSpan.FromSeconds((total - done) / BytesPerSecond) : null;
        }
    }
}
