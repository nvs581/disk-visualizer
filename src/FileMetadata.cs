using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskVisualizer
{
    internal struct FileSnapshot
    {
        public long Length, WriteTime;
        public string Identity;
        public uint Attributes;
        public bool Same(FileSnapshot other) { return Identity == other.Identity && Length == other.Length && WriteTime == other.WriteTime; }
    }
    internal static class FileMetadata
    {
        [StructLayout(LayoutKind.Sequential)] private struct Info
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Standard
        {
            public long Allocation, Length;
            public uint Links;
            public byte DeletePending, Directory;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Compression { public long Bytes; public ushort Format; public byte Unit, Chunk, Cluster, R1, R2, R3; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] private static extern bool GetStandard(SafeFileHandle handle, int kind, out Standard info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] private static extern bool GetCompression(SafeFileHandle handle, int kind, out Compression info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributesW(string path);

        public static SafeFileHandle Open(string path, bool content)
        {
            // Reject traversed links before accessing content; inspect the opened leaf again below.
            for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                uint attrs = GetFileAttributesW(Scanner.Extended(parent));
                if (attrs == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((attrs & 1024) != 0) throw new IOException("A parent folder is now a link or cloud placeholder.");
            }
            SafeFileHandle handle = CreateFileW(Scanner.Extended(path), content ? 0x80000000u : 0x80u, content ? 1u : 7u, IntPtr.Zero, 3, 0x00200000u | (content ? 0x08000000u : 0u), IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try
            {
                FileSnapshot snapshot = Snapshot(handle);
                if ((snapshot.Attributes & (1024 | 4096 | 16)) != 0) throw new IOException("Link, offline file, or folder — not read.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        public static FileSnapshot Snapshot(SafeFileHandle handle)
        {
            Info info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new FileSnapshot { Length = checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)), WriteTime = ((long)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime, Identity = info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8"), Attributes = info.Attributes };
        }
        public static long Allocated(SafeFileHandle handle, uint attributes)
        {
            if ((attributes & (0x800 | 0x200)) != 0)
            {
                Compression compressed;
                if (!GetCompression(handle, 8, out compressed, (uint)Marshal.SizeOf(typeof(Compression)))) throw new Win32Exception(Marshal.GetLastWin32Error());
                return compressed.Bytes;
            }
            Standard standard;
            if (!GetStandard(handle, 1, out standard, (uint)Marshal.SizeOf(typeof(Standard)))) throw new Win32Exception(Marshal.GetLastWin32Error());
            return standard.Allocation;
        }
    }
    internal sealed class AllocationResult
    {
        public long[] Sizes;
        public bool[] Known;
        public int UnknownFiles;
        public bool Canceled;
        public TimeSpan Elapsed;
        public readonly List<string> Errors = new List<string>();
        public string Display(int id) { return Known[id] ? Format.Size(Sizes[id]) : Sizes[id] > 0 ? Format.Size(Sizes[id]) + " + unknown" : "Not measured"; }
    }
    internal static class AllocationScanner
    {
        public static AllocationResult Measure(ScanResult scan, CancellationToken token, Action<int> progress)
        {
            var watch = Stopwatch.StartNew();
            var result = new AllocationResult { Sizes = new long[scan.Nodes.Count], Known = new bool[scan.Nodes.Count] };
            long last = -200;
            for (int i = 0; i < scan.Nodes.Count; i++)
            {
                Node node = scan.Nodes[i];
                if (node.Directory) { result.Known[i] = !node.Incomplete; continue; }
                if (!node.Link && !token.IsCancellationRequested)
                {
                    try
                    {
                        using (var handle = FileMetadata.Open(scan.PathFor(i), false))
                        {
                            FileSnapshot before = FileMetadata.Snapshot(handle);
                            if (before.Length != node.Bytes || before.WriteTime != node.WriteTime) throw new IOException("File changed since the scan.");
                            long allocated = FileMetadata.Allocated(handle, before.Attributes);
                            if (allocated < 0 || !before.Same(FileMetadata.Snapshot(handle))) throw new IOException("File changed during measurement.");
                            result.Sizes[i] = allocated; result.Known[i] = true;
                        }
                    }
                    catch (Exception ex)
                    { if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)) throw; if (result.Errors.Count < 50) result.Errors.Add(scan.PathFor(i) + ": " + ex.Message); }
                }
                if (!result.Known[i]) result.UnknownFiles++;
                if (progress != null && watch.ElapsedMilliseconds - last >= 180) { last = watch.ElapsedMilliseconds; progress(i); }
            }
            for (int i = scan.Nodes.Count - 1; i > 0; i--)
            {
                int parent = scan.Nodes[i].Parent;
                result.Sizes[parent] = checked(result.Sizes[parent] + result.Sizes[i]);
                result.Known[parent] &= result.Known[i];
            }
            result.Canceled = token.IsCancellationRequested;
            result.Elapsed = watch.Elapsed;
            return result;
        }
    }
}
