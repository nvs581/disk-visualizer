using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace DiskVisualizer
{
    internal static class Benchmarks
    {
        public static int Run(string[] args)
        {
            string mode = args[1], path = args[2], output = args[3];
            try
            {
                var process = Process.GetCurrentProcess();
                double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                MethodInfo baselineMethod = null;
                if (mode == "baseline")
                {
                    Assembly assembly = Assembly.LoadFile(Path.GetFullPath(args[4]));
                    if (assembly.Location == Assembly.GetExecutingAssembly().Location) throw new Exception("Baseline unexpectedly resolved to the current assembly.");
                    baselineMethod = assembly.GetType("DiskVisualizer.Scanner").GetMethod("Scan", BindingFlags.Public | BindingFlags.Static);
                }
                cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                var watch = Stopwatch.StartNew();
                double first = -1, allocationMs = 0, duplicateMs = 0;
                long bytes; int count, errors, unknown = 0, groups = 0;
                if (mode == "baseline")
                {
                    object result = baselineMethod.Invoke(null, new object[] { path, CancellationToken.None, null });
                    watch.Stop();
                    Type type = result.GetType();
                    var nodes = (IList)type.GetField("Nodes").GetValue(result);
                    count = nodes.Count - 1; bytes = (long)nodes[0].GetType().GetField("Bytes").GetValue(nodes[0]); errors = (int)type.GetField("ErrorCount").GetValue(result);
                }
                else
                {
                    ScanResult scan = Scanner.Scan(path, CancellationToken.None, p => { if (first < 0) first = watch.Elapsed.TotalMilliseconds; });
                    count = scan.Nodes.Count - 1; bytes = scan.Nodes[0].Bytes; errors = scan.ErrorCount;
                    if (mode == "allocation" || mode == "duplicates")
                    {
                        AllocationResult allocation = AllocationScanner.Measure(scan, CancellationToken.None, null);
                        allocationMs = allocation.Elapsed.TotalMilliseconds; unknown = allocation.UnknownFiles;
                    }
                    if (mode == "duplicates")
                    {
                        DuplicateResult duplicates = DuplicateScanner.Find(scan, CancellationToken.None, null);
                        duplicateMs = duplicates.Elapsed.TotalMilliseconds; groups = duplicates.Groups.Count;
                        errors += duplicates.Skipped;
                    }
                }
                watch.Stop(); process.Refresh();
                string[] fields = { mode, count.ToString(), bytes.ToString(), errors.ToString(), watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), (process.TotalProcessorTime.TotalMilliseconds - cpuBefore).ToString("F3", CultureInfo.InvariantCulture), process.PrivateMemorySize64.ToString(), process.PeakWorkingSet64.ToString(), first.ToString("F3", CultureInfo.InvariantCulture), allocationMs.ToString("F3", CultureInfo.InvariantCulture), duplicateMs.ToString("F3", CultureInfo.InvariantCulture), unknown.ToString(), groups.ToString() };
                File.WriteAllText(output, string.Join(",", fields));
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(output, "ERROR: " + ex); return 1; }
        }
        public static int MakeFixture(string[] args)
        {
            string root = Path.GetFullPath(args[1]);
            if (Directory.Exists(root)) return 1;
            Directory.CreateDirectory(root);
            int count = int.Parse(args[2]);
            for (int i = 0; i < count; i++)
            {
                string folder = Path.Combine(root, "folder-" + (i / 1000).ToString("D3")); Directory.CreateDirectory(folder);
                byte[] bytes = new byte[1024]; new Random(i / 2 + 23).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(folder, "file-" + i.ToString("D6") + ".bin"), bytes);
            }
            return 0;
        }
    }
}
