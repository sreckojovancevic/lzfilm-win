using LzFilm.Core;

namespace LzFilm.App;

/// <summary>The compression / volume / self-extracting options, used by both the "Pack" and the "Disk images" tab.</summary>
internal sealed class OptionControls
{
    private static readonly string[] VolumePresets =
    {
        "10 MB", "100 MB", "650 MB (CD)", "700 MB (CD)", "1 GB", "4095 MB (FAT32)", "4480 MB (DVD)",
    };

    /// <summary>Files-mode profiles: (label, xz level, segment MiB). Measured on a 571 MiB folder of git repositories.</summary>
    internal static readonly (string Label, int Preset, int SegmentMiB)[] Profiles =
    {
        ("Fast — level 6, 32 MB segments", 6, 32),
        ("Maximum — level 9, 64 MB segments (recommended)", 9, 64),
        ("Ultra — level 9, 256 MB segments (smallest, slower)", 9, 256),
    };

    private readonly bool _diskImages;
    private readonly bool _sfxAvailable;

    public readonly NumericUpDown Preset = new() { Minimum = 0, Maximum = 9, Value = 6, Width = 60 };
    public readonly ComboBox Profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    public readonly NumericUpDown Threads = new() { Minimum = 1, Maximum = Math.Max(1, Environment.ProcessorCount), Value = BackupOptions.DefaultThreads, Width = 60 };
    public readonly ComboBox Segment = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    public readonly CheckBox Motion = new() { Text = "Detect moved blocks (recommended)", Checked = true, AutoSize = true };
    public readonly CheckBox Split = new() { Text = "Split into volumes of", AutoSize = true };
    public readonly ComboBox VolumeSize = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 140, Enabled = false };
    public readonly CheckBox Sfx = new() { Text = "Self-extracting (.exe)", AutoSize = true };
    public readonly CheckBox Verify = new() { Text = "Verify after packing", Checked = true, AutoSize = true };
    public readonly Label Estimate = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    /// <summary>Raised when anything that affects the memory estimate or the archive name changes.</summary>
    public event Action? Changed;
    /// <summary>Raised when "split" or "self-extracting" changes (the archive extension changes).</summary>
    public event Action? NameChanged;

    public OptionControls(bool diskImages, bool sfxAvailable)
    {
        _diskImages = diskImages;
        _sfxAvailable = sfxAvailable;
        foreach (var p in Profiles)
            Profile.Items.Add(p.Label);
        Profile.SelectedIndex = 1;
        Profile.SelectedIndexChanged += (_, _) => Changed?.Invoke();
        foreach (var s in new[] { 8, 16, 32, 64, 128 })
            Segment.Items.Add($"{s} MB");
        Segment.SelectedItem = "32 MB";
        VolumeSize.Items.AddRange(VolumePresets);
        VolumeSize.Text = "100 MB";
        Sfx.Enabled = sfxAvailable;
        if (!sfxAvailable)
            Sfx.Text += " — not available in this build";
        if (diskImages)
            Verify.Text = "Verify against the originals after backup";

        Split.CheckedChanged += (_, _) => { VolumeSize.Enabled = Split.Checked; Changed?.Invoke(); NameChanged?.Invoke(); };
        Sfx.CheckedChanged += (_, _) => NameChanged?.Invoke();
        Preset.ValueChanged += (_, _) => Changed?.Invoke();
        Threads.ValueChanged += (_, _) => Changed?.Invoke();
        Segment.SelectedIndexChanged += (_, _) => Changed?.Invoke();
        Motion.CheckedChanged += (_, _) => Changed?.Invoke();
        VolumeSize.TextChanged += (_, _) => Changed?.Invoke();
        VolumeSize.SelectedIndexChanged += (_, _) => Changed?.Invoke();
    }

    public GroupBox Build()
    {
        var t = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };
        int row = 0;
        if (_diskImages)
        {
            t.Controls.Add(Lbl("Compression level (0–9):"), 0, row);
            t.Controls.Add(Preset, 1, row);
        }
        else
        {
            t.Controls.Add(Lbl("Compression:"), 0, row);
            t.Controls.Add(Profile, 1, row);
        }
        t.Controls.Add(Lbl("Threads:"), 2, row);
        t.Controls.Add(Threads, 3, row);
        row++;
        if (_diskImages)
        {
            t.Controls.Add(Lbl("Segment size:"), 0, row);
            t.Controls.Add(Segment, 1, row);
            t.Controls.Add(Motion, 2, row);
            t.SetColumnSpan(Motion, 2);
            row++;
        }
        var vol = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        vol.Controls.Add(Split);
        vol.Controls.Add(VolumeSize);
        vol.Controls.Add(Lbl("(1 MB = 1 048 576 bytes)"));
        t.Controls.Add(vol, 0, row);
        t.SetColumnSpan(vol, 4);
        row++;
        t.Controls.Add(Sfx, 0, row);
        t.SetColumnSpan(Sfx, 2);
        t.Controls.Add(Verify, 2, row);
        t.SetColumnSpan(Verify, 2);
        row++;
        t.Controls.Add(Estimate, 0, row);
        t.SetColumnSpan(Estimate, 4);
        var box = new GroupBox { Text = "Options", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        box.Controls.Add(t);
        return box;
    }

    public bool SelfExtracting => Sfx.Checked && _sfxAvailable;
    public bool Splitting => Split.Checked;

    /// <summary>Reads the options; null (with a message if <paramref name="owner"/> is given) when the volume size is invalid.</summary>
    public BackupOptions? Read(IWin32Window? owner)
    {
        long volume = 0;
        if (Split.Checked)
        {
            var v = UiLogic.ParseSize(VolumeSize.Text);
            if (v == null || v < (1L << 20))
            {
                if (owner != null)
                    MessageBox.Show(owner, "Enter a volume size of at least 1 MB, for example \"100 MB\" or \"4.7 GB\".", "Volume size",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            volume = v.Value;
        }
        var profile = Profiles[Math.Max(0, Profile.SelectedIndex)];
        return new BackupOptions
        {
            Preset = _diskImages ? (int)Preset.Value : profile.Preset,
            Threads = (int)Threads.Value,
            SegmentSizeMiB = _diskImages ? int.Parse(((string?)Segment.SelectedItem ?? "32 MB").Split(' ')[0]) : profile.SegmentMiB,
            Motion = _diskImages && Motion.Checked,
            VolumeSize = volume,
            SelfExtracting = SelfExtracting,
        };
    }

    internal static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
}
