namespace LzFilm.Core;

public enum Phase
{
    Preparing,
    CheckingExisting,
    Indexing,
    Rereading,
    Compressing,
    Finalizing,
    Verifying,
    Restoring,
    Testing,
    Scanning,
    Done,
}

/// <summary>Progress snapshot. <see cref="Done"/>/<see cref="Total"/> are bytes of the current phase.</summary>
public sealed class ProgressInfo
{
    public Phase Phase { get; init; }
    public int Version { get; init; } = -1;
    public int VersionCount { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    public long ArchiveBytes { get; init; }
    public string? Message { get; init; }

    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

public sealed class BackupOptions
{
    /// <summary>xz preset 0–9 (6 = 8 MiB dictionary, ~94 MiB encoder memory per thread).</summary>
    public int Preset { get; set; } = 6;
    /// <summary>Detect relocated 4 KB blocks (recommended for disk images).</summary>
    public bool Motion { get; set; } = true;
    public int Threads { get; set; } = DefaultThreads;
    /// <summary>Input bytes per independently compressed segment.</summary>
    public int SegmentSizeMiB { get; set; } = 32;
    /// <summary>Volume size in bytes; 0 = a single file.</summary>
    public long VolumeSize { get; set; }
    public bool SelfExtracting { get; set; }
    /// <summary>Memory limit of one motion index (two exist while a version is coded).</summary>
    public long MotionIndexMemoryLimit { get; set; } = 1L << 30;
    /// <summary>For tests: fixed archive id, so that two runs produce identical files.</summary>
    public Guid? ArchiveId { get; set; }

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

public sealed class BackupJob
{
    /// <summary>Full images of the versions, oldest first.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();
    public string OutputDirectory { get; init; } = "";
    /// <summary>Archive name without extension.</summary>
    public string BaseName { get; init; } = "";
    public BackupOptions Options { get; init; } = new();
    /// <summary>Self-extractor program (required when <see cref="BackupOptions.SelfExtracting"/> is set).</summary>
    public byte[]? SfxStub { get; init; }
    /// <summary>Continue an interrupted archive with the same name and settings.</summary>
    public bool Resume { get; init; }
}

public sealed class BackupResult
{
    public bool Completed { get; init; }
    public bool Resumed { get; init; }
    public long InputBytes { get; init; }
    public long ArchiveBytes { get; init; }
    public BlockStats Stats { get; init; }
    public bool MotionIndexTruncated { get; init; }
    public TimeSpan Elapsed { get; init; }
    public List<string> Files { get; init; } = new();
}

public sealed class ResumeInfo
{
    public bool CanResume { get; init; }
    public string Reason { get; init; } = "";
    public int VersionsDone { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
}

public sealed class RestoreResult
{
    public List<string> Files { get; init; } = new();
    public TimeSpan Elapsed { get; init; }
}

public sealed class TestReport
{
    public bool Ok => Problems.Count == 0;
    public List<string> Problems { get; } = new();
    public SortedSet<int> MissingVolumes { get; } = new();
    public SortedSet<int> DamagedVolumes { get; } = new();
    public int SegmentsChecked { get; set; }
    public int SegmentsTotal { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class ArchiveInfo
{
    public ArchiveHeader Header { get; init; } = new();
    public Footer? Footer { get; init; }
    public bool SelfExtracting { get; init; }
    public List<string> VolumePaths { get; init; } = new();
    public List<VolumeProblem> VolumeProblems { get; init; } = new();
    public long ArchiveBytes { get; init; }
    public long InputBytes => Header.Files ? (Header.Versions.Count > 0 ? Header.Versions[0].Size : 0) : Header.Versions.Sum(v => v.Size);
    /// <summary>Files-and-folders archive (otherwise a chain of disk image versions).</summary>
    public bool IsFiles => Header.Files;
}
