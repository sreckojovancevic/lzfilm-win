using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LzFilm.Core;

/// <summary>
/// Chain of versions of large images (disk images, VM snapshots): version i is coded against
/// version i−1 and compressed with LZMA. Entry point for backup, restore, test and verification.
/// </summary>
public static class SnapArchiver
{
    private const int ReadChunk = 8 << 20;

    // ------------------------------------------------------------------------------------- backup

    /// <summary>Estimated peak memory of a backup (bytes).</summary>
    /// <param name="dataBytes">Files archive: total size of the files, when known; small inputs need fewer buffers and encoders.</param>
    public static long EstimateMemory(BackupOptions o, long largestVersion, bool filesArchive = false, long dataBytes = -1)
    {
        long seg = (long)o.SegmentSizeMiB << 20;
        long enc = SegmentCodec.MaxEncodedLength((int)seg, FormatConst.DefaultBlockSize);
        // Files archive: the input buffer plus the part of the output buffer that is actually written.
        long perSlot = filesArchive ? seg + seg * 3 / 4 + (1 << 20) : 2 * seg + enc + enc + 64 * 1024;
        long slots = o.Threads + 2;
        long workers = o.Threads;
        if (filesArchive && dataBytes >= 0)
        {
            long segments = Math.Max(1, (dataBytes + seg - 1) / seg);
            slots = Math.Min(slots, segments);
            workers = Math.Min(workers, segments);
            perSlot = Math.Min(perSlot, Math.Max(dataBytes, 1 << 20) * 7 / 4 + (1 << 20));
        }
        long encoders = workers * SafeEncoderMemory(o.Preset);
        long index = o.Motion ? 2 * MotionIndex.EstimateBytes(largestVersion, FormatConst.DefaultBlockSize, o.MotionIndexMemoryLimit) : 0;
        return slots * perSlot + encoders + index + (64L << 20);
    }

    private static long SafeEncoderMemory(int preset)
    {
        try { return Xz.EncoderMemory(preset); }
        catch (DllNotFoundException) { return preset >= 7 ? 700L << 20 : 100L << 20; }
    }

    private static void Validate(BackupJob job)
    {
        var o = job.Options;
        if (job.Inputs.Count == 0)
            throw new ArgumentException("Add at least one version.");
        foreach (var p in job.Inputs)
            if (!File.Exists(p))
                throw new FileNotFoundException("Input not found.", p);
        if (job.Inputs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Inputs.Count)
            throw new ArgumentException("The same file was added more than once.");
        if (string.IsNullOrWhiteSpace(job.BaseName) || job.BaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid archive name.");
        if (o.Preset is < 0 or > 9)
            throw new ArgumentException("Preset must be 0–9.");
        if (o.Threads is < 1 or > 64)
            throw new ArgumentException("Threads must be 1–64.");
        if (o.SegmentSizeMiB is < 1 or > 1024)
            throw new ArgumentException("Segment size must be 1–1024 MiB.");
        if (o.VolumeSize != 0 && o.VolumeSize < (1L << 20))
            throw new ArgumentException("Volume size must be at least 1 MiB.");
        if (o.SelfExtracting && (job.SfxStub == null || job.SfxStub.Length == 0))
            throw new ArgumentException("The self-extractor is not available in this build.");
        foreach (var p in job.Inputs)
            if (Path.GetFullPath(p).StartsWith(Path.GetFullPath(Path.Combine(job.OutputDirectory, job.BaseName)) + ".", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An input file is part of the output archive.");
    }

    private static List<VersionEntry> Describe(IReadOnlyList<string> inputs) =>
        inputs.Select(p =>
        {
            var fi = new FileInfo(p);
            return new VersionEntry { Name = fi.Name, Size = fi.Length, LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks };
        }).ToList();

    private sealed class ResumeState
    {
        public int NextVersion;
        public long SegmentsDoneInNext;
        public long BytesDone;
        public Footer Footer = new();
        public long LogicalLength;
        public long Prefix;
        public ArchiveHeader? Header;
    }

    /// <summary>Checks whether an unfinished archive with the same name and settings can be continued.</summary>
    public static ResumeInfo InspectResume(BackupJob job) => InspectResume(SnapCore(job));

    internal static ResumeInfo InspectResume(CoreJob job)
    {
        try
        {
            var layout = job.Layout;
            if (!File.Exists(layout.PathOf(0)))
                return new ResumeInfo { Reason = "No previous archive." };
            var st = ScanExisting(job, layout, job.Entries, verifyPayloads: false, null, CancellationToken.None);
            return new ResumeInfo
            {
                CanResume = true,
                Reason = "An unfinished backup with the same settings exists.",
                VersionsDone = st.NextVersion,
                BytesDone = st.BytesDone,
                BytesTotal = job.Entries.Sum(e => e.Size),
            };
        }
        catch (Exception e)
        {
            return new ResumeInfo { Reason = e.Message };
        }
    }

    private static ResumeState ScanExisting(CoreJob job, VolumeLayout layout, List<VersionEntry> entries,
        bool verifyPayloads, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var o = job.Options;
        using var r = ArchiveReader.Open(layout.PathOf(0), allowIncomplete: true);
        var h = r.Header;
        if (h.Complete)
            throw new InvalidOperationException("An archive with this name is already complete.");
        string? why =
            h.Files != job.Files ? "archive type differs" :
            h.Motion != (!job.Files && o.Motion) ? "motion setting differs" :
            h.SegmentSize != (o.SegmentSizeMiB << 20) ? "segment size differs" :
            h.Preset != o.Preset ? "preset differs" :
            h.VolumeSize != o.VolumeSize ? "volume size differs" :
            r.Layout.SelfExtracting != o.SelfExtracting ? "self-extracting setting differs" :
            h.BaseName != job.BaseName ? "archive name differs" :
            h.Versions.Count != entries.Count ? "number of versions differs" : null;
        for (int i = 0; why == null && i < entries.Count; i++)
        {
            var a = h.Versions[i];
            var b = entries[i];
            if (a.Name != b.Name || a.Size != b.Size || a.LastWriteUtcTicks != b.LastWriteUtcTicks)
                why = job.Files ? "the files to pack have changed" : $"version {i + 1} ({b.Name}) is not the same file as before";
        }
        if (why != null)
            throw new InvalidOperationException("Cannot resume: " + why + ".");

        var st = new ResumeState { Prefix = r.Prefix, Header = h };
        long seg = h.SegmentSize;
        long off = FormatConst.HeaderSize;
        long avail = r.AvailableLength;
        int v = 0;
        long s = 0;
        var head = new byte[FormatConst.SegmentRecordSize];
        byte[] payload = Array.Empty<byte>();
        var last = Stopwatch.StartNew();
        while (v < entries.Count && off + 4 <= avail)
        {
            ct.ThrowIfCancellationRequested();
            long vsize = entries[v].Size;
            long segCount = (vsize + seg - 1) / seg;
            if (s == segCount)
            {
                if (off + FormatConst.VersionEndRecordSize > avail)
                    break;
                var vb = head.AsSpan(0, FormatConst.VersionEndRecordSize);
                r.Read(off, vb);
                if (!VersionEndRecord.TryParse(vb, out var ve) || ve.Version != v || ve.SegmentCount != segCount || ve.Size != vsize)
                    break;
                st.Footer.Versions.Add(new FooterVersion { Size = vsize, SegmentCount = segCount, Sha256 = ve.Sha256 });
                off += FormatConst.VersionEndRecordSize;
                v++;
                s = 0;
                continue;
            }
            if (off + FormatConst.SegmentRecordSize > avail)
                break;
            r.Read(off, head);
            if (!SegmentRecord.TryParse(head, out var rec) || rec.Version != v || rec.Index != s ||
                rec.RawOffset != s * seg || rec.RawLength != (int)Math.Min(seg, vsize - s * seg) ||
                off + FormatConst.SegmentRecordSize + rec.CompressedLength > avail)
                break;
            if (verifyPayloads)
            {
                if (payload.Length < rec.CompressedLength)
                    payload = new byte[rec.CompressedLength];
                var pl = payload.AsSpan(0, (int)rec.CompressedLength);
                r.Read(off + FormatConst.SegmentRecordSize, pl);
                if (!SHA256.HashData(pl).AsSpan().SequenceEqual(rec.PayloadSha256))
                    break;
            }
            st.Footer.Segments.Add(new FooterSegment { Version = v, Index = s, RecordOffset = off, CompressedLength = rec.CompressedLength });
            st.Footer.Stats.Add(rec.Stats);
            off += FormatConst.SegmentRecordSize + rec.CompressedLength;
            s++;
            if (progress != null && last.ElapsedMilliseconds > 200)
            {
                last.Restart();
                progress.Report(new ProgressInfo { Phase = Phase.CheckingExisting, Done = off, Total = avail, VersionCount = entries.Count, Version = v });
            }
        }
        st.NextVersion = v;
        st.SegmentsDoneInNext = s;
        st.LogicalLength = off;
        st.BytesDone = entries.Take(v).Sum(e => e.Size) + (v < entries.Count ? Math.Min(entries[v].Size, s * seg) : 0);
        return st;
    }

    private sealed class Slot
    {
        public byte[] Cur = Array.Empty<byte>(), Prev = Array.Empty<byte>(), Res = Array.Empty<byte>(), Comp = Array.Empty<byte>();
        public long Index, RawOffset;
        public int RawLength, EncodedLength, CompressedLength, Kind;
        public BlockStats Stats;
        public readonly byte[] Sha = new byte[32];
        public readonly ManualResetEventSlim Done = new(false);
        public Exception? Error;

        /// <param name="chained">Coded against a previous version (needs the reference and residual buffers).</param>
        public void Ensure(int seg, int block, bool chained)
        {
            if (Cur.Length < seg)
                Cur = new byte[seg];
            if (chained)
            {
                if (Prev.Length < seg)
                    Prev = new byte[seg];
                int enc = SegmentCodec.MaxEncodedLength(seg, block);
                if (Res.Length < enc)
                    Res = new byte[enc];
                if (Comp.Length < Xz.Bound(enc))
                    Comp = new byte[Xz.Bound(enc)];
            }
            else if (Comp.Length < Xz.Bound(seg))
            {
                Comp = new byte[Xz.Bound(seg)];
            }
        }
    }

    /// <summary>Creates (or resumes) an archive. Returns with Completed=false if cancelled; the archive can then be resumed.</summary>
    public static BackupResult Create(BackupJob job, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Validate(job);
        return CreateCore(SnapCore(job), progress, ct);
    }

    internal static CoreJob SnapCore(BackupJob job) => new()
    {
        Entries = Describe(job.Inputs),
        Open = v => new FileSource(job.Inputs[v]),
        SnapInputs = job.Inputs,
        OutputDirectory = job.OutputDirectory,
        BaseName = job.BaseName,
        Options = job.Options,
        SfxStub = job.SfxStub,
        Resume = job.Resume,
    };

    internal static BackupResult CreateCore(CoreJob job, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!Xz.IsAvailable)
            throw new DllNotFoundException("liblzma was not found (liblzma.dll must be next to the program).");
        var sw = Stopwatch.StartNew();
        var o = job.Options;
        const int B = FormatConst.DefaultBlockSize;
        int seg = o.SegmentSizeMiB << 20;
        var entries = job.Entries;
        int n = entries.Count;
        bool filesMode = job.Files;
        long totalBytes = entries.Sum(e => e.Size);
        var layout = job.Layout;

        ArchiveHeader header;
        VolumeWriter writer;
        ResumeState state;
        progress?.Report(new ProgressInfo { Phase = Phase.Preparing, VersionCount = n, Total = totalBytes });
        if (job.Resume)
        {
            state = ScanExisting(job, layout, entries, verifyPayloads: true, progress, ct);
            header = state.Header!;
            writer = VolumeWriter.OpenForResume(layout, state.Prefix, state.LogicalLength);
        }
        else
        {
            header = new ArchiveHeader
            {
                Motion = !filesMode && o.Motion,
                Files = filesMode,
                SegmentSize = seg,
                Preset = o.Preset,
                VolumeSize = o.VolumeSize,
                ArchiveId = o.ArchiveId ?? Guid.NewGuid(),
                BaseName = job.BaseName,
            };
            header.Versions.AddRange(entries);
            var headerBytes = header.Serialize();
            writer = VolumeWriter.Create(layout, job.SfxStub);
            writer.Write(headerBytes);
            writer.Flush(true);
            state = new ResumeState();
        }

        var footer = state.Footer;
        long doneBytes = state.BytesDone;
        long runStartBytes = doneBytes;
        bool truncated = false;
        var slots = Enumerable.Range(0, o.Threads + 2).Select(_ => new Slot()).ToArray();
        MotionIndex? carried = null;
        var last = Stopwatch.StartNew();

        void Report(Phase ph, int v, long done, long total, string? msg = null, bool force = false)
        {
            if (progress == null || (!force && last.ElapsedMilliseconds < 200))
                return;
            last.Restart();
            progress.Report(new ProgressInfo { Phase = ph, Version = v, VersionCount = n, Done = done, Total = total, ArchiveBytes = writer.Position, Message = msg });
        }

        BackupResult Cancelled() => new()
        {
            Completed = false,
            Resumed = job.Resume,
            InputBytes = totalBytes,
            ArchiveBytes = writer.Position,
            Stats = footer.Stats,
            Elapsed = sw.Elapsed,
            Files = Enumerable.Range(0, writer.VolumeCount).Select(layout.PathOf).ToList(),
        };

        using (writer)
        {
          try
          {
            for (int v = state.NextVersion; v < n; v++)
            {
                long size = entries[v].Size;
                long segCount = (size + seg - 1) / seg;
                long s0 = v == state.NextVersion ? state.SegmentsDoneInNext : 0;
                // A files archive has independent versions (data, file table); a snapshot chain codes
                // every version against the previous one.
                bool chained = !filesMode && v > 0;
                long prevLen = chained ? entries[v - 1].Size : 0;
                bool motionHere = chained && o.Motion;

                using var cur = job.Open(v);
                using var prevH = chained ? File.OpenHandle(job.SnapInputs![v - 1], FileMode.Open, FileAccess.Read, FileShare.Read) : null;

                MotionIndex? prevIdx = null;
                if (motionHere)
                {
                    prevIdx = carried ?? BuildIndex(prevH!, prevLen, B, o.MotionIndexMemoryLimit, p => Report(Phase.Indexing, v, p, prevLen), ct);
                    truncated |= prevIdx.Truncated;
                }
                carried = null;
                var buildIdx = !filesMode && o.Motion && v < n - 1 ? new MotionIndex(size / B, o.MotionIndexMemoryLimit) : null;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                if (s0 > 0)
                {
                    long upto = Math.Min(size, s0 * seg);
                    var buf = new byte[ReadChunk];
                    for (long off = 0; off < upto; off += ReadChunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        int k = (int)Math.Min(ReadChunk, upto - off);
                        cur.ReadExact(buf.AsSpan(0, k), off);
                        sha.AppendData(buf, 0, k);
                        buildIdx?.AddBuffer(buf.AsSpan(0, k), off / B, B);
                        Report(Phase.Rereading, v, off + k, upto);
                    }
                }

                // Pipeline: reader → workers (residual + xz) → this thread writes records in order.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tk = linked.Token;
                var free = new BlockingCollection<Slot>();
                foreach (var sl in slots)
                    free.Add(sl);
                var work = new BlockingCollection<Slot>();
                var order = new BlockingCollection<Slot>();
                Exception? readerError = null;
                int preset = o.Preset;
                string prevPath = chained ? job.SnapInputs![v - 1] : "";

                var reader = new Thread(() =>
                {
                    try
                    {
                        for (long s = s0; s < segCount; s++)
                        {
                            var slot = free.Take(tk);
                            slot.Ensure(seg, B, chained);
                            long off = s * seg;
                            int len = (int)Math.Min(seg, size - off);
                            cur.ReadExact(slot.Cur.AsSpan(0, len), off);
                            int m = prevLen > off ? (int)Math.Min(len, prevLen - off) : 0;
                            if (m > 0)
                                ReadExact(prevH!, slot.Prev.AsSpan(0, m), off, prevPath);
                            if (chained)
                                slot.Prev.AsSpan(m, len - m).Clear();
                            sha.AppendData(slot.Cur, 0, len);
                            buildIdx?.AddBuffer(slot.Cur.AsSpan(0, len), off / B, B);
                            slot.Index = s;
                            slot.RawOffset = off;
                            slot.RawLength = len;
                            slot.Error = null;
                            slot.Done.Reset();
                            order.Add(slot, tk);
                            work.Add(slot, tk);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        readerError = e;
                        linked.Cancel();
                    }
                    finally
                    {
                        work.CompleteAdding();
                        order.CompleteAdding();
                    }
                }) { IsBackground = true, Name = "lzfilm-reader" };

                var workers = Enumerable.Range(0, o.Threads).Select(_ => new Thread(() =>
                {
                    var scratch = new byte[B];
                    try
                    {
                        foreach (var slot in work.GetConsumingEnumerable(tk))
                        {
                            try
                            {
                                var st = default(BlockStats);
                                slot.Kind = motionHere ? FormatConst.KindMotion : FormatConst.KindXor;
                                if (!chained)
                                {
                                    // No reference: the residual is the data itself (XOR with zeros), compress it directly.
                                    slot.EncodedLength = slot.RawLength;
                                    slot.Stats = st;
                                    slot.CompressedLength = Xz.Compress(slot.Cur.AsSpan(0, slot.RawLength), slot.Comp, preset);
                                }
                                else
                                {
                                    slot.EncodedLength = SegmentCodec.Encode(slot.Cur.AsSpan(0, slot.RawLength), slot.Prev.AsSpan(0, slot.RawLength),
                                        slot.Res, motionHere, slot.RawOffset, B, prevIdx, prevH, prevLen, scratch, ref st);
                                    slot.Stats = st;
                                    slot.CompressedLength = Xz.Compress(slot.Res.AsSpan(0, slot.EncodedLength), slot.Comp, preset);
                                }
                                SHA256.HashData(slot.Comp.AsSpan(0, slot.CompressedLength), slot.Sha);
                            }
                            catch (Exception e)
                            {
                                slot.Error = e;
                            }
                            finally
                            {
                                slot.Done.Set();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }) { IsBackground = true, Name = "lzfilm-worker" }).ToList();

                reader.Start();
                workers.ForEach(t => t.Start());
                bool cancelled = false;
                long writtenSegments = 0;
                var recHead = new byte[FormatConst.SegmentRecordSize];
                try
                {
                    foreach (var slot in order.GetConsumingEnumerable(tk))
                    {
                        slot.Done.Wait(tk);
                        if (slot.Error != null)
                            throw new IOException($"Compressing version {v + 1}, segment {slot.Index + 1} failed: {slot.Error.Message}", slot.Error);
                        var rec = new SegmentRecord
                        {
                            Version = v,
                            Index = slot.Index,
                            RawOffset = slot.RawOffset,
                            RawLength = slot.RawLength,
                            EncodedLength = slot.EncodedLength,
                            CompressedLength = slot.CompressedLength,
                            Kind = slot.Kind,
                            Stats = slot.Stats,
                            PayloadSha256 = slot.Sha,
                        };
                        long recOff = writer.Position;
                        rec.Write(recHead);
                        writer.Write(recHead);
                        writer.Write(slot.Comp.AsSpan(0, slot.CompressedLength));
                        writer.Flush(false);
                        footer.Segments.Add(new FooterSegment { Version = v, Index = slot.Index, RecordOffset = recOff, CompressedLength = slot.CompressedLength });
                        footer.Stats.Add(slot.Stats);
                        doneBytes += slot.RawLength;
                        writtenSegments++;
                        free.Add(slot);
                        Report(Phase.Compressing, v, doneBytes, totalBytes);
                    }
                }
                catch (OperationCanceledException) when (readerError == null && ct.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (OperationCanceledException) when (readerError != null)
                {
                }
                finally
                {
                    linked.Cancel();
                    reader.Join();
                    workers.ForEach(t => t.Join());
                    writer.Flush(true);
                }
                if (readerError != null)
                    throw new IOException(readerError.Message, readerError);
                // The reader may finish (and close the queues) because of the cancellation before the
                // writer notices it, so completeness is checked explicitly before the version is closed.
                if (cancelled || writtenSegments != segCount - s0)
                {
                    if (ct.IsCancellationRequested)
                        return Cancelled();
                    throw new IOException($"Version {v + 1}: not all segments were written.");
                }

                var hash = sha.GetHashAndReset();
                var ve = new VersionEndRecord { Version = v, Size = size, SegmentCount = segCount, Sha256 = hash };
                var vb = new byte[FormatConst.VersionEndRecordSize];
                ve.Write(vb);
                writer.Write(vb);
                writer.Flush(true);
                footer.Versions.Add(new FooterVersion { Size = size, SegmentCount = segCount, Sha256 = hash });
                if (buildIdx != null)
                    truncated |= buildIdx.Truncated;
                carried = buildIdx;
                Report(Phase.Compressing, v, doneBytes, totalBytes, force: true);
            }
          }
          catch (OperationCanceledException) when (ct.IsCancellationRequested)
          {
            writer.Flush(true);
            return Cancelled();
          }

            Report(Phase.Finalizing, n - 1, totalBytes, totalBytes, force: true);
            header.FooterOffset = writer.Position;
            writer.Write(footer.Serialize());
            header.Complete = true;
            header.LogicalLength = writer.Position;
            header.VolumeCount = writer.VolumeCount;
            writer.Flush(true);
            writer.PatchVolume1(0, header.Serialize());
            writer.Flush(true);
            if (layout.SelfExtracting)
                writer.WriteSfxTrailer();

            var files = Enumerable.Range(0, writer.VolumeCount).Select(layout.PathOf).ToList();
            slots = Array.Empty<Slot>();
            carried = null;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return new BackupResult
            {
                Completed = true,
                Resumed = job.Resume,
                InputBytes = totalBytes,
                ArchiveBytes = files.Sum(f => new FileInfo(f).Length),
                Stats = footer.Stats,
                MotionIndexTruncated = truncated,
                Elapsed = sw.Elapsed,
                Files = files,
            };
        }
    }

    private static void ReadExact(SafeFileHandle h, Span<byte> buf, long offset, string path)
    {
        if (SegmentCodec.ReadAt(h, buf, offset) != buf.Length)
            throw new IOException($"{Path.GetFileName(path)} became shorter while it was being read (was it modified during the backup?).");
    }

    private static MotionIndex BuildIndex(SafeFileHandle h, long length, int block, long memLimit, Action<long> onProgress, CancellationToken ct)
    {
        var idx = new MotionIndex(length / block, memLimit);
        var buf = new byte[ReadChunk];
        for (long off = 0; off < length; off += ReadChunk)
        {
            ct.ThrowIfCancellationRequested();
            int k = (int)Math.Min(ReadChunk, length - off);
            if (SegmentCodec.ReadAt(h, buf.AsSpan(0, k), off) != k)
                throw new IOException("The previous version became shorter while it was being read.");
            idx.AddBuffer(buf.AsSpan(0, k), off / block, block);
            onProgress(off + k);
        }
        return idx;
    }

    // ------------------------------------------------------------------------------------ decode

    /// <summary>Write-only stream that compares everything written with a file.</summary>
    private sealed class CompareStream : Stream
    {
        private readonly FileStream _f;
        private byte[] _buf = new byte[1 << 20];
        public long FirstDifference { get; private set; } = -1;
        private long _pos;

        public CompareStream(string path) => _f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 << 20);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, _buf.Length);
                int got = 0;
                while (got < n)
                {
                    int k = _f.Read(_buf, got, n - got);
                    if (k == 0)
                        break;
                    got += k;
                }
                if (FirstDifference < 0)
                {
                    int same = data[..got].CommonPrefixLength(_buf.AsSpan(0, got));
                    if (same < got || got < n)
                        FirstDifference = _pos + same;
                }
                _pos += n;
                data = data[n..];
            }
        }

        public override long Length => _pos;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _f.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class DecodedSegment
    {
        public SegmentRecord Record;
        public byte[] Res = Array.Empty<byte>();
        public byte[] Comp = Array.Empty<byte>();
        public Exception? Error;
    }

    /// <summary>
    /// Decodes one version, optionally writing it to <paramref name="output"/>, and returns its SHA-256.
    /// Decompression runs one thread ahead of reconstruction.
    /// </summary>
    private static byte[] DecodeVersion(ArchiveReader r, int v, SafeFileHandle? prevH, long prevLen, Stream? output,
        Action<long> onBytes, CancellationToken ct) =>
        DecodeSegments(r, v, prevH, prevLen, null, output == null ? null : (_, d) => output.Write(d), onBytes, ct)!;

    internal delegate void SegmentSink(long rawOffset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Decodes the segments of a version in order (all of them, or those whose index <paramref name="want"/>
    /// accepts; only possible without a reference version). Returns the SHA-256 of the version when all
    /// segments were decoded, otherwise null (each segment is still checked with SHA-256 and CRC64).
    /// </summary>
    internal static byte[]? DecodeSegments(ArchiveReader r, int v, SafeFileHandle? prevH, long prevLen, Func<long, bool>? want,
        SegmentSink? sink, Action<long> onBytes, CancellationToken ct)
    {
        if (want != null && prevLen > 0)
            throw new InvalidOperationException("Partial decoding needs independent segments.");
        var h = r.Header;
        var f = r.Footer!;
        int B = h.BlockSize;
        int seg = h.SegmentSize;
        var fv = f.Versions[v];
        var segs = f.Segments.Where(s => s.Version == v).ToList();
        if (segs.Count != fv.SegmentCount)
            throw new InvalidDataException($"Version {v + 1}: the archive index is inconsistent.");
        for (int i = 0; i < segs.Count; i++)
            if (segs[i].Index != i)
                throw new InvalidDataException($"Version {v + 1}: the archive index is inconsistent.");
        if (want != null)
            segs = segs.Where(e => want(e.Index)).ToList();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tk = linked.Token;
        var free = new BlockingCollection<DecodedSegment>();
        for (int i = 0; i < 2; i++)
            free.Add(new DecodedSegment());
        var ready = new BlockingCollection<DecodedSegment>();
        var producer = new Thread(() =>
        {
            var head = new byte[FormatConst.SegmentRecordSize];
            try
            {
                foreach (var e in segs)
                {
                    var d = free.Take(tk);
                    d.Error = null;
                    try
                    {
                        r.Read(e.RecordOffset, head);
                        if (!SegmentRecord.TryParse(head, out var rec) || rec.Version != v || rec.Index != e.Index || rec.CompressedLength != e.CompressedLength)
                            throw new InvalidDataException($"Version {v + 1}, segment {e.Index + 1}: record header is damaged (volume {r.Layout.VolumeOf(e.RecordOffset) + 1}).");
                        if (d.Comp.Length < rec.CompressedLength)
                            d.Comp = new byte[rec.CompressedLength];
                        var comp = d.Comp.AsSpan(0, (int)rec.CompressedLength);
                        r.Read(e.RecordOffset + FormatConst.SegmentRecordSize, comp);
                        if (!SHA256.HashData(comp).AsSpan().SequenceEqual(rec.PayloadSha256))
                            throw new InvalidDataException($"Version {v + 1}, segment {e.Index + 1}: data is damaged (volume {r.Layout.VolumeOf(e.RecordOffset + FormatConst.SegmentRecordSize) + 1}).");
                        if (d.Res.Length < rec.EncodedLength)
                            d.Res = new byte[rec.EncodedLength];
                        Xz.Decompress(comp, d.Res.AsSpan(0, rec.EncodedLength));
                        d.Record = rec;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        d.Error = ex;
                    }
                    ready.Add(d, tk);
                    if (d.Error != null)
                        break;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                ready.CompleteAdding();
            }
        }) { IsBackground = true, Name = "lzfilm-decoder" };

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var prev = new byte[seg];
        var outBuf = new byte[seg];
        var scratch = new byte[B];
        long written = 0;
        producer.Start();
        try
        {
            foreach (var d in ready.GetConsumingEnumerable(tk))
            {
                if (d.Error != null)
                    throw d.Error;
                var rec = d.Record;
                if (rec.RawOffset != rec.Index * seg || (want == null && rec.RawOffset != written) || rec.RawLength > seg)
                    throw new InvalidDataException($"Version {v + 1}: segments are out of order.");
                int len = rec.RawLength;
                int m = prevLen > rec.RawOffset ? (int)Math.Min(len, prevLen - rec.RawOffset) : 0;
                if (m > 0 && SegmentCodec.ReadAt(prevH!, prev.AsSpan(0, m), rec.RawOffset) != m)
                    throw new IOException("The previous version is shorter than expected.");
                prev.AsSpan(m, len - m).Clear();
                SegmentCodec.Decode(d.Res.AsSpan(0, rec.EncodedLength), prev.AsSpan(0, len), outBuf.AsSpan(0, len),
                    rec.Kind, B, prevH, prevLen, scratch);
                sink?.Invoke(rec.RawOffset, outBuf.AsSpan(0, len));
                sha.AppendData(outBuf, 0, len);
                written += len;
                free.Add(d);
                onBytes(len);
            }
        }
        finally
        {
            linked.Cancel();
            producer.Join();
        }
        ct.ThrowIfCancellationRequested();
        if (want != null)
            return null;
        if (written != fv.Size)
            throw new InvalidDataException($"Version {v + 1}: decoded size does not match.");
        return sha.GetHashAndReset();
    }

    private static string SafeFileName(string name, int v, HashSet<string> used)
    {
        var n = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(n) || n == "." || n == "..")
            n = $"version{v + 1}.img";
        if (!used.Add(n))
        {
            n = $"{v + 1}_{n}";
            used.Add(n);
        }
        return n;
    }

    /// <summary>
    /// Restores versions into a folder. Versions that are needed only as a reference for later ones
    /// are decoded into a temporary file and deleted. Every restored version is verified with SHA-256.
    /// </summary>
    public static RestoreResult Restore(string archivePath, string destinationDirectory, IReadOnlyCollection<int>? versions = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!Xz.IsAvailable)
            throw new DllNotFoundException("liblzma was not found (liblzma.dll must be next to the program).");
        var sw = Stopwatch.StartNew();
        using var r = ArchiveReader.Open(archivePath);
        var h = r.Header;
        if (h.Files)
            throw new InvalidOperationException("This is a files-and-folders archive; use FolderArchiver.Extract.");
        int n = h.Versions.Count;
        var want = (versions ?? Enumerable.Range(0, n).ToList()).ToHashSet();
        if (want.Count == 0)
            throw new ArgumentException("Select at least one version.");
        if (want.Any(i => i < 0 || i >= n))
            throw new ArgumentOutOfRangeException(nameof(versions));
        int maxV = want.Max();
        Directory.CreateDirectory(destinationDirectory);
        string tmpDir = Path.Combine(destinationDirectory, ".lzfilm-tmp-" + h.ArchiveId.ToString("N")[..8]);
        long total = h.Versions.Take(maxV + 1).Sum(x => x.Size);
        long done = 0;
        var last = Stopwatch.StartNew();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new RestoreResult();
        string? prevPath = null;
        bool prevTemp = false;
        string? partial = null;
        try
        {
            for (int v = 0; v <= maxV; v++)
            {
                bool keep = want.Contains(v);
                string finalPath;
                if (keep)
                {
                    finalPath = Path.Combine(destinationDirectory, SafeFileName(h.Versions[v].Name, v, used));
                }
                else
                {
                    Directory.CreateDirectory(tmpDir);
                    finalPath = Path.Combine(tmpDir, $"v{v + 1}.tmp");
                }
                partial = finalPath + ".partial";
                byte[] got;
                using (var prevH = prevPath != null ? File.OpenHandle(prevPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                using (var outS = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20))
                {
                    outS.SetLength(h.Versions[v].Size);
                    int vv = v;
                    got = DecodeVersion(r, v, prevH, v > 0 ? h.Versions[v - 1].Size : 0, outS, k =>
                    {
                        done += k;
                        if (progress != null && last.ElapsedMilliseconds > 200)
                        {
                            last.Restart();
                            progress.Report(new ProgressInfo { Phase = Phase.Restoring, Version = vv, VersionCount = n, Done = done, Total = total });
                        }
                    }, ct);
                    outS.Flush(true);
                }
                if (!got.AsSpan().SequenceEqual(r.Footer!.Versions[v].Sha256))
                    throw new InvalidDataException($"Version {v + 1} ({h.Versions[v].Name}) failed verification (SHA-256 mismatch).");
                File.Move(partial, finalPath, overwrite: true);
                partial = null;
                if (prevTemp && prevPath != null)
                    File.Delete(prevPath);
                prevPath = finalPath;
                prevTemp = !keep;
                if (keep)
                    result.Files.Add(finalPath);
            }
            if (prevTemp && prevPath != null)
                File.Delete(prevPath);
        }
        finally
        {
            if (partial != null && File.Exists(partial))
                File.Delete(partial);
            if (Directory.Exists(tmpDir))
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch (IOException) { }
            }
        }
        progress?.Report(new ProgressInfo { Phase = Phase.Done, VersionCount = n, Done = total, Total = total });
        return new RestoreResult { Files = result.Files, Elapsed = sw.Elapsed };
    }

    /// <summary>
    /// Checks the archive against the original files without writing anything: version i is
    /// rebuilt using original i−1 as the reference and compared with the stored SHA-256.
    /// </summary>
    public static List<string> VerifyAgainstOriginals(string archivePath, IReadOnlyList<string> inputs,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        using var r = ArchiveReader.Open(archivePath);
        var h = r.Header;
        if (h.Files)
            throw new InvalidOperationException("This is a files-and-folders archive; use FolderArchiver.Verify.");
        int n = h.Versions.Count;
        if (inputs.Count != n)
            throw new ArgumentException("The number of original files does not match the archive.");
        long total = h.Versions.Sum(x => x.Size);
        long done = 0;
        var last = Stopwatch.StartNew();
        for (int v = 0; v < n; v++)
        {
            using var prevH = v > 0 ? File.OpenHandle(inputs[v - 1], FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            using var cmp = new CompareStream(inputs[v]);
            int vv = v;
            var got = DecodeVersion(r, v, prevH, v > 0 ? h.Versions[v - 1].Size : 0, cmp, k =>
            {
                done += k;
                if (progress != null && last.ElapsedMilliseconds > 200)
                {
                    last.Restart();
                    progress.Report(new ProgressInfo { Phase = Phase.Verifying, Version = vv, VersionCount = n, Done = done, Total = total });
                }
            }, ct);
            if (!got.AsSpan().SequenceEqual(r.Footer!.Versions[v].Sha256))
                problems.Add($"Version {v + 1} ({h.Versions[v].Name}): the archive does not reproduce the backed-up content.");
            else if (cmp.FirstDifference >= 0 || cmp.Length != new FileInfo(inputs[v]).Length)
                problems.Add($"Version {v + 1} ({h.Versions[v].Name}): differs from the current file" +
                             (cmp.FirstDifference >= 0 ? $" at byte {cmp.FirstDifference:N0}" : "") + " (changed after the backup?).");
        }
        return problems;
    }

    /// <summary>
    /// Integrity test without the original files: every volume must exist with the right size, and
    /// every segment must pass its SHA-256 and decompress correctly (CRC64). Reports which volumes
    /// are missing or damaged.
    /// </summary>
    public static TestReport Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var rep = new TestReport();
        var sw = Stopwatch.StartNew();
        ArchiveReader r;
        try
        {
            r = ArchiveReader.Open(archivePath);
        }
        catch (VolumeException e)
        {
            rep.MissingVolumes.Add(e.VolumeNumber);
            rep.Problems.Add(e.Message);
            rep.Elapsed = sw.Elapsed;
            return rep;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            rep.Problems.Add(e.Message);
            rep.Elapsed = sw.Elapsed;
            return rep;
        }
        using (r)
        {
            foreach (var p in r.CheckVolumes())
            {
                if (p.Problem == "missing")
                    rep.MissingVolumes.Add(p.VolumeNumber);
                else
                    rep.DamagedVolumes.Add(p.VolumeNumber);
                rep.Problems.Add($"Volume {p.VolumeNumber} ({Path.GetFileName(p.Path)}): {p.Problem}.");
            }
            var f = r.Footer!;
            rep.SegmentsTotal = f.Segments.Count;
            var head = new byte[FormatConst.SegmentRecordSize];
            byte[] comp = Array.Empty<byte>(), res = Array.Empty<byte>();
            long total = f.Segments.Sum(s => s.CompressedLength);
            long done = 0;
            var last = Stopwatch.StartNew();
            foreach (var s in f.Segments)
            {
                ct.ThrowIfCancellationRequested();
                long end = s.RecordOffset + FormatConst.SegmentRecordSize + s.CompressedLength;
                int v1 = r.Layout.VolumeOf(s.RecordOffset), v2 = r.Layout.VolumeOf(end - 1);
                if (Enumerable.Range(v1 + 1, v2 - v1 + 1).Any(rep.MissingVolumes.Contains))
                {
                    done += s.CompressedLength;
                    continue;
                }
                try
                {
                    r.Read(s.RecordOffset, head);
                    if (!SegmentRecord.TryParse(head, out var rec) || rec.Version != s.Version || rec.Index != s.Index || rec.CompressedLength != s.CompressedLength)
                        throw new InvalidDataException("record header is damaged");
                    if (comp.Length < rec.CompressedLength)
                        comp = new byte[rec.CompressedLength];
                    var c = comp.AsSpan(0, (int)rec.CompressedLength);
                    r.Read(s.RecordOffset + FormatConst.SegmentRecordSize, c);
                    if (!SHA256.HashData(c).AsSpan().SequenceEqual(rec.PayloadSha256))
                        throw new InvalidDataException("checksum mismatch");
                    if (res.Length < rec.EncodedLength)
                        res = new byte[rec.EncodedLength];
                    Xz.Decompress(c, res.AsSpan(0, rec.EncodedLength));
                    rep.SegmentsChecked++;
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    for (int k = v1; k <= v2; k++)
                        rep.DamagedVolumes.Add(k + 1);
                    string where = v1 == v2 ? $"volume {v1 + 1}" : $"volumes {v1 + 1}–{v2 + 1}";
                    rep.Problems.Add($"Version {s.Version + 1}, segment {s.Index + 1}: {e.Message} ({where}).");
                }
                done += s.CompressedLength;
                if (progress != null && last.ElapsedMilliseconds > 200)
                {
                    last.Restart();
                    progress.Report(new ProgressInfo { Phase = Phase.Testing, Version = s.Version, VersionCount = r.Header.Versions.Count, Done = done, Total = total });
                }
            }
            if (rep.MissingVolumes.Count > 0)
                rep.Problems.Add($"{rep.SegmentsTotal - rep.SegmentsChecked} segment(s) could not be checked because volumes are missing.");
        }
        rep.Elapsed = sw.Elapsed;
        return rep;
    }

    /// <summary>Reads the description of an archive (complete or not).</summary>
    public static ArchiveInfo ReadInfo(string archivePath)
    {
        using var r = ArchiveReader.Open(archivePath, allowIncomplete: true);
        var paths = r.VolumePaths().ToList();
        return new ArchiveInfo
        {
            Header = r.Header,
            Footer = r.Footer,
            SelfExtracting = r.Layout.SelfExtracting,
            VolumePaths = paths,
            VolumeProblems = r.Header.Complete ? r.CheckVolumes() : new List<VolumeProblem>(),
            ArchiveBytes = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length),
        };
    }
}
