using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskVisualizer
{
    internal static class SelfTests
    {
        private static int passed;
        private static readonly List<string> report = new List<string>();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CreateSymbolicLinkW(string link, string target, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "DiskVisualizer-test-" + Guid.NewGuid().ToString("N"));
            int code = 0;
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                Directory.CreateDirectory(Path.Combine(root, "empty"));
                File.WriteAllBytes(Path.Combine(root, "first.bin"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(root, "nested", "second.bin"), new byte[3072]);
                File.WriteAllBytes(Path.Combine(root, "nested", "文档.txt"), new byte[17]);
                File.WriteAllBytes(Path.Combine(root, "zero.txt"), new byte[0]);
                ScanResult scan = Scanner.Scan(root, CancellationToken.None, null);
                Check(scan.Nodes[0].Bytes == 4113, "Logical totals match independently specified fixture (4113 bytes)");
                Check(scan.Nodes.Count == 7, "All six files/folders represented, including empty entries");
                Check(scan.Nodes[0].Descendants == 6, "Descendant counts aggregate correctly");
                Check(scan.ErrorCount == 0 && !scan.Nodes[0].Incomplete, "Ordinary fixture has complete coverage");
                int unicode = scan.Nodes.FindIndex(n => n.Name == "文档.txt");
                Check(unicode > 0 && File.Exists(scan.PathFor(unicode)), "Unicode names and path reconstruction");
                int nested = scan.Nodes.FindIndex(n => n.Name == "nested");
                Check(scan.Nodes[nested].Bytes == 3089, "Subtree total is independent of enumeration order");
                Check(scan.IsAncestor(nested, unicode) && !scan.IsAncestor(unicode, nested), "Collection ancestry distinguishes parents and descendants");
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    ScanResult canceled = Scanner.Scan(root, cts.Token, null);
                    Check(canceled.Canceled && canceled.Nodes[0].Incomplete && canceled.Nodes.Count == 1, "Pre-canceled scans preserve explicitly partial results");
                }
                bool invalidFailed = false;
                try { Scanner.Scan(Path.Combine(root, "missing"), CancellationToken.None, null); } catch { invalidFailed = true; }
                Check(invalidFailed, "Missing root fails instead of reporting empty success");
                Check(Format.Size(1024) == "1.0 KiB" || Format.Size(1024) == "1,0 KiB", "Binary size formatting");
                string deep = root;
                for (int i = 0; i < 20; i++) deep = Path.Combine(deep, "nested-folder-" + i);
                Directory.CreateDirectory(Scanner.Extended(deep));
                File.WriteAllBytes(Scanner.Extended(Path.Combine(deep, "long.txt")), new byte[11]);
                scan = Scanner.Scan(root, CancellationToken.None, null);
                Check(scan.Nodes[0].Bytes == 4124 && scan.ErrorCount == 0, "Paths longer than MAX_PATH scan correctly");
                string link = Path.Combine(root, "cycle");
                if (CreateSymbolicLinkW(link, root, 3))
                {
                    scan = Scanner.Scan(root, CancellationToken.None, null);
                    Check(scan.Links == 1 && scan.Nodes[0].Bytes == 4124 && scan.Nodes[0].Incomplete, "Reparse cycle is excluded and coverage marked partial");
                    Directory.Delete(link);
                }
                else
                {
                    var info = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var process = Process.Start(info))
                    {
                        process.WaitForExit();
                        Check(process.ExitCode == 0, "Junction fixture created without symlink privilege");
                    }
                    scan = Scanner.Scan(root, CancellationToken.None, null);
                    Check(scan.Links == 1 && scan.Nodes[0].Bytes == 4124 && scan.Nodes[0].Incomplete, "Junction cycle is excluded and coverage marked partial");
                    Directory.Delete(link);
                }
                var synthetic = new ScanResult { RootPath = @"C:\fixture" };
                synthetic.Nodes.Add(new Node { Name = "root", Parent = -1, Directory = true, Children = new List<int> { 1 } });
                synthetic.Nodes.Add(new Node { Name = "large", Parent = 0, Bytes = 5L * 1024 * 1024 * 1024 });
                Scanner.Aggregate(synthetic);
                Check(synthetic.Nodes[0].Bytes == 5368709120L, "Aggregation preserves sizes above 4 GiB");
                Check(Scanner.LinkReason(0xA0000003, true).Contains("junction") && Scanner.LinkReason(0x9000301A, false).Contains("Cloud"), "Skipped entries explain junction and cloud-placeholder types");
                TestFeatures(root);
            }
            catch (Exception ex) { report.Add("FAIL: " + ex); code = 1; }
            finally
            {
                try
                {
                    string expectedPrefix = Path.Combine(Path.GetTempPath(), "DiskVisualizer-test-");
                    if (Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(Scanner.Extended(root), true);
                }
                catch (Exception ex) { report.Add("Fixture cleanup failed: " + ex.Message); code = 1; }
                report.Add(passed + " checks passed.");
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results.txt"), report);
            }
            return code;
        }
        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            passed++; report.Add("PASS: " + description);
        }
        private static void TestFeatures(string root)
        {
            string folder = Path.Combine(root, "feature-tests"); Directory.CreateDirectory(folder);
            string a = Path.Combine(folder, "1.bin"), b = Path.Combine(folder, "different-name.bin");
            byte[] content = new byte[32768]; new Random(42).NextBytes(content);
            File.WriteAllBytes(a, content); File.WriteAllBytes(b, content);
            Directory.CreateDirectory(Path.Combine(folder, "subfolder"));
            byte[] different = (byte[])content.Clone(); different[7000] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(folder, "subfolder", "1.bin"), different);
            File.WriteAllBytes(Path.Combine(folder, "empty.bin"), new byte[0]);
            Check(CreateHardLinkW(Path.Combine(folder, "shared-name.bin"), a, IntPtr.Zero), "Hard-link fixture created");
            ScanResult scan = Scanner.Scan(folder, CancellationToken.None, null);
            AllocationResult allocation = AllocationScanner.Measure(scan, CancellationToken.None, null);
            Check(allocation.Known[0] && allocation.UnknownFiles == 0, "Size-on-disk metadata available for ordinary files");
            long expected = 0;
            for (int i = 1; i < scan.Nodes.Count; i++) if (!scan.Nodes[i].Directory) expected += allocation.Sizes[i];
            Check(allocation.Sizes[0] == expected && expected > 0, "Directory allocation equals measured child entries");
            DuplicateResult duplicates = DuplicateScanner.Find(scan, CancellationToken.None, null);
            Check(duplicates.Groups.Count == 1 && duplicates.Groups[0].Files.Count == 2, "Identical content with different names forms exactly one verified pair");
            Check(duplicates.HardLinks == 1 && duplicates.EmptyFiles == 1, "Shared hard-link identity and empty files excluded from duplicates");
            Check(!duplicates.Groups[0].Files.Any(f => f.Path.Contains("subfolder")), "Same name and size with matching samples but different middle bytes is not a duplicate");
            var pair = duplicates.Groups[0].Files;
            Check(DuplicateScanner.Equal(pair[0], pair[1], CancellationToken.None, new DuplicateResult()), "Final byte comparison verifies matching files");
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                Check(DuplicateScanner.Find(scan, source.Token, null).Canceled, "Duplicate check cancels before file reads");
                AllocationResult canceled = AllocationScanner.Measure(scan, source.Token, null);
                Check(canceled.Canceled && !canceled.Known[0], "Canceled disk measurement never substitutes file size or reports complete");
            }
            using (var source = new CancellationTokenSource())
            {
                DuplicateResult canceled = DuplicateScanner.Find(scan, source.Token, text => source.Cancel());
                Check(canceled.Canceled && canceled.Groups.Count == 0, "Duplicate cancellation during sample stage publishes no unverified pair");
            }
            using (var locked = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                duplicates = DuplicateScanner.Find(scan, CancellationToken.None, null);
                Check(duplicates.Groups.Count == 0 && duplicates.Skipped > 0, "Locked candidate is reported, never labeled verified");
            }
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-1));
            duplicates = DuplicateScanner.Find(scan, CancellationToken.None, null);
            Check(duplicates.Groups.Count == 0 && duplicates.Skipped > 0, "Changed file since scan is not verified as duplicate");
            allocation = AllocationScanner.Measure(scan, CancellationToken.None, null);
            Check(!allocation.Known[0] && allocation.UnknownFiles > 0, "Changed file makes allocation total explicitly partial");
            string sparse = Path.Combine(folder, "sparse.bin");
            using (var stream = new FileStream(sparse, FileMode.Create, FileAccess.ReadWrite))
            {
                int returned;
                Check(DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Sparse file fixture created");
                stream.SetLength(8 * 1024 * 1024);
            }
            scan = Scanner.Scan(folder, CancellationToken.None, null); allocation = AllocationScanner.Measure(scan, CancellationToken.None, null);
            int sparseId = scan.Nodes.FindIndex(n => n.Name == "sparse.bin");
            Check(scan.Nodes[sparseId].Bytes == 8 * 1024 * 1024 && allocation.Known[sparseId] && allocation.Sizes[sparseId] < scan.Nodes[sparseId].Bytes, "Sparse allocation remains distinct from apparent file size");
            string compressed = Path.Combine(folder, "compressed.bin");
            using (var stream = new FileStream(compressed, FileMode.Create, FileAccess.ReadWrite))
            {
                IntPtr format = Marshal.AllocHGlobal(2);
                try { Marshal.WriteInt16(format, 1); int returned; Check(DeviceIoControl(stream.SafeFileHandle, 0x9C040, format, 2, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Compressed file fixture created"); }
                finally { Marshal.FreeHGlobal(format); }
                stream.Write(new byte[1048576], 0, 1048576);
                stream.Flush(true);
            }
            scan = Scanner.Scan(folder, CancellationToken.None, null); allocation = AllocationScanner.Measure(scan, CancellationToken.None, null);
            int compressedId = scan.Nodes.FindIndex(n => n.Name == "compressed.bin");
            Check(allocation.Known[compressedId] && allocation.Sizes[compressedId] < scan.Nodes[compressedId].Bytes, "Compressed allocation smaller than file size (known=" + allocation.Known[compressedId] + ", allocated=" + allocation.Sizes[compressedId] + ", file=" + scan.Nodes[compressedId].Bytes + ", errors=" + string.Join(";", allocation.Errors) + ")");
        }
    }
}
