using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DiskVisualizer
{
    internal sealed class DuplicateFile
    {
        public int Id;
        public string Path;
        public FileSnapshot Snapshot;
    }
    internal sealed class DuplicateGroup
    {
        public readonly List<DuplicateFile> Files = new List<DuplicateFile>();
        public DateTime VerifiedUtc;
    }
    internal sealed class DuplicateResult
    {
        public readonly List<DuplicateGroup> Groups = new List<DuplicateGroup>();
        public readonly List<string> Errors = new List<string>();
        public int Skipped, HardLinks, EmptyFiles, Checked;
        public long BytesRead;
        public bool Canceled;
        public TimeSpan Elapsed;
    }
    internal static class DuplicateScanner
    {
        public static DuplicateResult Find(ScanResult scan, CancellationToken token, Action<string> progress)
        {
            var result = new DuplicateResult();
            var watch = Stopwatch.StartNew();
            var identities = new HashSet<string>();
            long last = -200;
            Action<string> report = text => { if (progress != null && watch.ElapsedMilliseconds - last >= 180) { last = watch.ElapsedMilliseconds; progress(text); } };
            try
            {
                var sizes = new Dictionary<long, List<int>>();
                for (int id = 0; id < scan.Nodes.Count; id++)
                {
                    token.ThrowIfCancellationRequested();
                    Node node = scan.Nodes[id];
                    if (node.Directory || node.Link) continue;
                    if (node.Bytes == 0) { result.EmptyFiles++; continue; }
                    List<int> group;
                    if (!sizes.TryGetValue(node.Bytes, out group)) sizes.Add(node.Bytes, group = new List<int>());
                    group.Add(id);
                }
                foreach (List<int> sameSize in sizes.Values.Where(g => g.Count > 1))
                {
                    var samples = new Dictionary<string, List<DuplicateFile>>();
                    foreach (int id in sameSize)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = scan.PathFor(id);
                        report("Comparing samples · " + Path.GetFileName(path));
                        try
                        {
                            using (var handle = FileMetadata.Open(path, true))
                            using (var stream = new FileStream(handle, FileAccess.Read, 65536, false))
                            {
                                FileSnapshot snapshot = FileMetadata.Snapshot(handle);
                                if (snapshot.Length != scan.Nodes[id].Bytes || snapshot.WriteTime != scan.Nodes[id].WriteTime) throw new IOException("Changed since storage scan; rescan first.");
                                if (snapshot.Identity.EndsWith(":0000000000000000")) throw new IOException("Stable file identity unavailable.");
                                if (!identities.Add(snapshot.Identity)) { result.HardLinks++; continue; }
                                var file = new DuplicateFile { Id = id, Path = path, Snapshot = snapshot };
                                string hash = Sample(stream, token, result);
                                EnsureSame(file, FileMetadata.Snapshot(handle));
                                Add(samples, hash, file); result.Checked++;
                            }
                        }
                        catch (Exception ex) { if (!Expected(ex)) throw; Error(result, path, ex); }
                    }
                    foreach (List<DuplicateFile> matchingSample in samples.Values.Where(g => g.Count > 1))
                    {
                        var hashes = new Dictionary<string, List<DuplicateFile>>();
                        foreach (DuplicateFile file in matchingSample)
                        {
                            token.ThrowIfCancellationRequested(); report("Reading full contents · " + Path.GetFileName(file.Path));
                            try
                            {
                                using (var handle = FileMetadata.Open(file.Path, true))
                                using (var stream = new FileStream(handle, FileAccess.Read, 65536, false))
                                {
                                    EnsureSame(file, FileMetadata.Snapshot(handle));
                                    string hash = Hash(stream, token, result);
                                    EnsureSame(file, FileMetadata.Snapshot(handle));
                                    Add(hashes, hash, file);
                                }
                            }
                            catch (Exception ex) { if (!Expected(ex)) throw; Error(result, file.Path, ex); }
                        }
                        foreach (List<DuplicateFile> matchingHash in hashes.Values.Where(g => g.Count > 1))
                        {
                            var partitions = new List<DuplicateGroup>();
                            foreach (DuplicateFile file in matchingHash)
                            {
                                token.ThrowIfCancellationRequested(); report("Verifying every byte · " + Path.GetFileName(file.Path));
                                bool matched = false, failed = false;
                                foreach (DuplicateGroup group in partitions)
                                {
                                    try
                                    {
                                        if (Equal(group.Files[0], file, token, result))
                                        {
                                            group.Files.Add(file); group.VerifiedUtc = DateTime.UtcNow; matched = true;
                                            if (group.Files.Count == 2) result.Groups.Add(group);
                                            break;
                                        }
                                    }
                                    catch (Exception ex) { if (!Expected(ex)) throw; Error(result, file.Path, ex); failed = true; break; }
                                }
                                if (!matched && !failed) { var group = new DuplicateGroup(); group.Files.Add(file); partitions.Add(group); }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { result.Canceled = true; }
            result.Elapsed = watch.Elapsed;
            return result;
        }
        private static bool Expected(Exception ex) { return ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception; }
        private static void Error(DuplicateResult result, string path, Exception ex) { result.Skipped++; if (result.Errors.Count < 50) result.Errors.Add(path + ": " + ex.Message); }
        private static void EnsureSame(DuplicateFile file, FileSnapshot actual) { if (!file.Snapshot.Same(actual)) throw new IOException("File changed during duplicate detection; not verified."); }
        private static void Add(Dictionary<string, List<DuplicateFile>> groups, string hash, DuplicateFile file)
        { List<DuplicateFile> list; if (!groups.TryGetValue(hash, out list)) groups.Add(hash, list = new List<DuplicateFile>()); list.Add(file); }
        private static string Sample(FileStream stream, CancellationToken token, DuplicateResult result)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[4096];
                foreach (long offset in new[] { 0L, Math.Max(0, stream.Length / 2 - 2048), Math.Max(0, stream.Length - 4096) })
                {
                    token.ThrowIfCancellationRequested(); stream.Position = offset;
                    int read = ReadBlock(stream, buffer, token); result.BytesRead += read;
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0); return Convert.ToBase64String(sha.Hash);
            }
        }
        private static string Hash(FileStream stream, CancellationToken token, DuplicateResult result)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = ReadBlock(stream, buffer, token)) > 0) { result.BytesRead += read; sha.TransformBlock(buffer, 0, read, buffer, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0); return Convert.ToBase64String(sha.Hash);
            }
        }
        internal static bool Equal(DuplicateFile first, DuplicateFile second, CancellationToken token, DuplicateResult result)
        {
            using (var leftHandle = FileMetadata.Open(first.Path, true))
            using (var rightHandle = FileMetadata.Open(second.Path, true))
            using (var left = new FileStream(leftHandle, FileAccess.Read, 65536, false))
            using (var right = new FileStream(rightHandle, FileAccess.Read, 65536, false))
            {
                EnsureSame(first, FileMetadata.Snapshot(leftHandle)); EnsureSame(second, FileMetadata.Snapshot(rightHandle));
                byte[] a = new byte[65536], b = new byte[65536];
                while (true)
                {
                    int ac = ReadBlock(left, a, token), bc = ReadBlock(right, b, token); result.BytesRead += ac + bc;
                    if (ac != bc) return false;
                    for (int i = 0; i < ac; i++) if (a[i] != b[i]) return false;
                    if (ac == 0) break;
                }
                EnsureSame(first, FileMetadata.Snapshot(leftHandle)); EnsureSame(second, FileMetadata.Snapshot(rightHandle));
                return true;
            }
        }
        private static int ReadBlock(Stream stream, byte[] buffer, CancellationToken token)
        {
            int total = 0;
            while (total < buffer.Length) { token.ThrowIfCancellationRequested(); int read = stream.Read(buffer, total, buffer.Length - total); if (read == 0) break; total += read; }
            return total;
        }
    }
}
