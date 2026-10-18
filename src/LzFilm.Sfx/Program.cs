using System.Runtime.InteropServices;
using LzFilm.Core;

namespace LzFilm.Sfx;

/// <summary>
/// Self-extracting archive: this program followed by an LzFilm archive (and optionally further
/// volumes next to it: name.lzs.002, name.lzs.003, ...).
///
/// Usage:  archive.exe [/dest folder] [/all | /latest | /versions 1,3] [/test] [/yes]
/// (the version options apply to disk image archives; a files-and-folders archive is always extracted completely)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
        Console.Title = "LzFilm self-extracting archive";
        bool unattended = HasFlag(args, "/yes");
        try
        {
            return Run(args, unattended);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Cancelled.");
            return 2;
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: " + e.Message);
            Console.ResetColor();
            return 1;
        }
        finally
        {
            if (!unattended)
            {
                Console.WriteLine();
                Console.Write("Press Enter to close...");
                Console.ReadLine();
            }
        }
    }

    private static int Run(string[] args, bool unattended)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this program.");
        Console.WriteLine("LzFilm self-extracting archive");
        Console.WriteLine(new string('-', 60));

        var info = SnapArchiver.ReadInfo(self);
        var h = info.Header;
        if (!h.Complete)
            throw new InvalidDataException("The archive inside this program is incomplete (the backup was interrupted).");
        Console.WriteLine($"Archive:   {h.BaseName}");
        Console.WriteLine($"Volumes:   {info.VolumePaths.Count}");
        if (!info.IsFiles)
        {
            Console.WriteLine($"Versions:  {h.Versions.Count}");
            for (int i = 0; i < h.Versions.Count; i++)
                Console.WriteLine($"  {i + 1,3}. {h.Versions[i].Name}  ({Bytes(h.Versions[i].Size)})");
        }
        Console.WriteLine();

        if (info.VolumeProblems.Count > 0)
        {
            foreach (var p in info.VolumeProblems)
                Console.WriteLine($"Volume {p.VolumeNumber} ({Path.GetFileName(p.Path)}): {p.Problem}");
            throw new IOException("Some parts of the archive are missing or have the wrong size. Put all parts in the same folder as this program.");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (HasFlag(args, "/test"))
        {
            Console.WriteLine("Testing integrity...");
            var rep = SnapArchiver.Test(self, new ConsoleProgress(), cts.Token);
            Console.WriteLine();
            foreach (var p in rep.Problems)
                Console.WriteLine("  " + p);
            Console.WriteLine(rep.Ok ? $"OK — {rep.SegmentsChecked} segments checked." : "The archive is damaged.");
            return rep.Ok ? 0 : 1;
        }

        if (info.IsFiles)
            return ExtractFiles(args, unattended, self, h, cts.Token);

        List<int> versions = ChooseVersions(args, h.Versions.Count, unattended);
        string? dest = Option(args, "/dest");
        if (dest == null)
        {
            string suggested = Path.Combine(Path.GetDirectoryName(self)!, h.BaseName + "-restored");
            dest = unattended ? suggested : PickFolder(suggested);
            if (dest == null)
                throw new OperationCanceledException();
        }
        long needed = versions.Count > 0 ? h.Versions.Take(versions.Max() + 1).Max(v => v.Size) * 2 : 0;
        Console.WriteLine($"Restoring {versions.Count} version(s) to: {dest}");
        CheckSpace(dest, versions.Select(v => h.Versions[v].Size).Sum(), needed);

        var res = SnapArchiver.Restore(self, dest, versions, new ConsoleProgress(), cts.Token);
        Console.WriteLine();
        Console.WriteLine($"Done in {res.Elapsed:hh\\:mm\\:ss}. Every restored version was verified (SHA-256):");
        foreach (var f in res.Files)
            Console.WriteLine("  " + f);
        return 0;
    }

    private static int ExtractFiles(string[] args, bool unattended, string self, ArchiveHeader h, CancellationToken ct)
    {
        var table = FolderArchiver.ReadIndex(self, ct);
        int files = table.Files.Count(), dirs = table.Directories.Count();
        Console.WriteLine($"Contents:  {files} file(s), {dirs} folder(s), {Bytes(table.DataLength)}");
        foreach (var top in table.Entries.Where(e => !e.Path.Contains('/')).Take(20))
            Console.WriteLine($"  {(top.IsDirectory ? "[folder] " : "")}{top.Path}");
        Console.WriteLine();
        string? dest = Option(args, "/dest");
        if (dest == null)
        {
            string suggested = Path.Combine(Path.GetDirectoryName(self)!, h.BaseName);
            dest = unattended ? suggested : PickFolder(suggested);
            if (dest == null)
                throw new OperationCanceledException();
        }
        Console.WriteLine($"Extracting to: {dest}");
        CheckSpace(dest, table.DataLength, 0);
        var res = FolderArchiver.Extract(self, dest, null, new ConsoleProgress(), ct);
        Console.WriteLine();
        Console.WriteLine($"Done in {res.Elapsed:hh\\:mm\\:ss}: {res.Files.Count} file(s) extracted and verified (SHA-256).");
        return 0;
    }

    private static List<int> ChooseVersions(string[] args, int n, bool unattended)
    {
        if (n == 1 || HasFlag(args, "/all"))
            return Enumerable.Range(0, n).ToList();
        if (HasFlag(args, "/latest"))
            return new List<int> { n - 1 };
        var list = Option(args, "/versions");
        if (list != null)
            return ParseList(list, n) ?? throw new ArgumentException("Invalid /versions list.");
        if (unattended)
            return new List<int> { n - 1 };
        while (true)
        {
            Console.Write($"Restore [L]atest version (default), [A]ll versions, or numbers like 1,3: ");
            var a = (Console.ReadLine() ?? "").Trim();
            if (a.Length == 0 || a.Equals("l", StringComparison.OrdinalIgnoreCase))
                return new List<int> { n - 1 };
            if (a.Equals("a", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Range(0, n).ToList();
            var p = ParseList(a, n);
            if (p != null)
                return p;
            Console.WriteLine("  Please answer L, A or a list of version numbers.");
        }
    }

    private static List<int>? ParseList(string s, int n)
    {
        var res = new SortedSet<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int k) || k < 1 || k > n)
                return null;
            res.Add(k - 1);
        }
        return res.Count > 0 ? res.ToList() : null;
    }

    private static void CheckSpace(string dest, long output, long temp)
    {
        try
        {
            Directory.CreateDirectory(dest);
            var root = Path.GetPathRoot(Path.GetFullPath(dest));
            if (root == null)
                return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < output)
                Console.WriteLine($"Warning: only {Bytes(free)} free on {root}, about {Bytes(output)} is needed.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase) || a.Equals("-" + flag[1..], StringComparison.OrdinalIgnoreCase));

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) || args[i].Equals("-" + name[1..], StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    internal static string Bytes(long b)
    {
        string[] u = { "B", "KiB", "MiB", "GiB", "TiB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:F2} {u[i]}";
    }

    // ------------------------------------------------------------------- folder picker (Win32)

    private static string? PickFolder(string suggested)
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Choose the destination folder in the dialog...");
            var picked = Native.BrowseForFolder("Choose where to restore the archive (a new folder is fine)");
            if (picked != null)
                return picked;
            Console.Write($"No folder chosen. Restore to {suggested}? [Y/n]: ");
            var a = (Console.ReadLine() ?? "").Trim();
            return a.Length == 0 || a.StartsWith("y", StringComparison.OrdinalIgnoreCase) ? suggested : null;
        }
        Console.Write($"Destination folder [{suggested}]: ");
        var s = (Console.ReadLine() ?? "").Trim();
        return s.Length == 0 ? suggested : s;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BROWSEINFOW
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            public IntPtr lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        private const uint BIF_RETURNONLYFSDIRS = 0x0001;
        private const uint BIF_EDITBOX = 0x0010;
        private const uint BIF_NEWDIALOGSTYLE = 0x0040;

        [DllImport("shell32.dll")]
        private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW bi);

        [DllImport("shell32.dll")]
        private static extern int SHGetPathFromIDListEx(IntPtr pidl, IntPtr path, int cchPath, int flags);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        public static string? BrowseForFolder(string title)
        {
            const int cch = 32768;
            IntPtr display = Marshal.AllocHGlobal(520 * 2);
            IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
            IntPtr path = Marshal.AllocHGlobal(cch * 2);
            try
            {
                var bi = new BROWSEINFOW
                {
                    hwndOwner = GetConsoleWindow(),
                    pszDisplayName = display,
                    lpszTitle = titlePtr,
                    ulFlags = BIF_RETURNONLYFSDIRS | BIF_EDITBOX | BIF_NEWDIALOGSTYLE,
                };
                IntPtr pidl = SHBrowseForFolderW(ref bi);
                if (pidl == IntPtr.Zero)
                    return null;
                try
                {
                    return SHGetPathFromIDListEx(pidl, path, cch, 0) != 0 ? Marshal.PtrToStringUni(path) : null;
                }
                finally
                {
                    CoTaskMemFree(pidl);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(display);
                Marshal.FreeHGlobal(titlePtr);
                Marshal.FreeHGlobal(path);
            }
        }
    }

    private sealed class ConsoleProgress : IProgress<ProgressInfo>
    {
        private readonly DateTime _start = DateTime.UtcNow;
        private Phase _phase = (Phase)(-1);
        private DateTime _phaseStart;
        private long _phaseDone;

        public void Report(ProgressInfo p)
        {
            if (p.Phase != _phase)
            {
                _phase = p.Phase;
                _phaseStart = DateTime.UtcNow;
                _phaseDone = p.Done;
            }
            double secs = (DateTime.UtcNow - _phaseStart).TotalSeconds;
            double rate = secs > 1 ? (p.Done - _phaseDone) / secs : 0;
            string eta = rate > 0 && p.Total > p.Done ? $"  ETA {TimeSpan.FromSeconds((p.Total - p.Done) / rate):hh\\:mm\\:ss}" : "";
            string ver = p.Version >= 0 && p.VersionCount > 0 ? $"version {p.Version + 1}/{p.VersionCount}  " : "";
            int width = 30;
            int fill = (int)(p.Fraction * width);
            var line = $"\r[{new string('#', fill)}{new string('.', width - fill)}] {p.Fraction,6:P1}  {ver}{rate / 1048576,6:F1} MB/s{eta}   ";
            try { Console.Write(line); } catch (IOException) { }
        }
    }
}
