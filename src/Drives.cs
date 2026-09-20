using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DiskVisualizer
{
    internal sealed class DriveEntry
    {
        public string Path, Identity, Label, Capacity;
        public bool Ready, Pending;
    }
    internal static class DriveCatalog
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder name, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumePathName(string path, StringBuilder volume, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetVolumeInformation(string root, StringBuilder label, int labelSize, out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystem, int fileSystemSize);
        internal static DriveEntry Probe(string path)
        {
            var entry = new DriveEntry { Path = path, Label = path, Capacity = "Not ready" };
            try
            {
                var mount = new StringBuilder(1024);
                if (GetVolumePathName(path, mount, mount.Capacity)) entry.Path = mount.ToString();
                var name = new StringBuilder(1024);
                if (GetVolumeNameForVolumeMountPoint(entry.Path, name, name.Capacity)) entry.Identity = name.ToString();
                uint serial, componentLength, flags;
                if (entry.Identity != null && GetVolumeInformation(entry.Path, null, 0, out serial, out componentLength, out flags, null, 0)) entry.Identity += serial.ToString("X8");
                var drive = new DriveInfo(entry.Path);
                if (drive.DriveType == DriveType.Network || drive.DriveType == DriveType.CDRom) return null;
                if (!drive.IsReady) return entry;
                entry.Capacity = Format.Size(drive.AvailableFreeSpace) + " free of " + Format.Size(drive.TotalSize);
                entry.Label = (drive.VolumeLabel.Length == 0 ? "Local disk" : drive.VolumeLabel) + " (" + path.TrimEnd('\\') + ")";
                entry.Ready = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return entry;
        }
    }
    internal sealed partial class MainController
    {
        private HwndSource driveSource;
        private readonly DispatcherTimer driveDebounce = new DispatcherTimer();
        private readonly Dictionary<string, Task<DriveEntry>> driveProbes = new Dictionary<string, Task<DriveEntry>>(StringComparer.OrdinalIgnoreCase);
        private Func<Task<string[]>> listDrivePaths = () => Task.Run(() => DriveInfo.GetDrives().Select(d => d.Name).ToArray());
        private Func<string, Task<DriveEntry>> probeDrive = path => Task.Run(() => DriveCatalog.Probe(path));
        private DriveEntry scanDrive;
        private string scanMount, driveSignature;
        private bool driveStale, refreshing;
        private int driveVersion, driveRetries;
        private event Action DriveUnavailable;
        private void SetupDriveMonitor()
        {
            driveDebounce.Interval = TimeSpan.FromMilliseconds(350);
            driveDebounce.Tick += delegate { driveDebounce.Stop(); RefreshDrives(); };
            Window.SourceInitialized += delegate
            {
                driveSource = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                if (driveSource != null) driveSource.AddHook(DriveMessage);
            };
            Window.Closed += delegate
            {
                driveDebounce.Stop();
                if (driveSource != null) driveSource.RemoveHook(DriveMessage);
            };
        }
        private IntPtr DriveMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x219) return IntPtr.Zero;
            int kind = wParam.ToInt32();
            if (kind != 7 && kind != 0x8000 && kind != 0x8003 && kind != 0x8004 && kind != 0x18) return IntPtr.Zero;
            if ((kind == 0x8003 || kind == 0x8004) && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam) >= 20 && Marshal.ReadInt32(lParam, 4) == 2 && !string.IsNullOrEmpty(scanMount))
            {
                int letter = char.ToUpperInvariant(scanMount[0]) - 'A';
                if (letter >= 0 && letter < 26 && (Marshal.ReadInt32(lParam, 12) & (1 << letter)) != 0) MarkDriveUnavailable();
            }
            driveVersion++; driveRetries = 3;
            driveDebounce.Stop(); driveDebounce.Interval = TimeSpan.FromMilliseconds(350); driveDebounce.Start();
            return IntPtr.Zero;
        }
        private void MarkDriveUnavailable()
        {
            if (driveStale || scanMount == null) return;
            driveStale = true;
            if (cancellation != null) cancellation.Cancel();
            if (DriveUnavailable != null) DriveUnavailable();
            UpdateActions();
        }
        private void ShowDriveState()
        {
            if (!driveStale) return;
            Text("Coverage", "Stale / disconnected");
            Text("Status", "Drive unavailable or replaced. Results may be partial. Reconnect and scan again to use file actions.");
        }
        private void CheckScanDrive(IEnumerable<DriveEntry> entries)
        {
            if (scanDrive == null || driveStale) return;
            DriveEntry match = entries.FirstOrDefault(d => string.Equals(d.Path, scanDrive.Path, StringComparison.OrdinalIgnoreCase));
            if (match != null && match.Pending) return;
            if (match == null || !match.Ready || !string.Equals(match.Identity, scanDrive.Identity, StringComparison.OrdinalIgnoreCase)) MarkDriveUnavailable();
        }
        private async void RefreshDrives()
        {
            driveVersion++;
            if (refreshing || closed) return;
            refreshing = true;
            int version = driveVersion;
            try
            {
                string[] paths = await listDrivePaths();
                if (closed) return;
                var visiblePaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                if (scanDrive != null && !paths.Contains(scanDrive.Path, StringComparer.OrdinalIgnoreCase)) paths = paths.Concat(new[] { scanDrive.Path }).ToArray();
                foreach (string path in paths)
                {
                    Task<DriveEntry> pending;
                    if (!driveProbes.TryGetValue(path, out pending) || pending.IsCompleted) driveProbes[path] = probeDrive(path);
                }
                await Task.WhenAny(Task.WhenAll(paths.Select(p => driveProbes[p])), Task.Delay(1200));
                if (closed || version != driveVersion) return;
                var entries = new List<DriveEntry>();
                foreach (string path in paths)
                {
                    Task<DriveEntry> probe = driveProbes[path];
                    if (!probe.IsCompleted) { entries.Add(new DriveEntry { Path = path, Label = path, Capacity = "Waiting for device", Pending = true }); continue; }
                    driveProbes.Remove(path);
                    if (probe.Status == TaskStatus.RanToCompletion && probe.Result != null) entries.Add(probe.Result);
                }
                // A timeout is not proof of removal. Explicit removal messages still cancel immediately.
                CheckScanDrive(entries);
                entries = entries.Where(d => visiblePaths.Contains(d.Path)).ToList();
                string signature = string.Join("|", entries.Select(d => d.Path + d.Identity + d.Ready + d.Label + d.Capacity));
                if (signature == driveSignature) return;
                driveSignature = signature;
                var panel = Find<StackPanel>("DriveList"); panel.Children.Clear();
                foreach (DriveEntry drive in entries)
                {
                    var content = new StackPanel();
                    content.Children.Add(new TextBlock { Text = drive.Label, FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                    content.Children.Add(new TextBlock { Text = drive.Capacity, FontSize = 10, Foreground = Sunburst.ColorBrush("#8C9BB1"), Margin = new Thickness(0, 7, 0, 0) });
                    var button = new Button { Content = content, IsEnabled = drive.Ready, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 12, 10, 12), ToolTip = "Scan " + drive.Path };
                    string path = drive.Path; button.Click += delegate { StartScan(path); }; panel.Children.Add(button);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                refreshing = false;
                if (!closed && (version != driveVersion || driveRetries > 0))
                {
                    if (driveRetries > 0) driveRetries--;
                    driveDebounce.Stop(); driveDebounce.Interval = TimeSpan.FromSeconds(1); driveDebounce.Start();
                }
            }
        }
    }
}
