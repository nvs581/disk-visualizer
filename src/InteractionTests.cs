using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskVisualizer
{
    internal sealed partial class MainController
    {
        private async Task TestNewInteractions(List<string> checks)
        {
            Navigate(0); await Task.Delay(200); Window.UpdateLayout();
            int folder = result.Nodes[0].Children.First(id => result.Nodes[id].Directory && result.Nodes[id].Children.Count > 0);
            int child = result.Nodes[folder].Children[0];
            int savedCollection = collection.Count;
            Select(folder);
            chart.HasVisibleSector(folder);
            int builds = chart.GeometryBuildCount;
            PreviewRow(folder);
            for (int i = 0; i < 50; i++) { PreviewRow(folder); chart.HasVisibleSector(folder); }
            if (chart.PreviewId != folder || !chart.InBranch(child, folder) || selection != folder || chart.SelectedId != folder || current != 0 || collection.Count != savedCollection || chart.GeometryBuildCount != builds) throw new Exception("Hover changed persistent state or rebuilt geometry.");
            ClearRowPreview();
            if (chart.PreviewId != -1 || chart.SelectedId != folder) throw new Exception("Hover did not restore selection.");
            checks.Add("PASS: Folder preview highlights its branch, preserves selection/cleanup/navigation, and reuses geometry.");
            var files = Find<DataGrid>("Files");
            FileRow row = ((List<FileRow>)files.ItemsSource).First(r => r.Id == folder);
            files.ScrollIntoView(row); Window.UpdateLayout();
            var container = (DataGridRow)files.ItemContainerGenerator.ContainerFromItem(row);
            PreviewContainer(files, container);
            if (chart.PreviewId != folder) throw new Exception("Row container preview failed.");
            files.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            if (chart.PreviewId != -1) throw new Exception("List leave did not clear preview.");
            files.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, files, container) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            if (chart.PreviewId != folder) throw new Exception("Keyboard preview failed.");
            Find<CheckBox>("DiskMetric").IsChecked = true;
            if (chart.PreviewId != -1 || chart.SelectedId != folder) throw new Exception("Metric reset lost selection.");
            PreviewRow(folder);
            if (chart.PreviewId != folder) throw new Exception("Allocation hover failed.");
            Find<CheckBox>("DiskMetric").IsChecked = false;
            PreviewRow(folder); Find<TextBox>("Search").Text = "__no_match__"; await Task.Delay(150);
            if (chart.PreviewId != -1) throw new Exception("Filtering left a stale preview.");
            Find<TextBox>("Search").Text = ""; await Task.Delay(150);
            PreviewRow(folder); Navigate(folder);
            if (chart.PreviewId != -1) throw new Exception("Navigation left a stale preview.");
            Navigate(0); await Task.Delay(150);
            checks.Add("PASS: Row events, keyboard focus, both metrics, filtering, and navigation manage transient previews.");

            var savedDrive = scanDrive; string savedMount = scanMount;
            ScanResult savedResult = result; int savedCurrent = current;
            for (int i = 0; refreshing && i < 100; i++) await Task.Delay(50);
            if (refreshing) throw new Exception("Drive refresh did not settle.");
            driveDebounce.Stop(); driveRetries = 0;
            var realList = listDrivePaths; var realProbe = probeDrive;
            try
            {
                scanDrive = null;
                int enumerations = 0;
                var firstList = new TaskCompletionSource<string[]>();
                listDrivePaths = delegate { enumerations++; return enumerations == 1 ? firstList.Task : Task.FromResult(new[] { "Y:\\" }); };
                probeDrive = path => Task.FromResult(new DriveEntry { Path = path, Label = "Simulated drive", Capacity = "Test only", Ready = true, Identity = path });
                RefreshDrives(); RefreshDrives(); RefreshDrives();
                if (enumerations != 1) throw new Exception("Drive enumerations overlap.");
                firstList.SetResult(new[] { "Z:\\" });
                for (int i = 0; i < 80 && (enumerations < 2 || refreshing); i++) await Task.Delay(50);
                if (enumerations != 2 || driveSignature == null || !driveSignature.Contains("Y:\\") || driveSignature.Contains("Z:\\") || result != savedResult || scanning) throw new Exception("Latest drive refresh was lost or stale snapshot published.");
                checks.Add("PASS: Concurrent refresh requests coalesce into one follow-up and publish only the latest drive list without scanning.");
                scanDrive = new DriveEntry { Path = "Z:\\", Identity = "test-volume-a", Ready = true }; scanMount = scanDrive.Path;
                CheckScanDrive(new[] { scanDrive, new DriveEntry { Path = "Y:\\", Identity = "other", Ready = true } });
                if (driveStale || result != savedResult || scanning || current != savedCurrent || collection.Count != savedCollection) throw new Exception("Unrelated arrival changed the scan.");
                CheckScanDrive(new[] { new DriveEntry { Path = scanMount, Pending = true } });
                if (driveStale) throw new Exception("Slow probe incorrectly confirmed removal.");
                CheckScanDrive(new[] { new DriveEntry { Path = scanMount, Identity = "test-volume-b", Ready = true } });
                if (!driveStale || Find<Button>("Duplicates").IsEnabled || Find<Button>("Collect").IsEnabled || Find<Button>("Reveal").IsEnabled) throw new Exception("Replacement drive did not disable file actions.");
                CheckScanDrive(new[] { scanDrive });
                if (!driveStale) throw new Exception("Reconnect automatically restored stale actions.");
                AddToCollection(child);
                if (collection.Count != savedCollection || result != savedResult) throw new Exception("Stale results changed.");
                checks.Add("PASS: Drive identity replacement latches stale state, preserves results, and blocks file actions after reconnect.");
                driveStale = false;
                cancellation = new CancellationTokenSource();
                bool duplicateCanceled = false;
                Action cancelDuplicate = delegate { duplicateCanceled = true; };
                DriveUnavailable += cancelDuplicate;
                IntPtr volume = Marshal.AllocHGlobal(20);
                try
                {
                    for (int offset = 0; offset < 20; offset += 4) Marshal.WriteInt32(volume, offset, 0);
                    Marshal.WriteInt32(volume, 0, 20); Marshal.WriteInt32(volume, 4, 2); Marshal.WriteInt32(volume, 12, 1 << 25);
                    bool handled = false;
                    DriveMessage(IntPtr.Zero, 0x219, new IntPtr(0x8004), volume, ref handled);
                    if (!driveStale || !cancellation.IsCancellationRequested || !duplicateCanceled) throw new Exception("Volume removal did not cancel work.");
                    for (int i = 0; i < 10; i++) DriveMessage(IntPtr.Zero, 0x219, new IntPtr(7), IntPtr.Zero, ref handled);
                    if (!driveDebounce.IsEnabled || scanning) throw new Exception("Notification debounce started a scan.");
                }
                finally { Marshal.FreeHGlobal(volume); DriveUnavailable -= cancelDuplicate; cancellation.Dispose(); cancellation = null; }
                checks.Add("PASS: Simulated Windows removal cancels active work; notification bursts queue refresh without scanning.");
            }
            finally { driveDebounce.Stop(); driveRetries = 0; listDrivePaths = realList; probeDrive = realProbe; driveProbes.Clear(); scanDrive = savedDrive; scanMount = savedMount; driveStale = false; UpdateActions(); Text("Coverage", "Complete"); Text("Status", "Interaction checks completed."); RefreshDrives(); }
        }
    }
}
