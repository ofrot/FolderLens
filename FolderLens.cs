using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderLens
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool scanAll = args.Any(x => string.Equals(x, "--all-drives", StringComparison.OrdinalIgnoreCase));
                Application.Run(new MainForm(scanAll));
            }
            catch (Exception ex)
            {
                try
                {
                    string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FolderLens-error.log");
                    File.WriteAllText(log, DateTime.Now.ToString("u") + Environment.NewLine + ex);
                }
                catch { }
                MessageBox.Show("FolderLens encountered an unexpected error. A diagnostic log was saved beside the app.\n\n" + ex.Message,
                    "FolderLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class FolderNode
    {
        public string Name;
        public string Path;
        public long DirectSize;
        public long TotalSize;
        public long FileCount;
        public List<FolderNode> Children = new List<FolderNode>();
    }

    internal sealed class FileEntry
    {
        public string Name;
        public string Path;
        public long Size;
        public DateTime Modified;
    }

    internal sealed class ScanResult
    {
        public FolderNode Root;
        public long TotalSize;
        public long FileCount;
        public long FolderCount;
        public int ErrorCount;
        public TimeSpan Elapsed;
        public List<FileEntry> LargestFiles;
    }

    internal sealed class ScanProgress
    {
        public string CurrentPath;
        public long FileCount;
        public long FolderCount;
        public long Bytes;
        public int ErrorCount;
    }

    internal sealed class SavedScan
    {
        public ScanResult Result;
        public DateTime SavedAt;
    }

    internal static class ResultStore
    {
        private const int Magic = 0x464C5331;
        private const int Version = 1;

        private sealed class FlatNode
        {
            public FolderNode Node;
            public int ParentIndex;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FolderLens", "last-scan.fls");
            }
        }

        public static void Save(ScanResult result)
        {
            Save(result, DefaultPath, DateTime.Now);
        }

        internal static void Save(ScanResult result, string filePath, DateTime savedAt)
        {
            if (result == null || result.Root == null) throw new ArgumentNullException("result");
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                List<FlatNode> nodes = new List<FlatNode>();
                nodes.Add(new FlatNode { Node = result.Root, ParentIndex = -1 });
                for (int i = 0; i < nodes.Count; i++)
                {
                    foreach (FolderNode child in nodes[i].Node.Children)
                        nodes.Add(new FlatNode { Node = child, ParentIndex = i });
                }

                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(savedAt.ToBinary());
                    writer.Write(result.TotalSize);
                    writer.Write(result.FileCount);
                    writer.Write(result.FolderCount);
                    writer.Write(result.ErrorCount);
                    writer.Write(result.Elapsed.Ticks);
                    writer.Write(nodes.Count);
                    foreach (FlatNode flat in nodes)
                    {
                        FolderNode node = flat.Node;
                        writer.Write(flat.ParentIndex);
                        writer.Write(node.Name ?? "");
                        writer.Write(node.Path ?? "");
                        writer.Write(node.DirectSize);
                        writer.Write(node.TotalSize);
                        writer.Write(node.FileCount);
                    }
                    List<FileEntry> files = result.LargestFiles ?? new List<FileEntry>();
                    writer.Write(files.Count);
                    foreach (FileEntry file in files)
                    {
                        writer.Write(file.Name ?? "");
                        writer.Write(file.Path ?? "");
                        writer.Write(file.Size);
                        writer.Write(file.Modified.ToBinary());
                    }
                }

                if (File.Exists(filePath))
                    File.Replace(temporaryPath, filePath, null, true);
                else
                    File.Move(temporaryPath, filePath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public static SavedScan Load()
        {
            return Load(DefaultPath);
        }

        internal static SavedScan Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("Not a FolderLens results file.");
                if (reader.ReadInt32() != Version) throw new InvalidDataException("This results file uses an unsupported version.");
                DateTime savedAt = DateTime.FromBinary(reader.ReadInt64());
                ScanResult result = new ScanResult
                {
                    TotalSize = reader.ReadInt64(),
                    FileCount = reader.ReadInt64(),
                    FolderCount = reader.ReadInt64(),
                    ErrorCount = reader.ReadInt32(),
                    Elapsed = TimeSpan.FromTicks(reader.ReadInt64())
                };
                int nodeCount = reader.ReadInt32();
                if (nodeCount < 1 || nodeCount > 10000000) throw new InvalidDataException("The saved folder count is invalid.");
                FolderNode[] nodes = new FolderNode[nodeCount];
                int[] parents = new int[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    parents[i] = reader.ReadInt32();
                    nodes[i] = new FolderNode
                    {
                        Name = reader.ReadString(),
                        Path = reader.ReadString(),
                        DirectSize = reader.ReadInt64(),
                        TotalSize = reader.ReadInt64(),
                        FileCount = reader.ReadInt64()
                    };
                }
                if (parents[0] != -1) throw new InvalidDataException("The saved folder tree is invalid.");
                for (int i = 1; i < nodeCount; i++)
                {
                    if (parents[i] < 0 || parents[i] >= i) throw new InvalidDataException("The saved folder tree is invalid.");
                    nodes[parents[i]].Children.Add(nodes[i]);
                }
                result.Root = nodes[0];

                int fileCount = reader.ReadInt32();
                if (fileCount < 0 || fileCount > 10000) throw new InvalidDataException("The saved file count is invalid.");
                result.LargestFiles = new List<FileEntry>(fileCount);
                for (int i = 0; i < fileCount; i++)
                {
                    result.LargestFiles.Add(new FileEntry
                    {
                        Name = reader.ReadString(),
                        Path = reader.ReadString(),
                        Size = reader.ReadInt64(),
                        Modified = DateTime.FromBinary(reader.ReadInt64())
                    });
                }
                return new SavedScan { Result = result, SavedAt = savedAt };
            }
        }
    }

    internal static class FolderScanner
    {
        private sealed class WorkItem
        {
            public FolderNode Node;
            public bool Closing;
        }

        public static ScanResult Scan(string rootPath, CancellationToken token, Action<ScanProgress> report)
        {
            Stopwatch clock = Stopwatch.StartNew();
            FolderNode root = new FolderNode
            {
                Name = GetDisplayName(rootPath),
                Path = rootPath
            };
            List<FileEntry> largest = new List<FileEntry>();
            long smallestLargestFile = 0;
            Stack<WorkItem> stack = new Stack<WorkItem>();
            stack.Push(new WorkItem { Node = root, Closing = false });

            long filesSeen = 0;
            long foldersSeen = 0;
            long bytesSeen = 0;
            int errors = 0;
            long lastReport = 0;

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                WorkItem item = stack.Pop();
                if (item.Closing)
                {
                    long total = item.Node.DirectSize;
                    long count = item.Node.FileCount;
                    foreach (FolderNode child in item.Node.Children)
                    {
                        total += child.TotalSize;
                        count += child.FileCount;
                    }
                    item.Node.TotalSize = total;
                    item.Node.FileCount = count;
                    continue;
                }

                foldersSeen++;
                stack.Push(new WorkItem { Node = item.Node, Closing = true });

                FileInfo[] files;
                try
                {
                    files = new DirectoryInfo(item.Node.Path).GetFiles();
                }
                catch
                {
                    errors++;
                    files = new FileInfo[0];
                }

                foreach (FileInfo file in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        long size = file.Length;
                        item.Node.DirectSize += size;
                        item.Node.FileCount++;
                        filesSeen++;
                        bytesSeen += size;
                        AddLargest(largest, new FileEntry
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            Size = size,
                            Modified = file.LastWriteTime
                        }, 500, ref smallestLargestFile);
                    }
                    catch
                    {
                        errors++;
                    }
                }

                DirectoryInfo[] directories;
                try
                {
                    directories = new DirectoryInfo(item.Node.Path).GetDirectories();
                }
                catch
                {
                    errors++;
                    directories = new DirectoryInfo[0];
                }

                Array.Sort(directories, delegate(DirectoryInfo a, DirectoryInfo b)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                });

                for (int i = directories.Length - 1; i >= 0; i--)
                {
                    DirectoryInfo directory = directories[i];
                    try
                    {
                        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        FolderNode child = new FolderNode
                        {
                            Name = directory.Name,
                            Path = directory.FullName
                        };
                        item.Node.Children.Add(child);
                        stack.Push(new WorkItem { Node = child, Closing = false });
                    }
                    catch
                    {
                        errors++;
                    }
                }

                long now = clock.ElapsedMilliseconds;
                if (report != null && now - lastReport >= 90)
                {
                    lastReport = now;
                    report(new ScanProgress
                    {
                        CurrentPath = item.Node.Path,
                        FileCount = filesSeen,
                        FolderCount = foldersSeen,
                        Bytes = bytesSeen,
                        ErrorCount = errors
                    });
                }
            }

            clock.Stop();
            largest.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });
            if (report != null)
            {
                report(new ScanProgress
                {
                    CurrentPath = rootPath,
                    FileCount = filesSeen,
                    FolderCount = foldersSeen,
                    Bytes = root.TotalSize,
                    ErrorCount = errors
                });
            }
            return new ScanResult
            {
                Root = root,
                TotalSize = root.TotalSize,
                FileCount = filesSeen,
                FolderCount = foldersSeen,
                ErrorCount = errors,
                Elapsed = clock.Elapsed,
                LargestFiles = largest
            };
        }

        public static ScanResult ScanMany(string[] rootPaths, CancellationToken token, Action<ScanProgress> report)
        {
            Stopwatch clock = Stopwatch.StartNew();
            FolderNode combinedRoot = new FolderNode { Name = "All drives", Path = "" };
            List<FileEntry> largestFiles = new List<FileEntry>();
            long totalBytes = 0;
            long totalFiles = 0;
            long totalFolders = 0;
            int totalErrors = 0;

            foreach (string rootPath in rootPaths)
            {
                token.ThrowIfCancellationRequested();
                long bytesBefore = totalBytes;
                long filesBefore = totalFiles;
                long foldersBefore = totalFolders;
                int errorsBefore = totalErrors;
                ScanResult drive = Scan(rootPath, token, report == null ? null : (Action<ScanProgress>)delegate(ScanProgress value)
                {
                    report(new ScanProgress
                    {
                        CurrentPath = value.CurrentPath,
                        Bytes = bytesBefore + value.Bytes,
                        FileCount = filesBefore + value.FileCount,
                        FolderCount = foldersBefore + value.FolderCount,
                        ErrorCount = errorsBefore + value.ErrorCount
                    });
                });
                combinedRoot.Children.Add(drive.Root);
                totalBytes += drive.TotalSize;
                totalFiles += drive.FileCount;
                totalFolders += drive.FolderCount;
                totalErrors += drive.ErrorCount;
                largestFiles.AddRange(drive.LargestFiles);
            }

            combinedRoot.TotalSize = totalBytes;
            combinedRoot.FileCount = totalFiles;
            clock.Stop();
            return new ScanResult
            {
                Root = combinedRoot,
                TotalSize = totalBytes,
                FileCount = totalFiles,
                FolderCount = totalFolders,
                ErrorCount = totalErrors,
                Elapsed = clock.Elapsed,
                LargestFiles = largestFiles.OrderByDescending(x => x.Size).Take(500).ToList()
            };
        }

        private static void AddLargest(List<FileEntry> entries, FileEntry candidate, int limit, ref long smallestSize)
        {
            if (entries.Count < limit)
            {
                entries.Add(candidate);
                if (entries.Count == limit)
                    smallestSize = entries.Min(x => x.Size);
                return;
            }
            if (candidate.Size <= smallestSize) return;
            int smallestIndex = 0;
            long smallest = entries[0].Size;
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i].Size < smallest)
                {
                    smallest = entries[i].Size;
                    smallestIndex = i;
                }
            }
            if (candidate.Size > smallest)
                entries[smallestIndex] = candidate;
            smallestSize = entries.Min(x => x.Size);
        }

        private static string GetDisplayName(string path)
        {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }

    internal static class SizeFormatter
    {
        public static string Format(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            if (unit == 0) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";
            return value.ToString(value >= 100 ? "N0" : value >= 10 ? "N1" : "N2", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    internal sealed class SpaceChart : Control
    {
        private List<FolderNode> data = new List<FolderNode>();
        private long total;
        private readonly Color[] palette =
        {
            Color.FromArgb(89, 126, 247), Color.FromArgb(118, 92, 230),
            Color.FromArgb(46, 170, 145), Color.FromArgb(236, 157, 74),
            Color.FromArgb(226, 93, 117), Color.FromArgb(66, 160, 213),
            Color.FromArgb(141, 181, 76), Color.FromArgb(194, 115, 211)
        };

        public SpaceChart()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9F);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void SetData(FolderNode node)
        {
            data = node == null ? new List<FolderNode>() : node.Children.OrderByDescending(x => x.TotalSize).Take(8).ToList();
            total = node == null ? 0 : node.TotalSize;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle area = ClientRectangle;
            area.Inflate(-18, -12);
            if (data.Count == 0 || total <= 0)
            {
                using (Brush brush = new SolidBrush(Color.FromArgb(121, 129, 145)))
                    g.DrawString("No subfolder data to chart", Font, brush, area, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                return;
            }

            int rowHeight = Math.Max(31, Math.Min(43, area.Height / data.Count));
            int labelWidth = Math.Max(120, Math.Min(210, area.Width / 3));
            int valueWidth = 82;
            int barWidth = Math.Max(30, area.Width - labelWidth - valueWidth - 24);
            long max = Math.Max(1, data[0].TotalSize);

            for (int i = 0; i < data.Count; i++)
            {
                FolderNode node = data[i];
                int y = area.Top + i * rowHeight;
                string name = node.Name;
                if (name.Length > 25) name = name.Substring(0, 22) + "...";
                using (Brush textBrush = new SolidBrush(Color.FromArgb(45, 51, 64)))
                    g.DrawString(name, Font, textBrush, new Rectangle(area.Left, y + 5, labelWidth - 8, 22));

                Rectangle track = new Rectangle(area.Left + labelWidth, y + 7, barWidth, 13);
                using (Brush trackBrush = new SolidBrush(Color.FromArgb(235, 238, 244)))
                    FillRounded(g, trackBrush, track, 6);
                int filled = Math.Max(node.TotalSize > 0 ? 3 : 0, (int)Math.Round(barWidth * (double)node.TotalSize / max));
                Rectangle bar = new Rectangle(track.X, track.Y, Math.Min(barWidth, filled), track.Height);
                using (Brush barBrush = new SolidBrush(palette[i % palette.Length]))
                    FillRounded(g, barBrush, bar, 6);

                using (Brush valueBrush = new SolidBrush(Color.FromArgb(84, 91, 107)))
                    g.DrawString(SizeFormatter.Format(node.TotalSize), Font, valueBrush, new Rectangle(track.Right + 10, y + 3, valueWidth, 23), new StringFormat { Alignment = StringAlignment.Far });
            }
        }

        private static void FillRounded(Graphics g, Brush brush, Rectangle bounds, int radius)
        {
            if (bounds.Width <= radius * 2)
            {
                g.FillRectangle(brush, bounds);
                return;
            }
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
                path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
                path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color background = Color.FromArgb(244, 246, 250);
        private readonly Color navy = Color.FromArgb(31, 38, 53);
        private readonly Color muted = Color.FromArgb(108, 116, 133);
        private readonly Color accent = Color.FromArgb(78, 112, 238);

        private TextBox folderBox;
        private Button browseButton;
        private Button allDrivesButton;
        private Button scanButton;
        private Button cancelButton;
        private Button exportButton;
        private Label statusLabel;
        private Label pathStatusLabel;
        private ProgressBar progress;
        private Label sizeValue;
        private Label filesValue;
        private Label foldersValue;
        private Label issuesValue;
        private TabControl tabs;
        private SpaceChart chart;
        private DataGridView folderGrid;
        private TreeView folderTree;
        private DataGridView fileGrid;
        private TextBox fileFilter;
        private Label chartTitle;
        private ScanResult result;
        private CancellationTokenSource cancellation;
        private readonly bool autoScanAllDrives;
        private readonly bool persistenceEnabled;

        public MainForm() : this(false, true)
        {
        }

        public MainForm(bool autoScanAll) : this(autoScanAll, true)
        {
        }

        internal MainForm(bool autoScanAll, bool enablePersistence)
        {
            autoScanAllDrives = autoScanAll;
            persistenceEnabled = enablePersistence;
            Text = "FolderLens — Storage Analyzer";
            Icon = SystemIcons.Information;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 650);
            Size = new Size(1180, 790);
            BackColor = background;
            Font = new Font("Segoe UI", 9F);
            BuildInterface();
            Shown += async delegate
            {
                if (autoScanAllDrives)
                    await StartScanAll();
                else
                {
                    folderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    LoadLastResult();
                }
            };
            FormClosing += OnFormClosing;
        }

        private void BuildInterface()
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = navy, Padding = new Padding(24, 0, 24, 0) };
            Label logo = new Label
            {
                Text = "◒  FolderLens",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 18F),
                AutoSize = true,
                Location = new Point(24, 16)
            };
            Label subtitle = new Label
            {
                Text = "See what is using your disk space",
                ForeColor = Color.FromArgb(177, 185, 203),
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Location = new Point(61, 48)
            };
            header.Controls.Add(logo);
            header.Controls.Add(subtitle);

            Panel content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 18), BackColor = background };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            layout.Controls.Add(BuildPicker(), 0, 0);
            layout.Controls.Add(BuildSummary(), 0, 1);
            layout.Controls.Add(BuildProgressArea(), 0, 2);
            layout.Controls.Add(BuildTabs(), 0, 3);
            layout.Controls.Add(BuildFooter(), 0, 4);
            content.Controls.Add(layout);
            Controls.Add(content);
            Controls.Add(header);
        }

        private Control BuildPicker()
        {
            Panel card = Card();
            card.Padding = new Padding(16, 15, 16, 13);
            folderBox = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10F),
                Location = new Point(16, 19),
                Height = 30,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            browseButton = PrimaryButton("Choose folder", Color.FromArgb(226, 230, 240), navy);
            browseButton.Size = new Size(113, 34);
            browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browseButton.Click += Browse;
            allDrivesButton = PrimaryButton("All drives", Color.FromArgb(226, 230, 240), navy);
            allDrivesButton.Size = new Size(96, 34);
            allDrivesButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            allDrivesButton.Click += async delegate { await StartScanAll(); };
            scanButton = PrimaryButton("Analyze", accent, Color.White);
            scanButton.Size = new Size(104, 34);
            scanButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            scanButton.Click += async delegate { await StartScan(); };
            cancelButton = PrimaryButton("Cancel", Color.FromArgb(235, 93, 102), Color.White);
            cancelButton.Size = new Size(88, 34);
            cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancelButton.Visible = false;
            cancelButton.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            card.Resize += delegate
            {
                scanButton.Location = new Point(card.ClientSize.Width - 120, 14);
                cancelButton.Location = new Point(card.ClientSize.Width - 216, 14);
                allDrivesButton.Location = new Point(card.ClientSize.Width - (cancelButton.Visible ? 320 : 224), 14);
                browseButton.Location = new Point(card.ClientSize.Width - (cancelButton.Visible ? 441 : 345), 14);
                folderBox.Width = Math.Max(120, browseButton.Left - 28);
            };
            card.Controls.Add(folderBox);
            card.Controls.Add(browseButton);
            card.Controls.Add(allDrivesButton);
            card.Controls.Add(cancelButton);
            card.Controls.Add(scanButton);
            return card;
        }

        private Control BuildSummary()
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(0, 10, 0, 7) };
            for (int i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            sizeValue = new Label();
            filesValue = new Label();
            foldersValue = new Label();
            issuesValue = new Label();
            row.Controls.Add(SummaryCard("TOTAL SIZE", sizeValue, "0 B", Color.FromArgb(78, 112, 238)), 0, 0);
            row.Controls.Add(SummaryCard("FILES", filesValue, "0", Color.FromArgb(46, 170, 145)), 1, 0);
            row.Controls.Add(SummaryCard("FOLDERS", foldersValue, "0", Color.FromArgb(236, 157, 74)), 2, 0);
            row.Controls.Add(SummaryCard("SKIPPED ITEMS", issuesValue, "0", Color.FromArgb(226, 93, 117)), 3, 0);
            return row;
        }

        private Control SummaryCard(string title, Label valueLabel, string initial, Color color)
        {
            Panel host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0) };
            Panel card = Card();
            Panel stripe = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = color };
            Label titleLabel = new Label { Text = title, ForeColor = muted, Font = new Font("Segoe UI Semibold", 8.5F), AutoSize = true, Location = new Point(20, 13) };
            valueLabel.Text = initial;
            valueLabel.ForeColor = navy;
            valueLabel.Font = new Font("Segoe UI Semibold", 16F);
            valueLabel.AutoSize = true;
            valueLabel.Location = new Point(19, 34);
            card.Controls.Add(stripe);
            card.Controls.Add(titleLabel);
            card.Controls.Add(valueLabel);
            host.Controls.Add(card);
            return host;
        }

        private Control BuildProgressArea()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill };
            progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 4, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false };
            statusLabel = new Label { Text = "Choose a folder to begin", AutoSize = true, ForeColor = muted, Location = new Point(2, 9) };
            pathStatusLabel = new Label { Text = "", AutoEllipsis = true, ForeColor = Color.FromArgb(141, 148, 162), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(400, 9) };
            panel.Resize += delegate { pathStatusLabel.Width = panel.Width / 2; pathStatusLabel.Left = panel.Width - pathStatusLabel.Width; };
            panel.Controls.Add(statusLabel);
            panel.Controls.Add(pathStatusLabel);
            panel.Controls.Add(progress);
            return panel;
        }

        private Control BuildTabs()
        {
            tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 9F), Padding = new Point(16, 7) };
            TabPage overview = new TabPage("Overview") { BackColor = Color.White, Padding = new Padding(12) };
            TabPage treePage = new TabPage("Folder tree") { BackColor = Color.White, Padding = new Padding(8) };
            TabPage filesPage = new TabPage("Largest files") { BackColor = Color.White, Padding = new Padding(8) };

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Width = 1050, SplitterDistance = 575, BackColor = Color.FromArgb(229, 232, 239), Panel1MinSize = 320, Panel2MinSize = 320 };
            Panel chartPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(0, 38, 0, 0) };
            chartTitle = new Label { Text = "Largest subfolders", ForeColor = navy, Font = new Font("Segoe UI Semibold", 11F), AutoSize = true, Location = new Point(7, 7) };
            chart = new SpaceChart { Dock = DockStyle.Fill };
            chartPanel.Controls.Add(chart);
            chartPanel.Controls.Add(chartTitle);
            folderGrid = MakeGrid();
            folderGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FolderName", HeaderText = "Folder", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45 });
            folderGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FolderSize", HeaderText = "Size", Width = 95 });
            folderGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FolderPercent", HeaderText = "%", Width = 58 });
            folderGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FolderFiles", HeaderText = "Files", Width = 80 });
            folderGrid.CellDoubleClick += OpenFolderGridItem;
            split.Panel1.Controls.Add(chartPanel);
            split.Panel2.Controls.Add(folderGrid);
            overview.Controls.Add(split);

            folderTree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5F), HideSelection = false, ShowNodeToolTips = true };
            folderTree.BeforeExpand += PopulateTreeBranch;
            folderTree.AfterSelect += delegate(object sender, TreeViewEventArgs e) { SelectFolderNode(e.Node); };
            folderTree.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs e) { OpenPath(((FolderNode)e.Node.Tag).Path, true); };
            treePage.Controls.Add(folderTree);

            Panel fileTools = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.White };
            Label filterLabel = new Label { Text = "Filter:", AutoSize = true, ForeColor = muted, Location = new Point(2, 12) };
            fileFilter = new TextBox { Location = new Point(48, 7), Width = 260, Font = new Font("Segoe UI", 9.5F) };
            fileFilter.TextChanged += delegate { PopulateFiles(); };
            exportButton = PrimaryButton("Export CSV", Color.FromArgb(226, 230, 240), navy);
            exportButton.Size = new Size(102, 30);
            exportButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exportButton.Enabled = false;
            exportButton.Click += ExportCsv;
            fileTools.Resize += delegate { exportButton.Left = fileTools.Width - exportButton.Width - 2; };
            fileTools.Controls.Add(filterLabel);
            fileTools.Controls.Add(fileFilter);
            fileTools.Controls.Add(exportButton);

            fileGrid = MakeGrid();
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = "File", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 35 });
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileSize", HeaderText = "Size", Width = 100 });
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileModified", HeaderText = "Modified", Width = 135 });
            fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FilePath", HeaderText = "Location", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 65 });
            fileGrid.CellDoubleClick += OpenFileGridItem;
            filesPage.Controls.Add(fileGrid);
            filesPage.Controls.Add(fileTools);

            tabs.TabPages.Add(overview);
            tabs.TabPages.Add(treePage);
            tabs.TabPages.Add(filesPage);
            return tabs;
        }

        private Control BuildFooter()
        {
            Label label = new Label
            {
                Text = "Tip: double-click a folder or file to open it in File Explorer.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = muted,
                Font = new Font("Segoe UI", 8.5F)
            };
            return label;
        }

        private Panel Card()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
        }

        private Button PrimaryButton(string text, Color back, Color fore)
        {
            Button button = new Button
            {
                Text = text,
                BackColor = back,
                ForeColor = fore,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9F),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private DataGridView MakeGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 36,
                RowTemplate = { Height = 31 },
                GridColor = Color.FromArgb(234, 236, 242)
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 248, 251);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = muted;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = muted;
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(48, 54, 67);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 232, 255);
            grid.DefaultCellStyle.SelectionForeColor = navy;
            grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            return grid;
        }

        private void Browse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder or drive to analyze";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(folderBox.Text)) dialog.SelectedPath = folderBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    folderBox.Text = dialog.SelectedPath;
            }
        }

        private async Task StartScan()
        {
            string path = folderBox.Text.Trim().Trim('"');
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "That folder does not exist or is not accessible.", "FolderLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            path = Path.GetFullPath(path);
            folderBox.Text = path;
            SetScanning(true);
            ResetResults();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            try
            {
                result = await Task.Run(delegate
                {
                    return FolderScanner.Scan(path, token, delegate(ScanProgress value)
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((Action)delegate { UpdateProgress(value); });
                    });
                }, token);
                PopulateResult();
                statusLabel.Text = "Saving last result…";
                bool saved = await SaveLastResult();
                statusLabel.Text = string.Format("Scan complete in {0:N1} seconds{1}", result.Elapsed.TotalSeconds, saved ? " • saved" : "");
                pathStatusLabel.Text = (result.ErrorCount > 0 ? result.ErrorCount.ToString("N0") + " inaccessible item(s) skipped" : "Everything accessible was scanned") + (saved ? " • available next launch" : " • result could not be saved");
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Scan cancelled";
                pathStatusLabel.Text = "";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Scan failed";
                pathStatusLabel.Text = "";
                MessageBox.Show(this, "FolderLens could not scan this location.\n\n" + ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetScanning(false);
                if (cancellation != null) cancellation.Dispose();
                cancellation = null;
            }
        }

        private async Task StartScanAll()
        {
            string[] paths = GetReadyLocalDrives();
            if (paths.Length == 0)
            {
                MessageBox.Show(this, "No ready local drives were found.", "FolderLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            folderBox.Text = "All local drives: " + string.Join(", ", paths);
            SetScanning(true);
            ResetResults();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            try
            {
                result = await Task.Run(delegate
                {
                    return FolderScanner.ScanMany(paths, token, delegate(ScanProgress value)
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((Action)delegate { UpdateProgress(value); });
                    });
                }, token);
                PopulateResult();
                statusLabel.Text = "Saving last result…";
                bool saved = await SaveLastResult();
                statusLabel.Text = string.Format("All-drive scan complete in {0:N1} seconds{1}", result.Elapsed.TotalSeconds, saved ? " • saved" : "");
                pathStatusLabel.Text = (result.ErrorCount > 0 ? result.ErrorCount.ToString("N0") + " inaccessible item(s) skipped" : "Everything accessible was scanned") + (saved ? " • available next launch" : " • result could not be saved");
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Scan cancelled";
                pathStatusLabel.Text = "";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Scan failed";
                pathStatusLabel.Text = "";
                MessageBox.Show(this, "FolderLens could not complete the all-drive scan.\n\n" + ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetScanning(false);
                if (cancellation != null) cancellation.Dispose();
                cancellation = null;
            }
        }

        private static string[] GetReadyLocalDrives()
        {
            return DriveInfo.GetDrives()
                .Where(x => (x.DriveType == DriveType.Fixed || x.DriveType == DriveType.Removable) && x.IsReady)
                .Select(x => x.RootDirectory.FullName)
                .ToArray();
        }

        private async Task<bool> SaveLastResult()
        {
            if (!persistenceEnabled) return true;
            ScanResult snapshot = result;
            try
            {
                await Task.Run(delegate { ResultStore.Save(snapshot); });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void LoadLastResult()
        {
            if (!persistenceEnabled) return;
            try
            {
                SavedScan saved = ResultStore.Load();
                if (saved == null) return;
                result = saved.Result;
                folderBox.Text = string.IsNullOrEmpty(result.Root.Path) ? "All local drives (saved result)" : result.Root.Path;
                PopulateResult();
                statusLabel.Text = "Loaded last scan from " + saved.SavedAt.ToString("g");
                pathStatusLabel.Text = "Saved locally • run Analyze to refresh";
            }
            catch
            {
                statusLabel.Text = "The previous saved scan could not be loaded";
                pathStatusLabel.Text = "Run a new scan to replace it";
            }
        }

        private void SetScanning(bool scanning)
        {
            browseButton.Enabled = !scanning;
            allDrivesButton.Enabled = !scanning;
            scanButton.Enabled = !scanning;
            folderBox.Enabled = !scanning;
            cancelButton.Visible = scanning;
            progress.Visible = scanning;
            if (scanning)
            {
                statusLabel.Text = "Scanning…";
                pathStatusLabel.Text = "";
            }
            PerformLayout();
        }

        private void UpdateProgress(ScanProgress value)
        {
            if (cancellation == null) return;
            statusLabel.Text = string.Format("Scanning… {0:N0} files • {1}", value.FileCount, SizeFormatter.Format(value.Bytes));
            pathStatusLabel.Text = value.CurrentPath;
            sizeValue.Text = SizeFormatter.Format(value.Bytes);
            filesValue.Text = value.FileCount.ToString("N0");
            foldersValue.Text = value.FolderCount.ToString("N0");
            issuesValue.Text = value.ErrorCount.ToString("N0");
        }

        private void ResetResults()
        {
            result = null;
            sizeValue.Text = "0 B";
            filesValue.Text = "0";
            foldersValue.Text = "0";
            issuesValue.Text = "0";
            folderGrid.Rows.Clear();
            fileGrid.Rows.Clear();
            folderTree.Nodes.Clear();
            chart.SetData(null);
            exportButton.Enabled = false;
        }

        private void PopulateResult()
        {
            sizeValue.Text = SizeFormatter.Format(result.TotalSize);
            filesValue.Text = result.FileCount.ToString("N0");
            foldersValue.Text = result.FolderCount.ToString("N0");
            issuesValue.Text = result.ErrorCount.ToString("N0");
            PopulateFolderGrid(result.Root);
            chart.SetData(result.Root);
            chartTitle.Text = "Largest subfolders in " + result.Root.Name;
            BuildTree();
            PopulateFiles();
            exportButton.Enabled = result.LargestFiles.Count > 0;
        }

        private void PopulateFolderGrid(FolderNode parent)
        {
            folderGrid.Rows.Clear();
            IEnumerable<FolderNode> children = parent.Children.OrderByDescending(x => x.TotalSize);
            foreach (FolderNode child in children)
            {
                double percentage = parent.TotalSize == 0 ? 0 : child.TotalSize * 100.0 / parent.TotalSize;
                int index = folderGrid.Rows.Add(child.Name, SizeFormatter.Format(child.TotalSize), percentage.ToString("N1") + "%", child.FileCount.ToString("N0"));
                folderGrid.Rows[index].Tag = child;
            }
            if (parent.DirectSize > 0)
            {
                double percentage = parent.TotalSize == 0 ? 0 : parent.DirectSize * 100.0 / parent.TotalSize;
                int index = folderGrid.Rows.Add("[Files directly in this folder]", SizeFormatter.Format(parent.DirectSize), percentage.ToString("N1") + "%", "—");
                folderGrid.Rows[index].Tag = parent;
            }
        }

        private void BuildTree()
        {
            folderTree.BeginUpdate();
            folderTree.Nodes.Clear();
            TreeNode rootTreeNode = MakeTreeNode(result.Root);
            folderTree.Nodes.Add(rootTreeNode);
            rootTreeNode.Expand();
            folderTree.SelectedNode = rootTreeNode;
            folderTree.EndUpdate();
        }

        private void PopulateTreeBranch(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null || !(e.Node.Tag is FolderNode)) return;
            if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag != null) return;
            FolderNode folderParent = (FolderNode)e.Node.Tag;
            e.Node.Nodes.Clear();
            foreach (FolderNode child in folderParent.Children.OrderByDescending(x => x.TotalSize))
            {
                TreeNode node = MakeTreeNode(child);
                e.Node.Nodes.Add(node);
            }
        }

        private TreeNode MakeTreeNode(FolderNode folder)
        {
            TreeNode node = new TreeNode(folder.Name + "   —   " + SizeFormatter.Format(folder.TotalSize))
            {
                Tag = folder,
                ToolTipText = folder.Path + "\n" + folder.FileCount.ToString("N0") + " files"
            };
            if (folder.Children.Count > 0)
                node.Nodes.Add(new TreeNode("Loading…"));
            return node;
        }

        private void SelectFolderNode(TreeNode node)
        {
            if (node == null || !(node.Tag is FolderNode)) return;
            FolderNode folder = (FolderNode)node.Tag;
            PopulateFolderGrid(folder);
            chart.SetData(folder);
            chartTitle.Text = "Largest subfolders in " + folder.Name;
        }

        private void PopulateFiles()
        {
            fileGrid.Rows.Clear();
            if (result == null) return;
            string filter = fileFilter.Text.Trim();
            foreach (FileEntry file in result.LargestFiles)
            {
                if (filter.Length > 0 && file.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0 && file.Path.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;
                int index = fileGrid.Rows.Add(file.Name, SizeFormatter.Format(file.Size), file.Modified.ToString("g"), Path.GetDirectoryName(file.Path));
                fileGrid.Rows[index].Tag = file;
            }
        }

        private void OpenFolderGridItem(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            FolderNode node = folderGrid.Rows[e.RowIndex].Tag as FolderNode;
            if (node != null) OpenPath(node.Path, true);
        }

        private void OpenFileGridItem(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            FileEntry file = fileGrid.Rows[e.RowIndex].Tag as FileEntry;
            if (file != null) OpenPath(file.Path, false);
        }

        private void OpenPath(string path, bool folder)
        {
            try
            {
                if (folder)
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open File Explorer.\n\n" + ex.Message, "FolderLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExportCsv(object sender, EventArgs e)
        {
            if (result == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Export largest files";
                dialog.Filter = "CSV file (*.csv)|*.csv";
                dialog.FileName = "FolderLens-largest-files.csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (StreamWriter writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("Name,Size (bytes),Size,Modified,Path");
                        foreach (FileEntry file in result.LargestFiles)
                        {
                            writer.WriteLine(string.Join(",", new string[]
                            {
                                Csv(file.Name), file.Size.ToString(CultureInfo.InvariantCulture), Csv(SizeFormatter.Format(file.Size)),
                                Csv(file.Modified.ToString("s")), Csv(file.Path)
                            }));
                        }
                    }
                    statusLabel.Text = "Exported largest files to " + dialog.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not export the CSV file.\n\n" + ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (cancellation != null) cancellation.Cancel();
        }
    }
}
