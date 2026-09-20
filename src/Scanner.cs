using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskVisualizer
{
    internal sealed class Node
    {
        public string Name;
        public int Parent;
        public List<int> Children;
        public long Bytes;
        public bool Directory;
        public bool Link;
        public bool Incomplete;
        public bool Enumerated;
        public int Descendants;
        public long WriteTime;
        public string SkipReason;
    }

    internal sealed class ScanResult
    {
        public string RootPath;
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<string> Errors = new List<string>();
        public int ErrorCount;
        public int Links;
        public bool Canceled;
        public bool Limited;
        public TimeSpan Elapsed;
        public AllocationResult Allocation;
        public long SizeFor(int id, bool onDisk) { return onDisk && Allocation != null ? Allocation.Sizes[id] : Nodes[id].Bytes; }
        public string PathFor(int id)
        {
            var names = new Stack<string>();
            while (id > 0) { names.Push(Nodes[id].Name); id = Nodes[id].Parent; }
            string path = RootPath;
            while (names.Count > 0) path = Path.Combine(path, names.Pop());
            return path;
        }
        public bool IsAncestor(int ancestor, int child)
        {
            for (int i = child; i >= 0; i = Nodes[i].Parent) if (i == ancestor) return true;
            return false;
        }
    }

    internal sealed class ScanProgress
    {
        public int Count;
        public long Bytes;
        public int Errors;
        public string Path;
    }

    internal static class Scanner
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string name, int level, out FindData data, int search, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextFileW(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributesW(string path);

        internal static string Extended(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        public static ScanResult Scan(string path, CancellationToken token, Action<ScanProgress> progress)
        {
            path = Path.GetFullPath(path);
            uint rootAttributes = GetFileAttributesW(Extended(path));
            if (rootAttributes == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((rootAttributes & 16) == 0) throw new IOException("Choose a folder or drive to scan.");
            if ((rootAttributes & 1024) != 0) throw new IOException("This folder is a reparse point. Choose its real location instead.");
            var result = new ScanResult { RootPath = path };
            string rootName = Path.GetFileName(path.TrimEnd('\\'));
            result.Nodes.Add(new Node { Name = rootName.Length == 0 ? path : rootName, Parent = -1, Directory = true, Children = new List<int>() });
            var pending = new Stack<int>();
            pending.Push(0);
            var watch = Stopwatch.StartNew();
            long lastReport = -250;
            long bytes = 0;
            while (pending.Count > 0 && !token.IsCancellationRequested && !result.Limited)
            {
                int parent = pending.Pop();
                string directory = result.PathFor(parent);
                FindData data;
                IntPtr handle = FindFirstFileExW(Extended(directory).TrimEnd('\\') + @"\*", 1, out data, 0, IntPtr.Zero, 2);
                if (handle == new IntPtr(-1))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 && GetFileAttributesW(Extended(directory)) != uint.MaxValue) result.Nodes[parent].Enumerated = true;
                    else AddError(result, parent, directory, error);
                    continue;
                }
                try
                {
                    bool more = true;
                    while (more && !token.IsCancellationRequested)
                    {
                        if (data.Name != "." && data.Name != "..")
                        {
                            bool isDirectory = (data.Attributes & 16) != 0;
                            bool link = (data.Attributes & 1024) != 0;
                            long size = isDirectory || link ? 0 : checked((long)(((ulong)data.SizeHigh << 32) | data.SizeLow));
                            var node = new Node { Name = data.Name, Parent = parent, Directory = isDirectory, Link = link, Bytes = size, WriteTime = ((long)data.Write.dwHighDateTime << 32) | (uint)data.Write.dwLowDateTime, SkipReason = link ? LinkReason(data.Reserved0, isDirectory) : null, Children = isDirectory ? new List<int>() : null };
                            int id = result.Nodes.Count;
                            result.Nodes.Add(node);
                            result.Nodes[parent].Children.Add(id);
                            bytes = checked(bytes + size);
                            if (link) { result.Links++; node.Incomplete = true; }
                            else if (isDirectory) pending.Push(id);
                            if (result.Nodes.Count >= 2000000) { result.Limited = true; break; }
                        }
                        if (watch.ElapsedMilliseconds - lastReport >= 150)
                        {
                            lastReport = watch.ElapsedMilliseconds;
                            if (progress != null) progress(new ScanProgress { Count = result.Nodes.Count - 1, Bytes = bytes, Errors = result.ErrorCount, Path = directory });
                        }
                        more = FindNextFileW(handle, out data);
                        if (!more)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 18) AddError(result, parent, directory, error);
                            else result.Nodes[parent].Enumerated = true;
                        }
                    }
                }
                finally { FindClose(handle); }
            }
            result.Canceled = token.IsCancellationRequested;
            foreach (Node node in result.Nodes) if (node.Directory && !node.Enumerated) node.Incomplete = true;
            Aggregate(result);
            result.Nodes[0].Incomplete |= result.Canceled || result.Limited;
            result.Elapsed = watch.Elapsed;
            return result;
        }

        private static void AddError(ScanResult result, int id, string path, int code)
        {
            result.Nodes[id].Incomplete = true;
            result.ErrorCount++;
            if (result.Errors.Count < 100) result.Errors.Add(path + " — " + new Win32Exception(code).Message);
        }
        internal static string LinkReason(uint tag, bool directory)
        {
            if ((tag & 0xFFFF0FFF) == 0x9000001A) return "Cloud placeholder — not measured";
            if (tag == 0xA0000003) return "Folder junction — skipped";
            if (tag == 0xA000000C) return (directory ? "Folder" : "File") + " link — skipped";
            return "Special filesystem entry — skipped";
        }

        internal static void Aggregate(ScanResult result)
        {
            for (int i = result.Nodes.Count - 1; i > 0; i--)
            {
                Node n = result.Nodes[i], parent = result.Nodes[n.Parent];
                parent.Bytes = checked(parent.Bytes + n.Bytes);
                parent.Descendants += n.Descendants + 1;
                parent.Incomplete |= n.Incomplete;
            }
        }
    }

    internal static class Format
    {
        public static string Size(long bytes)
        {
            double value = bytes;
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "N0" : "0.0") + " " + units[unit];
        }
    }
}
