using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderLens
{
    internal static class Tests
    {
        private static int failures;

        [STAThread]
        private static void Main()
        {
            string fixture = Path.Combine(Environment.CurrentDirectory, ".folderlens-test-fixture");
            try
            {
                if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
                Directory.CreateDirectory(Path.Combine(fixture, "Alpha", "Nested"));
                Directory.CreateDirectory(Path.Combine(fixture, "Empty"));
                File.WriteAllBytes(Path.Combine(fixture, "root.dat"), new byte[10]);
                File.WriteAllBytes(Path.Combine(fixture, "Alpha", "alpha.dat"), new byte[1000]);
                File.WriteAllBytes(Path.Combine(fixture, "Alpha", "Nested", "big.dat"), new byte[2048]);

                int progressCalls = 0;
                ScanResult result = FolderScanner.Scan(fixture, CancellationToken.None, delegate(ScanProgress p) { progressCalls++; });
                Check(result.TotalSize == 3058, "total byte count");
                Check(result.FileCount == 3, "file count");
                Check(result.FolderCount == 4, "folder count includes root");
                Check(result.ErrorCount == 0, "no inaccessible items");
                Check(result.LargestFiles.Count == 3, "largest-file result count");
                Check(result.LargestFiles[0].Name == "big.dat" && result.LargestFiles[0].Size == 2048, "largest files are ordered");
                FolderNode alpha = result.Root.Children.Single(x => x.Name == "Alpha");
                FolderNode nested = alpha.Children.Single(x => x.Name == "Nested");
                Check(alpha.TotalSize == 3048, "recursive folder total");
                Check(alpha.DirectSize == 1000, "direct folder total");
                Check(nested.TotalSize == 2048, "nested folder total");
                Check(progressCalls > 0, "progress callback");
                Check(SizeFormatter.Format(1024).Contains("KB"), "size formatter");

                CancellationTokenSource cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                bool cancellationObserved = false;
                try
                {
                    FolderScanner.Scan(fixture, cancelled.Token, null);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                }
                Check(cancellationObserved, "pre-cancelled scan stops");

                string secondFixture = Path.Combine(Environment.CurrentDirectory, ".folderlens-test-fixture-2");
                if (Directory.Exists(secondFixture)) Directory.Delete(secondFixture, true);
                Directory.CreateDirectory(secondFixture);
                File.WriteAllBytes(Path.Combine(secondFixture, "second.dat"), new byte[512]);
                ScanResult combined = FolderScanner.ScanMany(new[] { fixture, secondFixture }, CancellationToken.None, null);
                Check(combined.Root.Children.Count == 2, "combined scan includes each root");
                Check(combined.TotalSize == 3570 && combined.FileCount == 4, "combined scan aggregates totals");

                string savedResultPath = Path.Combine(Environment.CurrentDirectory, ".folderlens-test-results.fls");
                if (File.Exists(savedResultPath)) File.Delete(savedResultPath);
                DateTime savedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
                ResultStore.Save(combined, savedResultPath, savedAt);
                SavedScan restored = ResultStore.Load(savedResultPath);
                Check(restored != null && restored.SavedAt == savedAt, "saved scan preserves timestamp");
                Check(restored.Result.TotalSize == combined.TotalSize && restored.Result.FileCount == combined.FileCount, "saved scan preserves summary");
                Check(restored.Result.Root.Children.Count == 2 && restored.Result.Root.Children[0].Children.Count == combined.Root.Children[0].Children.Count, "saved scan preserves folder tree");
                Check(restored.Result.LargestFiles.Count == 4 && restored.Result.LargestFiles[0].Name == "big.dat", "saved scan preserves largest files");
                File.Delete(savedResultPath);
                Directory.Delete(secondFixture, true);

                RunUiSmokeTest(fixture);
            }
            finally
            {
                if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
            }

            if (failures > 0)
            {
                Console.Error.WriteLine("FAILED: " + failures + " test(s)");
                Environment.Exit(1);
            }
            Console.WriteLine("All FolderLens scanner tests passed.");
        }

        private static void RunUiSmokeTest(string fixture)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm(false, false))
            {
                form.Show();
                Application.DoEvents();
                TextBox folderBox = (TextBox)GetField(form, "folderBox");
                folderBox.Text = fixture;
                MethodInfo startScan = typeof(MainForm).GetMethod("StartScan", BindingFlags.Instance | BindingFlags.NonPublic);
                Task scan = (Task)startScan.Invoke(form, null);
                DateTime timeout = DateTime.UtcNow.AddSeconds(10);
                while (!scan.IsCompleted && DateTime.UtcNow < timeout)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                Check(scan.IsCompleted, "UI scan completes without blocking");
                if (scan.IsCompleted) scan.GetAwaiter().GetResult();
                Application.DoEvents();

                ScanResult uiResult = (ScanResult)GetField(form, "result");
                DataGridView folderGrid = (DataGridView)GetField(form, "folderGrid");
                DataGridView fileGrid = (DataGridView)GetField(form, "fileGrid");
                TreeView folderTree = (TreeView)GetField(form, "folderTree");
                Button exportButton = (Button)GetField(form, "exportButton");
                Label sizeValue = (Label)GetField(form, "sizeValue");
                Check(uiResult != null && uiResult.TotalSize == 3058, "UI receives scan result");
                Check(folderGrid.Rows.Count == 3, "overview folder table is populated");
                Check(fileGrid.Rows.Count == 3, "largest-files table is populated");
                Check(folderTree.Nodes.Count == 1, "folder tree is populated");
                Check(exportButton.Enabled, "CSV export becomes available");
                Check(sizeValue.Text == SizeFormatter.Format(3058), "summary size is displayed");
                form.Close();
            }
        }

        private static object GetField(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(instance.GetType().FullName, name);
            return field.GetValue(instance);
        }

        private static void Check(bool condition, string name)
        {
            if (condition)
                Console.WriteLine("PASS  " + name);
            else
            {
                Console.Error.WriteLine("FAIL  " + name);
                failures++;
            }
        }
    }
}
