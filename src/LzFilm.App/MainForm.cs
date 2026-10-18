using System.Diagnostics;
using System.Reflection;
using LzFilm.Core;

namespace LzFilm.App;

/// <summary>Main window. Built in code (no designer file); all work is done by LzFilm.Core.</summary>
internal sealed class MainForm : Form
{
    private const string StubResource = "LzFilm.SfxStub.exe";

    private readonly byte[]? _stub = LoadStub();

    // shared
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 22 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready." };
    private readonly Button _cancel = new() { Text = "Cancel", Enabled = false, AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f), WordWrap = false };
    private readonly RateMeter _meter = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private Task? _running;
    private bool _showVersions;

    // pack tab (files and folders)
    private readonly ListView _items = new() { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _packOutput = new() { Dock = DockStyle.Fill };
    private readonly OptionControls _packOpts;
    private readonly Button _startPack = new() { Text = "Pack", AutoSize = true };

    // disk images tab
    private readonly ListView _versions = new() { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, AllowDrop = true };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly OptionControls _snapOpts;
    private readonly Button _startBackup = new() { Text = "Start backup", AutoSize = true };

    // extract tab
    private readonly TextBox _archive = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox _restoreVersions = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, Visible = false, HideSelection = false };
    private readonly Label _archiveInfo = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label _contentLabel = OptionControls.Lbl("Contents:");
    private readonly TextBox _destination = new() { Dock = DockStyle.Fill };
    private readonly Button _selectAll = new() { Text = "All", AutoSize = true };
    private readonly Button _selectLatest = new() { Text = "Latest only", AutoSize = true };
    private readonly Button _startRestore = new() { Text = "Extract", AutoSize = true, Enabled = false };
    private readonly Button _startTest = new() { Text = "Test integrity", AutoSize = true, Enabled = false };
    private readonly Button _clearArchive = new() { Text = "Clear", AutoSize = true };
    private bool _archiveIsFiles;
    private bool _treeUpdating;

    public MainForm()
    {
        _packOpts = new OptionControls(diskImages: false, sfxAvailable: _stub != null);
        _snapOpts = new OptionControls(diskImages: true, sfxAvailable: _stub != null);

        Text = "LzFilm";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(820, 660);
        Size = new Size(980, 780);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        _tabs.TabPages.Add(BuildPackTab());
        _tabs.TabPages.Add(BuildBackupTab());
        _tabs.TabPages.Add(BuildRestoreTab());

        var about = new LinkLabel { Text = "About", AutoSize = true, Anchor = AnchorStyles.Right };
        about.LinkClicked += (_, _) => ShowAbout();
        _cancel.Click += (_, _) => { _cts?.Cancel(); _cancel.Enabled = false; _status.Text = "Cancelling..."; };

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(8, 4, 8, 8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(_cancel, 1, 0);
        bottom.Controls.Add(about, 2, 0);
        bottom.Controls.Add(_status, 0, 1);
        bottom.SetColumnSpan(_status, 3);
        bottom.Controls.Add(_log, 0, 2);
        bottom.SetColumnSpan(_log, 3);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_tabs);
        split.Panel2.Controls.Add(bottom);
        Controls.Add(split);
        Load += (_, _) =>
        {
            split.SplitterDistance = Math.Max(200, split.Height - LogicalToDeviceUnits(200));
            Log($"LzFilm {Version()} — liblzma {Xz.Version}, {Environment.ProcessorCount} logical CPUs.");
            if (_stub == null)
                Log("Self-extracting archives are not available in this build (the extractor stub was not embedded).");
        };
        FormClosing += OnFormClosing;
    }

    public void OpenArchiveOnStart(string path)
    {
        _tabs.SelectedIndex = 2;
        Shown += (_, _) => LoadArchive(path);
    }

    private static void AllowFileDrop(Control c, Action<string[]> onDrop)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) onDrop(f); };
    }

    private static TableLayoutPanel Grid(int rows)
    {
        var g = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = rows };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    private static FlowLayoutPanel ButtonColumn(params Button[] buttons)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        foreach (var b in buttons)
        {
            b.MinimumSize = new Size(120, 0);
            p.Controls.Add(b);
        }
        return p;
    }

    private static Label Hint(string text) => new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = text, MaximumSize = new Size(780, 0) };

    // =========================================================================== pack tab

    private TabPage BuildPackTab()
    {
        var page = new TabPage("Pack files") { Padding = new Padding(8) };
        _items.Columns.Add("Name", 280);
        _items.Columns.Add("Type", 70);
        _items.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _items.Columns.Add("Location", 380);
        AllowFileDrop(_items, AddItems);
        _items.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveItems(); };

        var addFiles = new Button { Text = "Add files...", AutoSize = true };
        var addFolder = new Button { Text = "Add folder...", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        addFiles.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Add files", Multiselect = true, Filter = "All files (*.*)|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK)
                AddItems(d.FileNames);
        };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Add a folder (with everything in it)", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK)
                AddItems(new[] { d.SelectedPath });
        };
        remove.Click += (_, _) => RemoveItems();
        clear.Click += (_, _) => { _items.Items.Clear(); UpdatePackEstimate(); };

        var outBrowse = new Button { Text = "Browse...", AutoSize = true };
        outBrowse.Click += (_, _) => BrowseArchiveName(_packOutput, _packOpts);
        _packOpts.Changed += UpdatePackEstimate;
        _packOpts.NameChanged += () => FixExtension(_packOutput, _packOpts);
        _startPack.Click += (_, _) => StartPack();

        var g = Grid(6);
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = Hint("Add files and folders (or drag them here), choose the archive name and press Pack. " +
                        "Names in any script (Cyrillic, Latin with diacritics, ...) are kept exactly.");
        g.Controls.Add(hint, 0, 0);
        g.SetColumnSpan(hint, 3);
        g.Controls.Add(_items, 0, 1);
        g.SetColumnSpan(_items, 2);
        g.Controls.Add(ButtonColumn(addFiles, addFolder, remove, clear), 2, 1);
        g.Controls.Add(OptionControls.Lbl("Archive:"), 0, 2);
        g.Controls.Add(_packOutput, 1, 2);
        g.Controls.Add(outBrowse, 2, 2);
        var opts = _packOpts.Build();
        g.Controls.Add(opts, 0, 3);
        g.SetColumnSpan(opts, 3);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        row.Controls.Add(_startPack);
        g.Controls.Add(row, 0, 4);
        g.SetColumnSpan(row, 3);
        page.Controls.Add(g);
        UpdatePackEstimate();
        return page;
    }

    private List<string> PackItems() => _items.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void AddItems(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(PackItems(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (!existing.Add(full))
                continue;
            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full))
                continue;
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            if (name.Length == 0)
                name = full;
            string size = isDir ? "" : UiLogic.FormatBytes(new FileInfo(full).Length);
            _items.Items.Add(new ListViewItem(new[] { name, isDir ? "Folder" : "File", size, Path.GetDirectoryName(full) ?? "" }) { Tag = full });
        }
        if (_packOutput.Text.Length == 0 && _items.Items.Count > 0)
        {
            var first = (string)_items.Items[0].Tag!;
            var stem = _items.Items.Count == 1 || Directory.Exists(first)
                ? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(first))
                : "archive";
            if (stem.Length == 0)
                stem = "archive";
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(first)) ?? first;
            _packOutput.Text = Path.Combine(parent, UiLogic.FirstVolumeName(stem, _packOpts.Splitting, _packOpts.SelfExtracting));
        }
        UpdatePackEstimate();
    }

    private void RemoveItems()
    {
        foreach (ListViewItem it in _items.SelectedItems)
            _items.Items.Remove(it);
        UpdatePackEstimate();
    }

    private void UpdatePackEstimate()
    {
        var o = _packOpts.Read(null);
        if (o == null)
        {
            _packOpts.Estimate.Text = "Volume size: enter at least 1 MB.";
            return;
        }
        long files = _items.Items.Cast<ListViewItem>().Where(i => File.Exists((string)i.Tag!)).Sum(i => new FileInfo((string)i.Tag!).Length);
        int folders = _items.Items.Cast<ListViewItem>().Count(i => Directory.Exists((string)i.Tag!));
        long mem = SnapArchiver.EstimateMemory(o, 0, filesArchive: true, dataBytes: folders == 0 && _items.Items.Count > 0 ? files : -1);
        string text = (folders > 0 || _items.Items.Count == 0 ? "Memory: up to about " : "Memory: about ") + UiLogic.FormatBytes(mem);
        if (_items.Items.Count > 0)
            text += $"   ·   {_items.Items.Count} item(s)" + (folders > 0 ? $", {folders} folder(s) — the total size is counted when packing starts" : $", {UiLogic.FormatBytes(files)}");
        SetEstimate(_packOpts, text, mem);
    }

    /// <summary>Shows the memory estimate, in orange with a hint when it exceeds about 70% of the computer's memory.</summary>
    private static void SetEstimate(OptionControls opts, string text, long mem)
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        bool tight = ram > 0 && mem > ram * 7 / 10;
        opts.Estimate.Text = tight ? text + $" — more than this computer comfortably has ({UiLogic.FormatBytes(ram)}); use fewer threads or a faster setting" : text;
        opts.Estimate.ForeColor = tight ? Color.DarkOrange : SystemColors.GrayText;
    }

    private void StartPack()
    {
        var items = PackItems();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Add files or folders first.", "Pack", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_packOutput.Text.Trim().Length == 0)
            BrowseArchiveName(_packOutput, _packOpts);
        if (_packOutput.Text.Trim().Length == 0)
            return;
        var options = _packOpts.Read(this);
        if (options == null)
            return;
        var (dir, baseName) = UiLogic.SplitArchivePath(_packOutput.Text.Trim());
        if (baseName.Length == 0 || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "The archive name is not valid.", "Pack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Directory.CreateDirectory(dir);
        bool verify = _packOpts.Verify.Checked;
        var stub = options.SelfExtracting ? _stub : null;
        Log("");
        Log($"Packing {items.Count} item(s) into {Path.Combine(dir, UiLogic.FirstVolumeName(baseName, options.VolumeSize > 0, options.SelfExtracting))} — level {options.Preset}, {options.Threads} thread(s)" +
            (options.VolumeSize > 0 ? $", volumes of {UiLogic.FormatBytes(options.VolumeSize)}" : "") +
            (options.SelfExtracting ? ", self-extracting" : ""));
        _showVersions = false;

        Run(async (progress, ct) =>
        {
            var scan = await Task.Run(() => FolderArchiver.Scan(items, dir, baseName, progress, ct), ct);
            Log($"{scan.FileCount:N0} file(s) in {scan.DirectoryCount:N0} folder(s), {UiLogic.FormatBytes(scan.TotalBytes)}; " +
                $"memory about {UiLogic.FormatBytes(SnapArchiver.EstimateMemory(options, 0, filesArchive: true, dataBytes: scan.TotalBytes))}.");
            foreach (var s in scan.Skipped.Take(20))
                Log("  skipped: " + s);
            if (scan.Skipped.Count > 20)
                Log($"  ... and {scan.Skipped.Count - 20} more skipped.");

            var job = new BackupJob { OutputDirectory = dir, BaseName = baseName, Options = options, SfxStub = stub };
            var ri = FolderArchiver.InspectResume(job, scan);
            if (ri.CanResume)
            {
                var answer = MessageBox.Show(this,
                    $"An unfinished archive of the same files with the same settings was found ({UiLogic.FormatBytes(ri.BytesDone)} of {UiLogic.FormatBytes(ri.BytesTotal)} done).\n\n" +
                    "Yes — continue where it stopped\nNo — start over\nCancel — do nothing",
                    "Resume", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel)
                    return "Cancelled.";
                if (answer == DialogResult.Yes)
                    job = new BackupJob { OutputDirectory = dir, BaseName = baseName, Options = options, SfxStub = stub, Resume = true };
            }
            else
            {
                var first = Path.Combine(dir, UiLogic.FirstVolumeName(baseName, options.VolumeSize > 0, options.SelfExtracting));
                if (File.Exists(first) &&
                    MessageBox.Show(this, $"{Path.GetFileName(first)} already exists and will be replaced.", "Pack",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    return "Cancelled.";
            }

            var res = await Task.Run(() => FolderArchiver.Create(job, scan, progress, ct), ct);
            if (!res.Completed)
            {
                Log("Cancelled. Press Pack again with the same files and settings to continue where it stopped.");
                return "Packing cancelled — it can be resumed.";
            }
            double ratio = res.InputBytes > 0 ? (double)res.ArchiveBytes / res.InputBytes : 0;
            Log($"Done in {UiLogic.FormatDuration(res.Elapsed)}: {UiLogic.FormatBytes(res.InputBytes)} → {UiLogic.FormatBytes(res.ArchiveBytes)} ({ratio:P1}).");
            foreach (var f in res.Files)
                Log("  " + f);
            if (!verify)
                return "Packing complete.";
            Log("Verifying...");
            var problems = await Task.Run(() => FolderArchiver.Verify(res.Files[0], scan, progress, ct), ct);
            if (problems.Count == 0)
            {
                Log("Verification OK — every file is reproduced exactly.");
                return "Packing complete and verified.";
            }
            foreach (var p in problems.Take(50))
                Log("  PROBLEM: " + p);
            MessageBox.Show(this, "Verification found problems:\n\n" + string.Join("\n", problems.Take(10)), "Verify", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return "Packing finished, but verification reported problems.";
        });
    }

    // =========================================================================== disk images tab

    private TabPage BuildBackupTab()
    {
        var page = new TabPage("Disk image versions") { Padding = new Padding(8) };

        _versions.Columns.Add("#", 40);
        _versions.Columns.Add("Version (oldest first)", 260);
        _versions.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _versions.Columns.Add("Modified", 140);
        _versions.Columns.Add("Folder", 300);
        AllowFileDrop(_versions, files => AddVersions(files.Where(File.Exists).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)));
        _versions.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        var add = new Button { Text = "Add...", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        var up = new Button { Text = "Move up", AutoSize = true };
        var down = new Button { Text = "Move down", AutoSize = true };
        var sortByDate = new Button { Text = "Sort by date", AutoSize = true };
        add.Click += (_, _) => BrowseVersions();
        remove.Click += (_, _) => RemoveSelected();
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        sortByDate.Click += (_, _) => SortByDate();

        var outBrowse = new Button { Text = "Browse...", AutoSize = true };
        outBrowse.Click += (_, _) => BrowseArchiveName(_output, _snapOpts);
        _snapOpts.Changed += UpdateEstimate;
        _snapOpts.NameChanged += () => FixExtension(_output, _snapOpts);
        _startBackup.Click += (_, _) => StartBackup();

        var g = Grid(6);
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = Hint("Successive versions of the same large disk image (raw / flat VMDK / fixed VHD), oldest first. " +
                        "Each version is stored as the difference to the previous one, so many versions take little more space than one.");
        g.Controls.Add(hint, 0, 0);
        g.SetColumnSpan(hint, 3);
        g.Controls.Add(_versions, 0, 1);
        g.SetColumnSpan(_versions, 2);
        g.Controls.Add(ButtonColumn(add, remove, up, down, sortByDate), 2, 1);
        g.Controls.Add(OptionControls.Lbl("Archive:"), 0, 2);
        g.Controls.Add(_output, 1, 2);
        g.Controls.Add(outBrowse, 2, 2);
        var opts = _snapOpts.Build();
        g.Controls.Add(opts, 0, 3);
        g.SetColumnSpan(opts, 3);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        row.Controls.Add(_startBackup);
        g.Controls.Add(row, 0, 4);
        g.SetColumnSpan(row, 3);
        page.Controls.Add(g);
        UpdateEstimate();
        return page;
    }

    private void BrowseVersions()
    {
        using var d = new OpenFileDialog
        {
            Title = "Add versions (they are sorted by name; reorder if needed)",
            Multiselect = true,
            Filter = "Disk images (*.img;*.raw;*.vmdk;*.vhd;*.bin;*.iso)|*.img;*.raw;*.vmdk;*.vhd;*.bin;*.iso|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) == DialogResult.OK)
            AddVersions(d.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void AddVersions(IEnumerable<string> files)
    {
        var existing = new HashSet<string>(Inputs(), StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var full = Path.GetFullPath(f);
            if (!existing.Add(full))
                continue;
            var fi = new FileInfo(full);
            var item = new ListViewItem(new[] { "", fi.Name, UiLogic.FormatBytes(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), fi.DirectoryName ?? "" }) { Tag = full };
            _versions.Items.Add(item);
            var warn = UiLogic.DetectImageWarning(full);
            if (warn != null)
            {
                item.ForeColor = Color.DarkOrange;
                Log($"Warning — {fi.Name}: {warn.Replace('\n', ' ')}");
                MessageBox.Show(this, $"{fi.Name}\n\n{warn}\n\nThe file was added anyway, but the result will be much larger than with flat images.",
                    "Image format", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (_output.Text.Length == 0 && _versions.Items.Count > 0)
        {
            var first = (string)_versions.Items[0].Tag!;
            var stem = Path.GetFileNameWithoutExtension(first);
            _output.Text = Path.Combine(Path.GetDirectoryName(first)!, UiLogic.FirstVolumeName(stem + "-versions", _snapOpts.Splitting, _snapOpts.SelfExtracting));
        }
        Renumber();
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem it in _versions.SelectedItems)
            _versions.Items.Remove(it);
        Renumber();
    }

    private void MoveSelected(int delta)
    {
        if (_versions.SelectedItems.Count != 1)
            return;
        var it = _versions.SelectedItems[0];
        int i = it.Index, j = i + delta;
        if (j < 0 || j >= _versions.Items.Count)
            return;
        _versions.Items.RemoveAt(i);
        _versions.Items.Insert(j, it);
        it.Selected = true;
        it.Focused = true;
        Renumber();
    }

    private void SortByDate()
    {
        var items = _versions.Items.Cast<ListViewItem>().OrderBy(i => File.GetLastWriteTimeUtc((string)i.Tag!)).ToList();
        _versions.Items.Clear();
        _versions.Items.AddRange(items.ToArray());
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < _versions.Items.Count; i++)
            _versions.Items[i].Text = (i + 1).ToString();
        UpdateEstimate();
    }

    private List<string> Inputs() => _versions.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void BrowseArchiveName(TextBox target, OptionControls opts)
    {
        using var d = new SaveFileDialog
        {
            Title = "Archive name",
            Filter = opts.SelfExtracting ? "Self-extracting archive (*.exe)|*.exe" : "LzFilm archive (*.lzs)|*.lzs",
            OverwritePrompt = false,
        };
        if (target.Text.Length > 0)
        {
            var (dir, name) = UiLogic.SplitArchivePath(target.Text);
            d.InitialDirectory = dir;
            d.FileName = name;
        }
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            var (dir, name) = UiLogic.SplitArchivePath(d.FileName);
            target.Text = Path.Combine(dir, UiLogic.FirstVolumeName(name, opts.Splitting, opts.SelfExtracting));
        }
    }

    private static void FixExtension(TextBox target, OptionControls opts)
    {
        if (target.Text.Trim().Length == 0)
            return;
        var (dir, name) = UiLogic.SplitArchivePath(target.Text.Trim());
        target.Text = Path.Combine(dir, UiLogic.FirstVolumeName(name, opts.Splitting, opts.SelfExtracting));
    }

    private void UpdateEstimate()
    {
        var o = _snapOpts.Read(null);
        if (o == null)
        {
            _snapOpts.Estimate.Text = "Volume size: enter at least 1 MB.";
            return;
        }
        long largest = 0, total = 0;
        foreach (var p in Inputs())
        {
            try
            {
                long len = new FileInfo(p).Length;
                largest = Math.Max(largest, len);
                total += len;
            }
            catch (IOException) { }
        }
        long mem = SnapArchiver.EstimateMemory(o, largest);
        string text = $"Memory: about {UiLogic.FormatBytes(mem)}";
        if (total > 0)
            text += $"   ·   Input: {_versions.Items.Count} version(s), {UiLogic.FormatBytes(total)}";
        SetEstimate(_snapOpts, text, mem);
    }

    private void StartBackup()
    {
        var inputs = Inputs();
        if (inputs.Count == 0)
        {
            MessageBox.Show(this, "Add at least one version.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var missing = inputs.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "These files no longer exist:\n\n" + string.Join("\n", missing), "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_output.Text.Trim().Length == 0)
            BrowseArchiveName(_output, _snapOpts);
        if (_output.Text.Trim().Length == 0)
            return;
        var options = _snapOpts.Read(this);
        if (options == null)
            return;
        var (dir, baseName) = UiLogic.SplitArchivePath(_output.Text.Trim());
        if (baseName.Length == 0 || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "The archive name is not valid.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Directory.CreateDirectory(dir);

        var job = new BackupJob { Inputs = inputs, OutputDirectory = dir, BaseName = baseName, Options = options, SfxStub = options.SelfExtracting ? _stub : null };
        var ri = SnapArchiver.InspectResume(job);
        if (ri.CanResume)
        {
            var answer = MessageBox.Show(this,
                $"An unfinished backup with the same name and settings was found ({ri.VersionsDone} of {inputs.Count} version(s) complete, " +
                $"{UiLogic.FormatBytes(ri.BytesDone)} of {UiLogic.FormatBytes(ri.BytesTotal)}).\n\n" +
                "Yes — continue where it stopped\nNo — start over (the old parts are deleted)\nCancel — do nothing",
                "Resume backup", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
                job = new BackupJob { Inputs = inputs, OutputDirectory = dir, BaseName = baseName, Options = options, SfxStub = job.SfxStub, Resume = true };
        }
        else
        {
            var first = Path.Combine(dir, UiLogic.FirstVolumeName(baseName, options.VolumeSize > 0, options.SelfExtracting));
            if (File.Exists(first) &&
                MessageBox.Show(this, $"{Path.GetFileName(first)} already exists and will be replaced.\n({ri.Reason})", "Backup",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;
        }

        bool verify = _snapOpts.Verify.Checked;
        Log("");
        Log($"Backup of {inputs.Count} version(s) to {Path.Combine(dir, baseName)} — level {options.Preset}, {options.Threads} thread(s)" +
            (options.VolumeSize > 0 ? $", volumes of {UiLogic.FormatBytes(options.VolumeSize)}" : "") +
            (options.SelfExtracting ? ", self-extracting" : "") + (job.Resume ? ", resuming" : ""));
        _showVersions = true;

        Run(async (progress, ct) =>
        {
            var res = await Task.Run(() => SnapArchiver.Create(job, progress, ct), ct);
            if (!res.Completed)
            {
                Log("Cancelled. Start the backup again with the same settings to continue where it stopped.");
                return "Backup cancelled — it can be resumed.";
            }
            double ratio = res.InputBytes > 0 ? (double)res.ArchiveBytes / res.InputBytes : 0;
            Log($"Done in {UiLogic.FormatDuration(res.Elapsed)}: {UiLogic.FormatBytes(res.InputBytes)} → {UiLogic.FormatBytes(res.ArchiveBytes)} ({ratio:P1}).");
            var s = res.Stats;
            Log($"Blocks: unchanged {s.Unchanged:N0}, changed in place {s.ChangedInPlace:N0}, moved {s.Moved:N0}, new {s.NoPrediction:N0}.");
            if (res.MotionIndexTruncated)
                Log("Note: the moved-block index reached its memory limit; some moved blocks were stored as new data.");
            foreach (var f in res.Files)
                Log("  " + f);
            if (!verify)
                return "Backup complete.";

            Log("Verifying against the original files...");
            var problems = await Task.Run(() => SnapArchiver.VerifyAgainstOriginals(res.Files[0], inputs, progress, ct), ct);
            if (problems.Count == 0)
            {
                Log("Verification OK — every version restores bit-for-bit.");
                return "Backup complete and verified.";
            }
            foreach (var p in problems)
                Log("  PROBLEM: " + p);
            MessageBox.Show(this, "Verification found problems:\n\n" + string.Join("\n", problems.Take(10)), "Verify", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return "Backup finished, but verification failed.";
        });
    }

    // =========================================================================== extract tab

    private TabPage BuildRestoreTab()
    {
        var page = new TabPage("Extract / Test") { Padding = new Padding(8) };
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog
            {
                Title = "Open archive (for split archives choose any part)",
                Filter = "LzFilm archives (*.lzs;*.lzs.001;*.exe)|*.lzs;*.lzs.0*;*.exe|All files (*.*)|*.*",
            };
            if (d.ShowDialog(this) == DialogResult.OK)
                LoadArchive(d.FileName);
        };
        _archive.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { LoadArchive(_archive.Text.Trim()); e.SuppressKeyPress = true; } };
        AllowFileDrop(page, f => LoadArchive(f[0]));

        var destBrowse = new Button { Text = "Browse...", AutoSize = true };
        destBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Extract into this folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(_destination.Text))
                d.SelectedPath = _destination.Text;
            if (d.ShowDialog(this) == DialogResult.OK)
                _destination.Text = d.SelectedPath;
        };

        _selectAll.Click += (_, _) =>
        {
            if (_archiveIsFiles)
                SetAllChecked(true);
            else
                for (int i = 0; i < _restoreVersions.Items.Count; i++) _restoreVersions.SetItemChecked(i, true);
        };
        _selectLatest.Click += (_, _) =>
        {
            if (_archiveIsFiles)
                SetAllChecked(false);
            else
                for (int i = 0; i < _restoreVersions.Items.Count; i++) _restoreVersions.SetItemChecked(i, i == _restoreVersions.Items.Count - 1);
        };
        _tree.AfterCheck += (_, e) =>
        {
            if (_treeUpdating || e.Node == null)
                return;
            _treeUpdating = true;
            try { CheckChildren(e.Node, e.Node.Checked); }
            finally { _treeUpdating = false; }
        };

        _startRestore.Click += (_, _) => StartRestore();
        _startTest.Click += (_, _) => StartTest();
        _clearArchive.Click += (_, _) => ClearArchive();

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_tree);
        content.Controls.Add(_restoreVersions);

        var g = Grid(6);
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.Controls.Add(OptionControls.Lbl("Archive:"), 0, 0);
        g.Controls.Add(_archive, 1, 0);
        var archiveButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        archiveButtons.Controls.Add(browse);
        archiveButtons.Controls.Add(_clearArchive);
        g.Controls.Add(archiveButtons, 2, 0);
        g.Controls.Add(_archiveInfo, 1, 1);
        g.SetColumnSpan(_archiveInfo, 2);
        g.Controls.Add(_contentLabel, 0, 2);
        g.Controls.Add(content, 1, 2);
        g.Controls.Add(ButtonColumn(_selectAll, _selectLatest), 2, 2);
        g.Controls.Add(OptionControls.Lbl("Extract to:"), 0, 3);
        g.Controls.Add(_destination, 1, 3);
        g.Controls.Add(destBrowse, 2, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_startRestore);
        buttons.Controls.Add(_startTest);
        g.Controls.Add(buttons, 0, 4);
        g.SetColumnSpan(buttons, 3);
        page.Controls.Add(g);
        return page;
    }

    private void LoadArchive(string path)
    {
        if (path.Length == 0)
            return;
        _archive.Text = path;
        _restoreVersions.Items.Clear();
        _tree.Nodes.Clear();
        _startRestore.Enabled = _startTest.Enabled = false;
        try
        {
            var info = SnapArchiver.ReadInfo(path);
            var h = info.Header;
            _archiveIsFiles = info.IsFiles;
            _tree.Visible = info.IsFiles;
            _restoreVersions.Visible = !info.IsFiles;
            _contentLabel.Text = info.IsFiles ? "Files:" : "Versions:";
            _selectLatest.Text = info.IsFiles ? "None" : "Latest only";

            string text;
            bool usable = h.Complete && info.VolumeProblems.Count == 0;
            if (info.IsFiles)
            {
                string counts = "";
                if (usable)
                {
                    var table = FolderArchiver.ReadIndex(path);
                    FillTree(table);
                    counts = $"{table.Files.Count():N0} file(s), {table.Directories.Count():N0} folder(s), ";
                }
                text = $"{h.BaseName}: files archive — {counts}{UiLogic.FormatBytes(info.InputBytes)} → {UiLogic.FormatBytes(info.ArchiveBytes)}, " +
                       $"{info.VolumePaths.Count} part(s){(info.SelfExtracting ? ", self-extracting" : "")}, level {h.Preset}.";
            }
            else
            {
                for (int i = 0; i < h.Versions.Count; i++)
                {
                    var v = h.Versions[i];
                    var when = v.LastWriteUtcTicks > 0 ? new DateTime(v.LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
                    _restoreVersions.Items.Add($"{i + 1}. {v.Name}   ({UiLogic.FormatBytes(v.Size)}, {when})", i == h.Versions.Count - 1);
                }
                text = $"{h.BaseName}: disk image versions — {h.Versions.Count} version(s), {UiLogic.FormatBytes(info.InputBytes)} → {UiLogic.FormatBytes(info.ArchiveBytes)}, " +
                       $"{info.VolumePaths.Count} part(s){(info.SelfExtracting ? ", self-extracting" : "")}, level {h.Preset}.";
            }
            if (!h.Complete)
                text += "\nThe archive is incomplete (packing was interrupted). Continue it from the tab where it was started.";
            foreach (var p in info.VolumeProblems.Take(3))
                text += $"\nPart {p.VolumeNumber} ({Path.GetFileName(p.Path)}): {p.Problem}";
            if (info.VolumeProblems.Count > 3)
                text += $"\n... and {info.VolumeProblems.Count - 3} more problem(s).";
            _archiveInfo.Text = text;
            _archiveInfo.ForeColor = usable ? SystemColors.ControlText : Color.Firebrick;
            _destination.Text = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, info.IsFiles ? h.BaseName : h.BaseName + "-restored");
            _startRestore.Enabled = usable;
            _startTest.Enabled = h.Complete;
        }
        catch (Exception e)
        {
            _archiveInfo.Text = e.Message;
            _archiveInfo.ForeColor = Color.Firebrick;
        }
    }

    private void ClearArchive()
    {
        _archive.Text = "";
        _destination.Text = "";
        _archiveInfo.Text = "";
        _archiveInfo.ForeColor = SystemColors.ControlText;
        _restoreVersions.Items.Clear();
        _treeUpdating = true;
        try { _tree.Nodes.Clear(); }
        finally { _treeUpdating = false; }
        _archiveIsFiles = false;
        _tree.Visible = false;
        _restoreVersions.Visible = true;
        _contentLabel.Text = "Contents:";
        _selectLatest.Text = "Latest only";
        _startRestore.Enabled = _startTest.Enabled = false;
        _archive.Focus();
    }

    private void FillTree(FileTable table)
    {
        _tree.BeginUpdate();
        _treeUpdating = true;
        try
        {
            _tree.Nodes.Clear();
            var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
            // Folders first (sorted), then files sorted by path, so that parents exist before children.
            foreach (var e in table.Entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Path, StringComparer.Ordinal))
            {
                int k = e.Path.LastIndexOf('/');
                string parent = k < 0 ? "" : e.Path[..k];
                var label = e.IsDirectory ? e.Name : $"{e.Name}   ({UiLogic.FormatBytes(e.Size)})";
                var node = new TreeNode(label) { Tag = e.Path, Checked = true };
                if (!e.IsDirectory)
                    node.ForeColor = SystemColors.ControlText;
                var coll = parent.Length > 0 && nodes.TryGetValue(parent, out var pn) ? pn.Nodes : _tree.Nodes;
                coll.Add(node);
                if (e.IsDirectory)
                    nodes[e.Path] = node;
            }
            foreach (TreeNode n in _tree.Nodes)
                n.Expand();
        }
        finally
        {
            _treeUpdating = false;
            _tree.EndUpdate();
        }
    }

    private static void CheckChildren(TreeNode node, bool value)
    {
        foreach (TreeNode c in node.Nodes)
        {
            c.Checked = value;
            CheckChildren(c, value);
        }
    }

    private void SetAllChecked(bool value)
    {
        _treeUpdating = true;
        try
        {
            foreach (TreeNode n in _tree.Nodes)
            {
                n.Checked = value;
                CheckChildren(n, value);
            }
        }
        finally { _treeUpdating = false; }
    }

    /// <summary>The checked part of the tree as archive paths; a fully checked folder is given as one path.</summary>
    private List<string> TreeSelection(out bool everything)
    {
        var result = new List<string>();
        bool all = true;

        bool FullyChecked(TreeNode n) => n.Checked && n.Nodes.Cast<TreeNode>().All(FullyChecked);

        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (FullyChecked(n))
                    result.Add((string)n.Tag!);
                else
                {
                    all = false;
                    Walk(n.Nodes);          // partly checked folder: take its checked parts
                }
            }
        }

        Walk(_tree.Nodes);
        everything = all;
        return result;
    }

    private void StartRestore()
    {
        var path = _archive.Text.Trim();
        var dest = _destination.Text.Trim();
        if (dest.Length == 0)
        {
            MessageBox.Show(this, "Choose the destination folder.", "Extract", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_archiveIsFiles)
        {
            var sel = TreeSelection(out bool everything);
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "Check at least one file or folder.", "Extract", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any() &&
                MessageBox.Show(this, $"{dest} is not empty. Files with the same names are overwritten.\n\nContinue?", "Extract",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            IReadOnlyCollection<string>? selection = everything ? null : sel;
            Log("");
            Log($"Extracting {(everything ? "everything" : $"{sel.Count} selected item(s)")} from {path} to {dest}");
            _showVersions = false;
            Run(async (progress, ct) =>
            {
                var res = await Task.Run(() => FolderArchiver.Extract(path, dest, selection, progress, ct), ct);
                Log($"Done in {UiLogic.FormatDuration(res.Elapsed)}: {res.Files.Count:N0} file(s) extracted and verified (SHA-256).");
                return "Extraction complete.";
            });
            return;
        }

        var versions = _restoreVersions.CheckedIndices.Cast<int>().ToList();
        if (versions.Count == 0)
        {
            MessageBox.Show(this, "Check at least one version.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Log("");
        Log($"Restoring {versions.Count} version(s) from {path} to {dest}");
        _showVersions = true;
        Run(async (progress, ct) =>
        {
            var res = await Task.Run(() => SnapArchiver.Restore(path, dest, versions, progress, ct), ct);
            Log($"Done in {UiLogic.FormatDuration(res.Elapsed)}. Every restored file was verified (SHA-256):");
            foreach (var f in res.Files)
                Log("  " + f);
            return "Restore complete.";
        });
    }

    private void StartTest()
    {
        var path = _archive.Text.Trim();
        Log("");
        Log($"Testing {path}");
        _showVersions = false;
        Run(async (progress, ct) =>
        {
            var rep = await Task.Run(() => SnapArchiver.Test(path, progress, ct), ct);
            if (rep.Ok)
            {
                Log($"OK — {rep.SegmentsChecked:N0} segments checked in {UiLogic.FormatDuration(rep.Elapsed)}.");
                return "Test passed.";
            }
            foreach (var p in rep.Problems)
                Log("  " + p);
            if (rep.MissingVolumes.Count > 0)
                Log("Missing parts: " + string.Join(", ", rep.MissingVolumes));
            if (rep.DamagedVolumes.Count > 0)
                Log("Damaged parts: " + string.Join(", ", rep.DamagedVolumes));
            return "Test failed — see the log.";
        });
    }

    // =========================================================================== running jobs

    private void Run(Func<IProgress<ProgressInfo>, CancellationToken, Task<string>> work)
    {
        if (_running is { IsCompleted: false })
            return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        var progress = new Progress<ProgressInfo>(OnProgress);
        _running = RunCore();

        async Task RunCore()
        {
            string final;
            try
            {
                final = await work(progress, ct);
            }
            catch (OperationCanceledException)
            {
                final = "Cancelled.";
                Log(final);
            }
            catch (Exception e)
            {
                final = "Failed: " + e.Message;
                Log("ERROR: " + e.Message);
                if (!IsDisposed)
                    MessageBox.Show(this, e.Message, "LzFilm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            if (IsDisposed)
                return;
            SetBusy(false);
            _status.Text = final;
            _progress.Value = final.StartsWith("Failed") || final.StartsWith("Cancel") ? 0 : _progress.Maximum;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (TabPage p in _tabs.TabPages)
            foreach (Control c in p.Controls)
                c.Enabled = !busy;
        _cancel.Enabled = busy;
        if (busy)
        {
            _progress.Value = 0;
            _status.Text = "Starting...";
        }
    }

    private void OnProgress(ProgressInfo p)
    {
        if (IsDisposed)
            return;
        if (p.Phase == Phase.Scanning)
        {
            _status.Text = $"Scanning folders  ·  {p.Done:N0} files found";
            return;
        }
        _meter.Update((p.Phase, p.Version), p.Done, p.Total, _clock.Elapsed.TotalSeconds);
        _progress.Value = (int)(p.Fraction * _progress.Maximum);
        string phase = p.Phase switch
        {
            Phase.Preparing => "Preparing",
            Phase.CheckingExisting => "Checking the existing parts",
            Phase.Indexing => "Indexing",
            Phase.Rereading => "Re-reading",
            Phase.Compressing => "Compressing",
            Phase.Finalizing => "Finishing",
            Phase.Verifying => "Verifying",
            Phase.Restoring => _archiveIsFiles && !_showVersions ? "Extracting" : "Restoring",
            Phase.Testing => "Testing",
            _ => "Done",
        };
        var parts = new List<string> { phase };
        if (_showVersions && p.Version >= 0 && p.VersionCount > 0)
            parts.Add($"version {p.Version + 1} of {p.VersionCount}");
        if (p.Total > 0)
            parts.Add($"{UiLogic.FormatBytes(p.Done)} of {UiLogic.FormatBytes(p.Total)} ({p.Fraction:P0})");
        if (_meter.BytesPerSecond > 0)
            parts.Add($"{UiLogic.FormatBytes((long)_meter.BytesPerSecond)}/s");
        if (_meter.Remaining is { } r)
            parts.Add($"about {UiLogic.FormatDuration(r)} left");
        if (p.ArchiveBytes > 0)
            parts.Add($"archive {UiLogic.FormatBytes(p.ArchiveBytes)}");
        _status.Text = string.Join("  ·  ", parts);
        if (!string.IsNullOrEmpty(p.Message))
            Log(p.Message);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var t = _running;
        if (t == null || t.IsCompleted)
            return;
        if (MessageBox.Show(this, "A job is running. Stop it and close?\n\nAn interrupted packing can be resumed later.", "LzFilm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _cts?.Cancel();
        _status.Text = "Stopping...";
        t.ContinueWith(_ => BeginInvoke(new Action(() => { _running = null; Close(); })), TaskScheduler.Default);
    }

    // =========================================================================== misc

    private void Log(string line)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(line)));
            return;
        }
        var text = line.Length == 0 ? Environment.NewLine : $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}";
        _log.AppendText(text);
    }

    private static byte[]? LoadStub()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(StubResource);
        if (s == null)
            return null;
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"LzFilm {Version()}\n" +
            "Archiver for files and folders, and for successive versions of large disk images.\n\n" +
            "Files and folders are packed as one solid LZMA (xz) stream. Disk image versions are coded against the " +
            "previous version block by block (unchanged, changed in place, moved) before compression. " +
            "Split volumes, self-extracting archives, resume after interruption and SHA-256 verification are built in.\n\n" +
            "Author: Srećko Jovančević\n" +
            "Acknowledgments:\n" +
            "  Claude (Anthropic) — code elaboration and codebase extension\n" +
            "  Claude (Anthropic) — icon design\n\n" +
            $"LZMA: liblzma {Xz.Version} (xz-utils, 0BSD)\n" +
            "License: MIT\n" +
            "https://github.com/sreckojovancevic/lzfilm",
            "About LzFilm", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
