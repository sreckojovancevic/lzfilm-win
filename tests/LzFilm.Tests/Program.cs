using System.Buffers.Binary;
using System.Diagnostics;
using LzFilm.Core;

// Minimal test runner (no test framework needed): dotnet run --project tests/LzFilm.Tests

var tests = new List<(string Name, Action Run)>
{
    ("xz roundtrip and corruption detection", Tests.XzRoundtrip),
    ("chain roundtrip, xor and motion, 1 and 3 threads", Tests.ChainRoundtrip),
    ("identical archive for 1 and 4 threads", Tests.DeterministicAcrossThreads),
    ("volumes: roundtrip, open from any part, missing and damaged volume", Tests.Volumes),
    ("self-extracting layout, with and without volumes", Tests.SelfExtracting),
    ("resume after interruption gives the identical archive", Tests.ResumeAfterCrash),
    ("cancel and resume through the API", Tests.CancelAndResume),
    ("Cyrillic and Latin names", Tests.UnicodeNames),
    ("restore only the last version", Tests.RestoreLastOnly),
    ("verify against originals detects a changed original", Tests.VerifyDetectsChange),
    ("empty and odd-sized versions", Tests.OddSizes),
    ("resume refuses different settings", Tests.ResumeRefusesMismatch),
    ("GUI helpers: names, sizes, image formats, rate meter", Tests.UiHelpers),
    ("folders: pack and extract everything (names, empty files and folders, times)", Tests.FolderRoundtrip),
    ("folders: extract a selection, only needed segments", Tests.FolderPartial),
    ("folders: volumes, self-extracting layout, test, damaged volume", Tests.FolderVolumes),
    ("folders: resume after interruption gives the identical archive", Tests.FolderResume),
    ("folders: verify detects a changed file; archive inside the input is left out", Tests.FolderVerifyAndExclude),
    ("real self-extractor stub (LZFILM_SFX_STUB)", Tests.RealSfxStub),
};

Console.WriteLine($"liblzma {Xz.Version}");
if (args.Length > 2 && args[0] == "bench")
{
    // bench <threads> <preset> <out-dir> <v0> <v1> ...
    int th = int.Parse(args[1]);
    var inputsB = args.Skip(4).ToArray();
    var o = new BackupOptions { Threads = th, Preset = int.Parse(args[2]), Motion = true };
    var swb = Stopwatch.StartNew();
    var rb = SnapArchiver.Create(new BackupJob { Inputs = inputsB, OutputDirectory = args[3], BaseName = "bench", Options = o });
    Console.WriteLine($"threads {th}: {rb.ArchiveBytes / 1048576.0:F2} MiB in {swb.Elapsed.TotalSeconds:F1} s, est. RAM {SnapArchiver.EstimateMemory(o, inputsB.Max(f => new FileInfo(f).Length)) / 1048576} MiB, moved {rb.Stats.Moved}, raw {rb.Stats.NoPrediction}");
    var swv = Stopwatch.StartNew();
    var pr = SnapArchiver.VerifyAgainstOriginals(rb.Files[0], inputsB);
    Console.WriteLine($"verify: {(pr.Count == 0 ? "OK" : string.Join("; ", pr))} in {swv.Elapsed.TotalSeconds:F1} s");
    return 0;
}
if (args.Length >= 7 && args[0] == "packbench")
{
    // packbench <preset> <segment-MiB> <threads> <out-dir> <name> <input> [<input> ...]
    var o = new BackupOptions { Preset = int.Parse(args[1]), SegmentSizeMiB = int.Parse(args[2]), Threads = int.Parse(args[3]) };
    var inputsP = args.Skip(6).ToArray();
    Directory.CreateDirectory(args[4]);
    var sw = Stopwatch.StartNew();
    var scan = FolderArchiver.Scan(inputsP, args[4], args[5]);
    double scanS = sw.Elapsed.TotalSeconds;
    var res = FolderArchiver.Create(new BackupJob { OutputDirectory = args[4], BaseName = args[5], Options = o }, scan);
    double total = sw.Elapsed.TotalSeconds;
    long peak = Process.GetCurrentProcess().PeakWorkingSet64;
    Console.WriteLine($"RESULT;{args[5]};{res.ArchiveBytes};{total:F1};{scanS:F1};{scan.FileCount};{scan.TotalBytes};{peak >> 20};{SnapArchiver.EstimateMemory(o, 0, true) >> 20}");
    return 0;
}
if (args.Length >= 2 && args[0] == "dupes")
{
    // dupes <input> [<input> ...] — how much identical file content there is (potential of deduplication)
    var scan = FolderArchiver.Scan(args.Skip(1).ToArray());
    var files = scan.Table.Entries.Select((e, i) => (e, i)).Where(x => !x.e.IsDirectory && x.e.Size > 0).ToList();
    long dupBytes = 0, dupFiles = 0;
    var groups = new List<(long Size, int Count, string Example)>();
    foreach (var g in files.GroupBy(x => x.e.Size).Where(g => g.Count() > 1))
        foreach (var h in g.GroupBy(x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(scan.SourcePaths[x.i])))).Where(h => h.Count() > 1))
        {
            int c = h.Count();
            dupFiles += c - 1;
            dupBytes += (c - 1) * g.Key;
            groups.Add((g.Key, c, h.First().e.Path));
        }
    Console.WriteLine($"{scan.FileCount:N0} files, {scan.TotalBytes / 1048576.0:F1} MiB");
    Console.WriteLine($"duplicate copies: {dupFiles:N0} files, {dupBytes / 1048576.0:F1} MiB ({100.0 * dupBytes / Math.Max(1, scan.TotalBytes):F1}% of the data)");
    foreach (var g in groups.OrderByDescending(g => (g.Count - 1) * g.Size).Take(10))
        Console.WriteLine($"  {g.Count} x {g.Size / 1048576.0:F2} MiB  {g.Example}");
    Console.WriteLine("largest extensions:");
    foreach (var x in files.GroupBy(x => Path.GetExtension(x.e.Name).ToLowerInvariant()).Select(g => (Ext: g.Key, Bytes: g.Sum(y => y.e.Size), N: g.Count())).OrderByDescending(x => x.Bytes).Take(12))
        Console.WriteLine($"  {(x.Ext.Length == 0 ? "(none)" : x.Ext),-10} {x.Bytes / 1048576.0,9:F1} MiB  {x.N,8:N0} files");
    long git = files.Where(x => x.e.Path.Contains("/.git/")).Sum(x => x.e.Size);
    Console.WriteLine($"inside .git folders: {git / 1048576.0:F1} MiB ({100.0 * git / Math.Max(1, scan.TotalBytes):F1}%)");
    return 0;
}
int failed = 0;
foreach (var (name, run) in tests)
{
    var sw = Stopwatch.StartNew();
    try
    {
        run();
        Console.WriteLine($"PASS  {name}  ({sw.Elapsed.TotalSeconds:F1} s)");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {e}");
    }
}
Console.WriteLine(failed == 0 ? $"All {tests.Count} tests passed." : $"{failed} of {tests.Count} tests FAILED.");
return failed == 0 ? 0 : 1;

static class Tests
{
    static readonly DateTime T0 = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>A small tree with the awkward cases: nested and empty folders, empty file, duplicates, Unicode names, a file over several segments.</summary>
    static string MakeTree(string dir, string name = "Проба фолдер")
    {
        var root = Path.Combine(dir, name);
        var r = new Rng(777);
        void W(string rel, byte[] data, int minutes)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            File.SetLastWriteTimeUtc(p, T0.AddMinutes(minutes));
        }
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 6000).Select(i => $"line {i} lorem ipsum ћирилица šđčćž\n")));
        W("a.txt", text, 1);
        W("copy of a.txt", text, 2);
        var big = new byte[(5 << 20) / 2 + 12345];
        r.Fill(big.AsSpan(0, big.Length * 2 / 3));
        W(Path.Combine("sub", "c.bin"), big, 3);
        W(Path.Combine("sub", "deep", "d.log"), text.AsSpan(0, 5000).ToArray(), 4);
        W("z.empty", Array.Empty<byte>(), 5);
        W(Path.Combine("sub", "also.empty"), Array.Empty<byte>(), 6);
        W("šđčćž.txt", "latinica"u8.ToArray(), 7);
        W("ћирилица.doc", "ћирилица"u8.ToArray(), 8);
        Directory.CreateDirectory(Path.Combine(root, "empty folder"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "empty too"));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "empty folder"), T0.AddDays(-3));
        return root;
    }

    static void CompareTree(string expected, string actual)
    {
        Check(Directory.Exists(actual), "extracted folder exists: " + actual);
        var e = Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var a = Directory.EnumerateFileSystemEntries(actual, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(actual, p)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(e.SequenceEqual(a), $"same entries:\n  expected {string.Join(", ", e)}\n  got      {string.Join(", ", a)}");
        foreach (var rel in e)
        {
            var pe = Path.Combine(expected, rel);
            var pa = Path.Combine(actual, rel);
            if (File.Exists(pe))
            {
                Check(File.ReadAllBytes(pe).AsSpan().SequenceEqual(File.ReadAllBytes(pa)), "content of " + rel);
                Check(File.GetLastWriteTimeUtc(pe) == File.GetLastWriteTimeUtc(pa), "time of " + rel);
            }
            else
            {
                Check(Directory.Exists(pa), "folder " + rel);
                Check(Directory.GetLastWriteTimeUtc(pe) == Directory.GetLastWriteTimeUtc(pa), "folder time of " + rel);
            }
        }
    }

    public static void FolderRoundtrip()
    {
        var dir = NewDir("fold");
        var root = MakeTree(dir);
        var single = Path.Combine(dir, "single.dat");
        File.WriteAllBytes(single, Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray());
        File.SetLastWriteTimeUtc(single, T0);
        var other = Path.Combine(dir, "other", "Проба фолдер");      // same top-level name → renamed
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.txt"), "x");
        var inputs = new[] { root, single, other };
        var outDir = Path.Combine(dir, "arc");
        var scan = FolderArchiver.Scan(inputs);
        Check(scan.Skipped.Count == 0, "nothing skipped: " + string.Join("; ", scan.Skipped));
        Check(scan.Table.Entries.Any(x => x.Path == "Проба фолдер (2)/x.txt"), "duplicate top-level name renamed");
        foreach (int threads in new[] { 1, 3 })
        {
            var res = FolderArchiver.Create(Job(inputs, Path.Combine(outDir, "t" + threads), "pack", o => o.Threads = threads), FolderArchiver.Scan(inputs));
            Check(res.Completed, "created");
            var info = SnapArchiver.ReadInfo(res.Files[0]);
            Check(info.IsFiles && info.InputBytes == scan.TotalBytes, "files archive info");
            Check(SnapArchiver.Test(res.Files[0]).Ok, "test ok");
            Check(res.ArchiveBytes < scan.TotalBytes, "compressed");
            var t = FolderArchiver.ReadIndex(res.Files[0]);
            Check(t.Entries.Count == scan.Table.Entries.Count, "index entries");
            var dest = Path.Combine(dir, "out" + threads);
            var ex = FolderArchiver.Extract(res.Files[0], dest);
            Check(ex.Files.Count == scan.FileCount, $"extracted {ex.Files.Count} of {scan.FileCount}");
            CompareTree(root, Path.Combine(dest, "Проба фолдер"));
            CompareTree(other, Path.Combine(dest, "Проба фолдер (2)"));
            Check(File.ReadAllBytes(Path.Combine(dest, "single.dat")).AsSpan().SequenceEqual(File.ReadAllBytes(single)), "single file");
            // extracting again over existing files works
            FolderArchiver.Extract(res.Files[0], dest);
            CompareTree(root, Path.Combine(dest, "Проба фолдер"));
            bool refused = false;
            try { SnapArchiver.Restore(res.Files[0], Path.Combine(dir, "wrong")); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "snapshot restore refuses a files archive");
        }
        Check(File.ReadAllBytes(Path.Combine(outDir, "t1", "pack.lzs")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(outDir, "t3", "pack.lzs"))),
            "identical archive for 1 and 3 threads");
    }

    public static void FolderPartial()
    {
        var dir = NewDir("fpart");
        var root = MakeTree(dir);
        var outDir = Path.Combine(dir, "arc");
        var res = FolderArchiver.Create(Job(new[] { root }, outDir, "p"), FolderArchiver.Scan(new[] { root }));
        var dest = Path.Combine(dir, "out");
        var ex = FolderArchiver.Extract(res.Files[0], dest, new[] { "Проба фолдер/sub/deep", "Проба фолдер/ћирилица.doc", "Проба фолдер/z.empty", "Проба фолдер/empty folder" });
        var got = Directory.EnumerateFileSystemEntries(dest, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dest, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var want = new[] { "Проба фолдер", "Проба фолдер/empty folder", "Проба фолдер/sub", "Проба фолдер/sub/deep", "Проба фолдер/sub/deep/d.log", "Проба фолдер/z.empty", "Проба фолдер/ћирилица.doc" };
        Check(got.SequenceEqual(want), "selection extracted: " + string.Join(", ", got));
        Check(ex.Files.Count == 3, "three files");
        Check(File.ReadAllBytes(Path.Combine(dest, "Проба фолдер", "sub", "deep", "d.log")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "sub", "deep", "d.log"))), "content");
        // A selected file that lies in the last segments only: the big file is not needed for it.
        var dest2 = Path.Combine(dir, "out2");
        FolderArchiver.Extract(res.Files[0], dest2, new[] { "Проба фолдер/sub/c.bin" });
        Check(File.ReadAllBytes(Path.Combine(dest2, "Проба фолдер", "sub", "c.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "sub", "c.bin"))), "multi-segment file");

        // Damage a data segment that d.log does not use: d.log still extracts, everything else fails.
        var info = SnapArchiver.ReadInfo(res.Files[0]);
        var t = FolderArchiver.ReadIndex(res.Files[0]);
        var dlog = t.Entries.Single(e => e.Path.EndsWith("/d.log"));
        long seg = info.Header.SegmentSize;
        var victim = info.Footer!.Segments.First(x => x.Version == 0 && x.Index != dlog.Offset / seg && x.Index != (dlog.Offset + dlog.Size - 1) / seg);
        var bytes = File.ReadAllBytes(res.Files[0]);
        bytes[victim.RecordOffset + FormatConst.SegmentRecordSize + victim.CompressedLength / 2] ^= 0x10;
        File.WriteAllBytes(res.Files[0], bytes);
        var dest3 = Path.Combine(dir, "out3");
        FolderArchiver.Extract(res.Files[0], dest3, new[] { "Проба фолдер/sub/deep/d.log" });
        Check(File.Exists(Path.Combine(dest3, "Проба фолдер", "sub", "deep", "d.log")), "file outside the damaged segment extracts");
        bool failed = false;
        try { FolderArchiver.Extract(res.Files[0], Path.Combine(dir, "out4")); } catch (InvalidDataException) { failed = true; }
        Check(failed, "full extraction reports the damaged segment");
    }

    public static void FolderVolumes()
    {
        var dir = NewDir("fvol");
        var root = MakeTree(dir);
        var stub = FakeStub();
        foreach (var sfx in new[] { false, true })
        {
            var outDir = Path.Combine(dir, "arc" + sfx);
            var res = FolderArchiver.Create(Job(new[] { root }, outDir, "v", o => { o.VolumeSize = 1L << 20; o.SelfExtracting = sfx; o.Preset = 0; }, stub), FolderArchiver.Scan(new[] { root }));
            Check(res.Files.Count >= 2, "several volumes");
            Check(res.Files[0].EndsWith(sfx ? "v.exe" : "v.lzs.001"), "first volume name");
            var dest = Path.Combine(dir, "out" + sfx);
            FolderArchiver.Extract(res.Files[^1], dest);       // opened from the last part
            CompareTree(root, Path.Combine(dest, "Проба фолдер"));
            var bytes = File.ReadAllBytes(res.Files[1]);
            bytes[bytes.Length / 2] ^= 0x55;
            File.WriteAllBytes(res.Files[1], bytes);
            var t = SnapArchiver.Test(res.Files[0]);
            Check(!t.Ok && t.DamagedVolumes.Contains(2), "damaged volume 2 reported");
        }
    }

    public static void FolderResume()
    {
        var dir = NewDir("fres");
        var root = MakeTree(dir);
        var inputs = new[] { root };
        foreach (long vol in new[] { 0L, 1L << 20 })
        {
            var refDir = Path.Combine(dir, "ref" + vol);
            var refRes = FolderArchiver.Create(Job(inputs, refDir, "r", o => o.VolumeSize = vol), FolderArchiver.Scan(inputs));
            var refBytes = ConcatFiles(refRes.Files);
            long logical = SnapArchiver.ReadInfo(refRes.Files[0]).Header.LogicalLength;
            foreach (var cut in new[] { FormatConst.HeaderSize + 10, logical / 3, logical / 2, logical - 200, logical - 10 })
            {
                var wd = Path.Combine(dir, $"w{vol}-{cut}");
                Directory.CreateDirectory(wd);
                foreach (var f in refRes.Files)
                    File.Copy(f, Path.Combine(wd, Path.GetFileName(f)));
                var files = refRes.Files.Select(f => Path.Combine(wd, Path.GetFileName(f))).ToList();
                SimulateCrash(files, 0, false, vol, cut);
                var job = Job(inputs, wd, "r", o => o.VolumeSize = vol, resume: true);
                var ri = FolderArchiver.InspectResume(job, FolderArchiver.Scan(inputs));
                Check(ri.CanResume, $"inspect resume (cut {cut}): {ri.Reason}");
                var res = FolderArchiver.Create(job, FolderArchiver.Scan(inputs));
                Check(res.Completed && ConcatFiles(res.Files).AsSpan().SequenceEqual(refBytes), $"identical archive after resume (vol={vol}, cut={cut})");
            }
            // A changed file list refuses to resume.
            var wd2 = Path.Combine(dir, "changed" + vol);
            Directory.CreateDirectory(wd2);
            foreach (var f in refRes.Files)
                File.Copy(f, Path.Combine(wd2, Path.GetFileName(f)));
            SimulateCrash(refRes.Files.Select(f => Path.Combine(wd2, Path.GetFileName(f))).ToList(), 0, false, vol, logical / 2);
            File.WriteAllText(Path.Combine(root, "new.txt"), "new");
            var ri2 = FolderArchiver.InspectResume(Job(inputs, wd2, "r", o => o.VolumeSize = vol, resume: true), FolderArchiver.Scan(inputs));
            Check(!ri2.CanResume && ri2.Reason.Contains("changed"), "changed files refuse resume: " + ri2.Reason);
            File.Delete(Path.Combine(root, "new.txt"));
        }
    }

    public static void FolderVerifyAndExclude()
    {
        var dir = NewDir("fver");
        var root = MakeTree(dir);
        // The archive is written into the folder being packed: its own parts must be left out.
        var scan = FolderArchiver.Scan(new[] { root }, root, "self");
        var res = FolderArchiver.Create(Job(new[] { root }, root, "self", o => o.VolumeSize = 1L << 20), scan);
        var rescan = FolderArchiver.Scan(new[] { root }, root, "self");
        Check(rescan.Table.Entries.Count == scan.Table.Entries.Count, "archive parts are not packed into themselves");
        Check(FolderArchiver.Verify(res.Files[0], scan).Count == 0, "verify ok");
        var p = Path.Combine(root, "sub", "deep", "d.log");
        var b = File.ReadAllBytes(p);
        b[100] ^= 1;
        File.WriteAllBytes(p, b);
        var problems = FolderArchiver.Verify(res.Files[0], scan);
        Check(problems.Count == 1 && problems[0].Contains("d.log"), "verify reports the changed file: " + string.Join("; ", problems));
    }

    public static void UiHelpers()
    {
        static void Eq<T>(T a, T b, string what) => Check(EqualityComparer<T>.Default.Equals(a, b), $"{what}: got {a}, expected {b}");
        var root = Path.GetFullPath(Path.GetTempPath());
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "disk.lzs")).BaseName, "disk", "strip .lzs");
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "disk.lzs.001")).BaseName, "disk", "strip .lzs.001");
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "disk.exe")).BaseName, "disk", "strip .exe");
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "my.disk.v2")).BaseName, "my.disk.v2", "keep dots");
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "ћирилица.lzs")).BaseName, "ћирилица", "cyrillic");
        Eq(LzFilm.App.UiLogic.SplitArchivePath(Path.Combine(root, "disk.lzs")).Directory, root.TrimEnd(Path.DirectorySeparatorChar), "dir");
        Eq(LzFilm.App.UiLogic.FirstVolumeName("a", true, false), "a.lzs.001", "first split");
        Eq(LzFilm.App.UiLogic.FirstVolumeName("a", true, true), "a.exe", "first sfx");
        Eq(LzFilm.App.UiLogic.FirstVolumeName("a", false, false), "a.lzs", "first single");
        Eq(LzFilm.App.UiLogic.ParseSize("100"), 100L << 20, "100");
        Eq(LzFilm.App.UiLogic.ParseSize("10 MB"), 10L << 20, "10 MB");
        Eq(LzFilm.App.UiLogic.ParseSize("650 MB (CD)"), 650L << 20, "preset with note");
        Eq(LzFilm.App.UiLogic.ParseSize("4,7 GB"), (long)Math.Round(4.7 * (1L << 30)), "comma decimal");
        Eq(LzFilm.App.UiLogic.ParseSize("512k"), 512L << 10, "512k");
        Eq(LzFilm.App.UiLogic.ParseSize("2GiB"), 2L << 30, "GiB");
        Eq(LzFilm.App.UiLogic.ParseSize("1048576 B"), 1L << 20, "bytes");
        Eq(LzFilm.App.UiLogic.ParseSize("abc"), null, "abc");
        Eq(LzFilm.App.UiLogic.ParseSize("-5"), null, "negative");
        Eq(LzFilm.App.UiLogic.ParseSize("5 XB"), null, "bad unit");
        Eq(LzFilm.App.UiLogic.ParseSize(""), null, "empty");

        var dir = NewDir("ui");
        var vmdk = Path.Combine(dir, "s.vmdk");
        File.WriteAllBytes(vmdk, "KDMV\u0001\0\0\0"u8.ToArray());
        Check(LzFilm.App.UiLogic.DetectImageWarning(vmdk)?.Contains("sparse VMDK") == true, "sparse vmdk");
        var desc = Path.Combine(dir, "d.vmdk");
        File.WriteAllText(desc, "# Disk DescriptorFile\nversion=1\n");
        Check(LzFilm.App.UiLogic.DetectImageWarning(desc)?.Contains("descriptor") == true, "descriptor");
        var raw = Path.Combine(dir, "r.img");
        File.WriteAllBytes(raw, new byte[4096]);
        Check(LzFilm.App.UiLogic.DetectImageWarning(raw) == null, "raw no warning");

        var m = new LzFilm.App.RateMeter(8);
        m.Update(1, 0, 1000, 0);
        m.Update(1, 100, 1000, 1);
        m.Update(1, 300, 1000, 3);
        Check(Math.Abs(m.BytesPerSecond - 100) < 1e-9, "rate 100/s");
        Check(m.Remaining is { } r && Math.Abs(r.TotalSeconds - 7) < 1e-6, "eta 7 s");
        m.Update(2, 0, 50, 4);
        Check(m.BytesPerSecond == 0 && m.Remaining == null, "phase change resets");
    }

    /// <summary>
    /// End-to-end: a real published LzFilm.Sfx (path in LZFILM_SFX_STUB) is put in front of the archive,
    /// and the resulting program restores itself, also from split volumes. Skipped when not set.
    /// </summary>
    public static void RealSfxStub()
    {
        var stubPath = Environment.GetEnvironmentVariable("LZFILM_SFX_STUB");
        if (string.IsNullOrEmpty(stubPath) || !File.Exists(stubPath))
        {
            Console.WriteLine("      (skipped: set LZFILM_SFX_STUB to a published LzFilm.Sfx executable)");
            return;
        }
        var stub = File.ReadAllBytes(stubPath);
        foreach (long vol in new long[] { 0, 1L << 20 })
        {
            var dir = NewDir("realsfx" + vol);
            var inputs = MakeVersions(dir, 3);
            var res = SnapArchiver.Create(Job(inputs, dir, "self", o => { o.SelfExtracting = true; o.VolumeSize = vol; }, stub));
            Check(res.Completed, "sfx archive created");
            var exe = res.Files[0];
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var dest = Path.Combine(dir, "out");
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (var a in new[] { "/dest", dest, "/all", "/yes" })
                psi.ArgumentList.Add(a);
            using var pr = Process.Start(psi)!;
            var output = pr.StandardOutput.ReadToEnd();
            pr.WaitForExit();
            Check(pr.ExitCode == 0, $"sfx exit code {pr.ExitCode} (volumes {res.Files.Count}):\n{output}");
            foreach (var input in inputs)
            {
                var restored = Path.Combine(dest, Path.GetFileName(input));
                Check(File.Exists(restored) && File.ReadAllBytes(restored).AsSpan().SequenceEqual(File.ReadAllBytes(input)), "sfx restored " + Path.GetFileName(input));
            }
            var tpsi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
            tpsi.ArgumentList.Add("/test");
            tpsi.ArgumentList.Add("/yes");
            using var tp = Process.Start(tpsi)!;
            var tout = tp.StandardOutput.ReadToEnd();
            tp.WaitForExit();
            Check(tp.ExitCode == 0 && tout.Contains("OK"), "sfx /test:\n" + tout);
        }
        // files-and-folders archive
        foreach (long vol in new long[] { 0, 1L << 20 })
        {
            var dir = NewDir("realsfxf" + vol);
            var root = MakeTree(dir);
            var res = FolderArchiver.Create(Job(new[] { root }, dir, "folder", o => { o.SelfExtracting = true; o.VolumeSize = vol; }, stub), FolderArchiver.Scan(new[] { root }));
            var exe = res.Files[0];
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var dest = Path.Combine(dir, "out");
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (var a in new[] { "/dest", dest, "/yes" })
                psi.ArgumentList.Add(a);
            using var pr = Process.Start(psi)!;
            var output = pr.StandardOutput.ReadToEnd();
            pr.WaitForExit();
            Check(pr.ExitCode == 0, $"folder sfx exit code {pr.ExitCode}:\n{output}");
            CompareTree(root, Path.Combine(dest, "Проба фолдер"));
        }
    }

    static string NewDir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), "lzfilm-tests", tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void Check(bool cond, string msg)
    {
        if (!cond)
            throw new Exception("check failed: " + msg);
    }

    sealed class Rng
    {
        ulong _x;
        public Rng(ulong seed) => _x = seed | 1;
        public ulong Next() { _x ^= _x << 13; _x ^= _x >> 7; _x ^= _x << 17; return _x; }
        public void Fill(Span<byte> b) { for (int i = 0; i < b.Length; i++) b[i] = (byte)Next(); }
        public int Int(int max) => (int)(Next() % (ulong)max);
    }

    /// <summary>Three versions of a fake disk: mixed random/structured blocks, moves, edits, growth.</summary>
    static string[] MakeVersions(string dir, int sizeMiB = 5, string[]? names = null)
    {
        const int B = 4096;
        var r = new Rng(12345);
        int blocks = sizeMiB * 256;
        var v0 = new byte[blocks * B + 777];
        for (int b = 0; b < blocks; b++)
        {
            var blk = v0.AsSpan(b * B, B);
            int kind = r.Int(10);
            if (kind < 3) r.Fill(blk);                              // incompressible
            else if (kind < 5) blk.Clear();                         // free space
            else for (int i = 0; i < B; i++) blk[i] = (byte)(i * kind + b); // structured
        }
        r.Fill(v0.AsSpan(blocks * B));
        var v1 = (byte[])v0.Clone();
        for (int i = 0; i < 40; i++)                                // relocate blocks
        {
            int src = r.Int(blocks), dst = r.Int(blocks);
            v0.AsSpan(src * B, B).CopyTo(v1.AsSpan(dst * B, B));
        }
        for (int i = 0; i < 300; i++)                               // small edits
            v1[r.Int(v1.Length)] ^= (byte)(1 + r.Int(255));
        r.Fill(v1.AsSpan(B * 7, B * 3));                            // new content
        var v2 = new byte[v1.Length + B * 50 + 123];                // grows
        v1.CopyTo(v2, 0);
        r.Fill(v2.AsSpan(v1.Length));
        for (int i = 0; i < 20; i++)
        {
            int src = r.Int(blocks), dst = r.Int(blocks);
            v1.AsSpan(src * B, B).CopyTo(v2.AsSpan(dst * B, B));
        }
        names ??= new[] { "v0-flat.vmdk", "v1-flat.vmdk", "v2-flat.vmdk" };
        var data = new[] { v0, v1, v2 };
        var paths = new string[3];
        for (int i = 0; i < 3; i++)
        {
            paths[i] = Path.Combine(dir, names[i]);
            File.WriteAllBytes(paths[i], data[i]);
            File.SetLastWriteTimeUtc(paths[i], new DateTime(2026, 10, 17, 12, i, 0, DateTimeKind.Utc));
        }
        return paths;
    }

    static BackupJob Job(string[] inputs, string outDir, string name, Action<BackupOptions>? cfg = null, byte[]? stub = null, bool resume = false)
    {
        var o = new BackupOptions { Preset = 1, Threads = 2, SegmentSizeMiB = 1, ArchiveId = new Guid("11111111-2222-3333-4444-555555555555") };
        cfg?.Invoke(o);
        return new BackupJob { Inputs = inputs, OutputDirectory = outDir, BaseName = name, Options = o, SfxStub = stub, Resume = resume };
    }

    static void AssertRestores(string archive, string[] inputs, string tag)
    {
        var dest = NewDir(tag + "-out");
        var res = SnapArchiver.Restore(archive, dest);
        Check(res.Files.Count == inputs.Length, "restored file count");
        for (int i = 0; i < inputs.Length; i++)
        {
            Check(Path.GetFileName(res.Files[i]) == Path.GetFileName(inputs[i]), $"name of version {i + 1}");
            Check(File.ReadAllBytes(res.Files[i]).AsSpan().SequenceEqual(File.ReadAllBytes(inputs[i])), $"content of version {i + 1}");
        }
        var t = SnapArchiver.Test(archive);
        Check(t.Ok, "test: " + string.Join("; ", t.Problems));
        var vp = SnapArchiver.VerifyAgainstOriginals(archive, inputs);
        Check(vp.Count == 0, "verify: " + string.Join("; ", vp));
    }

    static byte[] ConcatFiles(IEnumerable<string> files) => files.SelectMany(File.ReadAllBytes).ToArray();

    public static void XzRoundtrip()
    {
        var r = new Rng(7);
        foreach (int n in new[] { 1, 100, 4096, 1 << 20 })
        {
            var a = new byte[n];
            r.Fill(a.AsSpan(0, n / 2));
            var c = new byte[Xz.Bound(n)];
            int cl = Xz.Compress(a, c, 1);
            var d = new byte[n];
            Xz.Decompress(c.AsSpan(0, cl), d);
            Check(a.AsSpan().SequenceEqual(d), "xz roundtrip " + n);
            c[cl / 2] ^= 0x40;
            bool threw = false;
            try { Xz.Decompress(c.AsSpan(0, cl), d); } catch (InvalidDataException) { threw = true; }
            Check(threw, "corruption detected " + n);
        }
    }

    public static void ChainRoundtrip()
    {
        var dir = NewDir("chain");
        var inputs = MakeVersions(dir);
        foreach (bool motion in new[] { false, true })
            foreach (int threads in new[] { 1, 3 })
            {
                var outDir = Path.Combine(dir, $"a-{motion}-{threads}");
                var res = SnapArchiver.Create(Job(inputs, outDir, "backup", o => { o.Motion = motion; o.Threads = threads; }));
                Check(res.Completed && res.Files.Count == 1, "completed");
                if (motion)
                    Check(res.Stats.Moved >= 30, "moved blocks found: " + res.Stats.Moved);
                AssertRestores(res.Files[0], inputs, "chain");
            }
    }

    public static void DeterministicAcrossThreads()
    {
        var dir = NewDir("det");
        var inputs = MakeVersions(dir);
        var a = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "t1"), "b", o => o.Threads = 1));
        var b = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "t4"), "b", o => o.Threads = 4));
        Check(File.ReadAllBytes(a.Files[0]).AsSpan().SequenceEqual(File.ReadAllBytes(b.Files[0])), "archives identical");
    }

    public static void Volumes()
    {
        var dir = NewDir("vol");
        var inputs = MakeVersions(dir, 6);
        var outDir = Path.Combine(dir, "out");
        var res = SnapArchiver.Create(Job(inputs, outDir, "backup", o => o.VolumeSize = 1 << 20));
        Check(res.Files.Count >= 3, "several volumes: " + res.Files.Count);
        Check(Path.GetFileName(res.Files[0]) == "backup.lzs.001" && Path.GetFileName(res.Files[1]) == "backup.lzs.002", "volume names");
        AssertRestores(res.Files[0], inputs, "vol");
        AssertRestores(res.Files[1], inputs, "vol2");   // opened from part 2

        // Same content as a single-file archive, just split.
        var single = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "single"), "backup"));
        var joined = ConcatFiles(res.Files);
        var one = File.ReadAllBytes(single.Files[0]);
        Check(joined.Length == one.Length, "split length equals single length");

        // Damaged volume 2.
        var bad = Path.Combine(dir, "bad");
        Directory.CreateDirectory(bad);
        foreach (var f in res.Files)
            File.Copy(f, Path.Combine(bad, Path.GetFileName(f)));
        var v2 = Path.Combine(bad, "backup.lzs.002");
        var bytes = File.ReadAllBytes(v2);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(v2, bytes);
        var t = SnapArchiver.Test(Path.Combine(bad, "backup.lzs.001"));
        Check(!t.Ok && t.DamagedVolumes.Contains(2) && t.MissingVolumes.Count == 0, "damaged volume 2 reported: " + string.Join("; ", t.Problems));
        bool threw = false;
        try { SnapArchiver.Restore(Path.Combine(bad, "backup.lzs.001"), Path.Combine(dir, "badout")); }
        catch (InvalidDataException) { threw = true; }
        Check(threw, "restore refuses damaged data");

        // Missing volume 3.
        File.Delete(Path.Combine(bad, "backup.lzs.003"));
        t = SnapArchiver.Test(Path.Combine(bad, "backup.lzs.001"));
        Check(t.MissingVolumes.Contains(3), "missing volume 3 reported: " + string.Join("; ", t.Problems));
    }

    static byte[] FakeStub()
    {
        var s = new byte[10_000];
        new Rng(99).Fill(s);
        s[0] = (byte)'M';
        s[1] = (byte)'Z';
        // A real stub contains the format constants as data; make sure that does not confuse the reader.
        "LZSN"u8.CopyTo(s.AsSpan(5000));
        return s;
    }

    public static void SelfExtracting()
    {
        var dir = NewDir("sfx");
        var inputs = MakeVersions(dir, 6);
        var stub = FakeStub();
        var a = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "one"), "backup", o => o.SelfExtracting = true, stub));
        Check(a.Files.Count == 1 && a.Files[0].EndsWith("backup.exe"), "sfx single file");
        var exe = File.ReadAllBytes(a.Files[0]);
        Check(exe.AsSpan(0, stub.Length).SequenceEqual(stub), "stub at the start");
        Check(SnapArchiver.ReadInfo(a.Files[0]).SelfExtracting, "info says self-extracting");
        AssertRestores(a.Files[0], inputs, "sfx1");

        var b = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "multi"), "backup", o => { o.SelfExtracting = true; o.VolumeSize = 1 << 20; }, stub));
        Check(b.Files.Count >= 3 && b.Files[0].EndsWith("backup.exe") && b.Files[1].EndsWith("backup.lzs.002"), "sfx + volumes names");
        AssertRestores(b.Files[0], inputs, "sfx2");
        AssertRestores(b.Files[2], inputs, "sfx3");     // opened from part 3, finds backup.exe
    }

    /// <summary>Puts a finished archive into the state an interrupted run leaves behind.</summary>
    static void SimulateCrash(List<string> files, long prefix, bool sfx, long volSize, long cutLogical)
    {
        var info = SnapArchiver.ReadInfo(files[0]);
        var h = info.Header;
        h.Complete = false;
        h.VolumeCount = 0;
        h.LogicalLength = 0;
        h.FooterOffset = 0;
        var hb = h.Serialize();
        int k = volSize > 0 ? (int)((cutLogical - 1) / volSize) : 0;
        long inVol = cutLogical - k * volSize;
        for (int i = k + 1; i < files.Count; i++)
            File.Delete(files[i]);
        using (var fs = new FileStream(files[k], FileMode.Open))
            fs.SetLength((k == 0 ? prefix : 0) + inVol);
        if (k > 0 && sfx)
            using (var fs = new FileStream(files[0], FileMode.Open))
                fs.SetLength(prefix + volSize);   // drop the trailer
        using (var fs = new FileStream(files[0], FileMode.Open))
        {
            fs.Seek(prefix, SeekOrigin.Begin);
            fs.Write(hb);
        }
    }

    public static void ResumeAfterCrash()
    {
        var dir = NewDir("crash");
        var inputs = MakeVersions(dir, 6);
        var stub = FakeStub();
        foreach (var (sfx, vol) in new[] { (false, 0L), (false, 1L << 20), (true, 1L << 20), (true, 0L) })
        {
            var refDir = Path.Combine(dir, $"ref-{sfx}-{vol}");
            var refRes = SnapArchiver.Create(Job(inputs, refDir, "backup", o => { o.SelfExtracting = sfx; o.VolumeSize = vol; }, stub));
            var refBytes = ConcatFiles(refRes.Files);
            long logical = SnapArchiver.ReadInfo(refRes.Files[0]).Header.LogicalLength;
            long prefix = sfx ? VolumeLayout.AlignStub(stub.Length) : 0;
            var rng = new Rng((ulong)(vol + (sfx ? 1 : 0) + 5));
            var cuts = new List<long> { FormatConst.HeaderSize, FormatConst.HeaderSize + 50, logical - 10, logical / 2 };
            if (vol > 0) { cuts.Add(vol); cuts.Add(vol + 1); cuts.Add(2 * vol); }
            for (int i = 0; i < 4; i++) cuts.Add(FormatConst.HeaderSize + (long)(rng.Next() % (ulong)(logical - FormatConst.HeaderSize)));
            foreach (var cut in cuts)
            {
                var wd = Path.Combine(dir, $"w-{sfx}-{vol}-{cut}");
                Directory.CreateDirectory(wd);
                foreach (var f in refRes.Files)
                    File.Copy(f, Path.Combine(wd, Path.GetFileName(f)));
                var files = refRes.Files.Select(f => Path.Combine(wd, Path.GetFileName(f))).ToList();
                SimulateCrash(files, prefix, sfx, vol, cut);
                var job = Job(inputs, wd, "backup", o => { o.SelfExtracting = sfx; o.VolumeSize = vol; }, stub, resume: true);
                var ri = SnapArchiver.InspectResume(job);
                Check(ri.CanResume, $"inspect resume (cut {cut}): {ri.Reason}");
                var res = SnapArchiver.Create(job);
                Check(res.Completed, "resumed run completed");
                var got = ConcatFiles(res.Files);
                Check(got.AsSpan().SequenceEqual(refBytes), $"identical archive after resume (sfx={sfx}, vol={vol}, cut={cut})");
            }
            AssertRestores(refRes.Files[0], inputs, "crash");
        }
    }

    public static void CancelAndResume()
    {
        var dir = NewDir("cancel");
        var inputs = MakeVersions(dir, 4);
        var refRes = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "ref"), "backup"));
        var refBytes = File.ReadAllBytes(refRes.Files[0]);

        // Cancel immediately, then after the first finished version.
        foreach (int stopAfterVersion in new[] { -1, 0, 1 })
        {
            var wd = Path.Combine(dir, "w" + stopAfterVersion);
            using var cts = new CancellationTokenSource();
            var prog = new SyncProgress(p =>
            {
                if (stopAfterVersion < 0 || (p.Phase == Phase.Compressing && p.Version == stopAfterVersion && p.Done >= inputs.Take(stopAfterVersion + 1).Sum(f => new FileInfo(f).Length)))
                    cts.Cancel();
            });
            var r1 = SnapArchiver.Create(Job(inputs, wd, "backup"), prog, cts.Token);
            Check(!r1.Completed, "first run was cancelled");
            Check(SnapArchiver.InspectResume(Job(inputs, wd, "backup", resume: true)).CanResume, "can resume");
            var r2 = SnapArchiver.Create(Job(inputs, wd, "backup", resume: true));
            Check(r2.Completed, "second run completed");
            Check(File.ReadAllBytes(r2.Files[0]).AsSpan().SequenceEqual(refBytes), "identical to an uninterrupted run");
        }
    }

    sealed class SyncProgress : IProgress<ProgressInfo>
    {
        private readonly Action<ProgressInfo> _a;
        public SyncProgress(Action<ProgressInfo> a) => _a = a;
        public void Report(ProgressInfo value) => _a(value);
    }

    public static void UnicodeNames()
    {
        var dir = NewDir("ћирилица-šđčćž");
        var inputs = MakeVersions(dir, 2, new[] { "резервна-копија-1.img", "Šđčćž – druga.img", "трећа ЖЂЋЧ.img" });
        var res = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "излаз"), "бекап-šđčćž", o => o.VolumeSize = 1 << 20));
        Check(Path.GetFileName(res.Files[0]) == "бекап-šđčćž.lzs.001", "unicode archive name");
        AssertRestores(res.Files[^1], inputs, "unicode");
        var info = SnapArchiver.ReadInfo(res.Files[0]);
        Check(info.Header.Versions[0].Name == "резервна-копија-1.img", "stored name");
    }

    public static void RestoreLastOnly()
    {
        var dir = NewDir("last");
        var inputs = MakeVersions(dir, 3);
        var res = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "a"), "backup"));
        var dest = Path.Combine(dir, "dest");
        var rr = SnapArchiver.Restore(res.Files[0], dest, new[] { 2 });
        Check(rr.Files.Count == 1 && Path.GetFileName(rr.Files[0]) == "v2-flat.vmdk", "only the last version");
        Check(Directory.GetFileSystemEntries(dest).Length == 1, "no temporary files left: " + string.Join(", ", Directory.GetFileSystemEntries(dest)));
        Check(File.ReadAllBytes(rr.Files[0]).AsSpan().SequenceEqual(File.ReadAllBytes(inputs[2])), "content");
    }

    public static void VerifyDetectsChange()
    {
        var dir = NewDir("verify");
        var inputs = MakeVersions(dir, 2);
        var res = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "a"), "backup"));
        Check(SnapArchiver.VerifyAgainstOriginals(res.Files[0], inputs).Count == 0, "clean verify");
        var b = File.ReadAllBytes(inputs[1]);
        b[12345] ^= 0xFF;
        File.WriteAllBytes(inputs[1], b);
        Check(SnapArchiver.VerifyAgainstOriginals(res.Files[0], inputs).Count > 0, "change detected");
    }

    public static void OddSizes()
    {
        var dir = NewDir("odd");
        var r = new Rng(3);
        var sizes = new[] { 5000, 0, 1, 4096 * 300 + 17, 4095, 2 << 20 };
        var inputs = new string[sizes.Length];
        byte[] prev = Array.Empty<byte>();
        for (int i = 0; i < sizes.Length; i++)
        {
            var d = new byte[sizes[i]];
            int keep = Math.Min(prev.Length, d.Length);
            prev.AsSpan(0, keep).CopyTo(d);
            r.Fill(d.AsSpan(keep));
            if (d.Length > 100) d[d.Length / 3] ^= 1;
            inputs[i] = Path.Combine(dir, $"img{i}.raw");
            File.WriteAllBytes(inputs[i], d);
            prev = d;
        }
        foreach (bool motion in new[] { false, true })
        {
            var res = SnapArchiver.Create(Job(inputs, Path.Combine(dir, "a" + motion), "backup", o => o.Motion = motion));
            AssertRestores(res.Files[0], inputs, "odd");
        }
    }

    public static void ResumeRefusesMismatch()
    {
        var dir = NewDir("mismatch");
        var inputs = MakeVersions(dir, 2);
        var wd = Path.Combine(dir, "w");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r1 = SnapArchiver.Create(Job(inputs, wd, "backup"), null, cts.Token);
        Check(!r1.Completed, "cancelled");
        Check(!SnapArchiver.InspectResume(Job(inputs, wd, "backup", o => o.Preset = 2, resume: true)).CanResume, "different preset refused");
        File.SetLastWriteTimeUtc(inputs[0], DateTime.UtcNow);
        var ri = SnapArchiver.InspectResume(Job(inputs, wd, "backup", resume: true));
        Check(!ri.CanResume && ri.Reason.Contains("not the same file"), "changed input refused: " + ri.Reason);
    }
}
