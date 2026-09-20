using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskVisualizer
{
    internal sealed class FileRow
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public long Bytes { get; set; }
        public string SizeText { get; set; }
        public string Percent { get; set; }
        public long DiskBytes { get; set; }
        public string DiskText { get; set; }
        public string Details { get; set; }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Length >= 4 && args[0] == "--benchmark") return Benchmarks.Run(args);
            if (args.Length == 3 && args[0] == "--make-fixture") return Benchmarks.MakeFixture(args);
            try
            {
                var application = new Application();
                application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "Disk Visualizer", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                var controller = new MainController(args);
                application.Run(controller.Window);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"), ex.ToString());
                return 1;
            }
        }
    }

    internal sealed partial class MainController
    {
        public Window Window;
        private readonly Sunburst chart = new Sunburst();
        private ScanResult result;
        private int current, selection = -1;
        private readonly List<int> collection = new List<int>();
        private CancellationTokenSource cancellation;
        private volatile ScanProgress progress;
        private readonly DispatcherTimer timer;
        private readonly Stopwatch elapsed = new Stopwatch();
        private bool scanning, closed;
        private Point dragStart;
        private int queryVersion;
        private readonly string[] arguments;
        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        private void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        private void Button(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }

        public MainController(string[] args)
        {
            arguments = args;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) Window = (Window)XamlReader.Load(stream);
            SetupFeatures(); SetupDriveMonitor();
            Find<Grid>("ChartHost").Children.Add(chart);
            Find<TextBox>("PathInput").Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Find<TextBox>("Search").TextChanged += delegate { RefreshRows(); };
            Button("Choose", ChooseFolder); Button("SidebarChoose", ChooseFolder);
            Button("Scan", delegate { StartScan(Find<TextBox>("PathInput").Text); });
            Button("Cancel", delegate { if (cancellation != null) { cancellation.Cancel(); Text("Status", "Canceling… keeping measured results."); } });
            Button("RefreshDrives", RefreshDrives);
            Button("Up", GoUp);
            Button("Collect", CollectSelected);
            Button("Reveal", RevealSelected);
            Button("Review", ReviewCollection);
            Button("Issues", ShowIssues);
            Find<TextBox>("PathInput").KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { StartScan(Find<TextBox>("PathInput").Text); e.Handled = true; } };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.SystemKey == Key.Up || e.Key == Key.Up)) { GoUp(); e.Handled = true; }
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F) { Find<TextBox>("Search").Focus(); e.Handled = true; }
                if (e.Key == Key.Escape && cancellation != null) cancellation.Cancel();
            };
            var files = Find<DataGrid>("Files"); SetupRowPreview(files);
            files.SelectionChanged += delegate
            {
                var row = files.SelectedItem as FileRow;
                if (row != null) Select(row.Id);
            };
            files.MouseDoubleClick += delegate { NavigateSelected(); };
            files.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { NavigateSelected(); e.Handled = true; }
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C) CopySelected();
            };
            files.Sorting += delegate(object sender, DataGridSortingEventArgs e)
            {
                e.Handled = true;
                sortName = e.Column.SortMemberPath == "Name";
                sortDisk = e.Column.SortMemberPath == "DiskBytes";
                descending = e.Column.SortDirection != ListSortDirection.Descending;
                foreach (var column in files.Columns) column.SortDirection = null;
                e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                RefreshRows();
            };
            var menu = new ContextMenu();
            AddMenu(menu, "Open folder", NavigateSelected); AddMenu(menu, "Show in Explorer", RevealSelected);
            AddMenu(menu, "Copy path", CopySelected); AddMenu(menu, "Add to cleanup list", CollectSelected);
            files.ContextMenu = menu;
            files.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                DependencyObject item = e.OriginalSource as DependencyObject;
                while (item != null && !(item is DataGridRow)) item = VisualTreeHelper.GetParent(item);
                if (item is DataGridRow) ((DataGridRow)item).IsSelected = true;
            };
            files.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(files); };
            files.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed && files.SelectedItem is FileRow && (e.GetPosition(files) - dragStart).Length > 8)
                    DragDrop.DoDragDrop(files, new DataObject("DiskVisualizer.Node", ((FileRow)files.SelectedItem).Id), DragDropEffects.Copy);
            };
            Find<Border>("CollectionDrop").Drop += delegate(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent("DiskVisualizer.Node")) AddToCollection((int)e.Data.GetData("DiskVisualizer.Node"));
            };
            chart.Picked += delegate(int id, bool open) { if (open) Navigate(id); else { Select(id); SyncRow(id); } };
            chart.Hovered += delegate(int id) { ShowHover(id >= 0 ? id : selection); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += delegate
            {
                if (driveStale) return;
                if (measuring) { Text("Status", "Measuring size on disk · " + allocationProgress.ToString("N0") + " / " + (result.Nodes.Count - 1).ToString("N0") + " entries"); return; }
                ScanProgress p = progress;
                if (!scanning || p == null) return;
                Text("TotalSize", Format.Size(p.Bytes)); Text("ItemCount", p.Count.ToString("N0"));
                Text("Duration", elapsed.Elapsed.TotalSeconds.ToString("0.0") + " seconds elapsed");
                Text("Status", "Scanning  ·  " + p.Path);
                Find<Button>("Issues").Content = p.Errors.ToString("N0") + " inaccessible locations";
            };
            Window.Closed += delegate { closed = true; timer.Stop(); if (cancellation != null) cancellation.Cancel(); };
            Window.Activated += delegate { RefreshDrives(); };
            Window.Loaded += delegate
            {
                RefreshDrives(); UpdateActions();
                int index = Array.IndexOf(arguments, "--scan");
                if (index >= 0 && index + 1 < arguments.Length) StartScan(arguments[index + 1]);
                if (arguments.Contains("--screenshot") && index < 0) ScheduleScreenshot();
                if (arguments.Contains("--ui-test")) RunUiTests();
            };
        }

        private bool sortName;
        private bool descending = true;
        private static void AddMenu(ContextMenu menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += delegate { action(); }; menu.Items.Add(item); }
        private void ChooseFolder()
        {
            if (scanning || measuring) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder to analyze"; dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) StartScan(dialog.SelectedPath);
            }
        }
        private async void StartScan(string path)
        {
            if (scanning || measuring) return;
            if (collection.Count > 0 && MessageBox.Show(Window, "Starting a new scan clears the cleanup list. Continue?", "New scan", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            path = path.Trim().Trim('"');
            if (path.Length == 0) return;
            try { path = Path.GetFullPath(path); } catch (Exception ex) { MessageBox.Show(Window, ex.Message, "Invalid path"); return; }
            ClearRowPreview(); driveStale = false; scanDrive = null; scanMount = Path.GetPathRoot(path);
            scanning = true; result = null; selection = -1; current = 0;
            duplicates = null; Find<CheckBox>("DiskMetric").IsChecked = false; Text("DiskTotal", "Size on disk: waiting for file sizes");
            queryVersion++; collection.Clear(); UpdateCollection(); chart.SetData(null, 0);
            Find<DataGrid>("Files").ItemsSource = null; Find<TextBox>("PathInput").Text = path;
            Find<TextBox>("Search").Text = ""; Text("SelectedPath", "Scan in progress…");
            Text("HoverName", "Finding what takes up space…"); Text("HoverSize", "The map appears when scanning finishes. You can cancel anytime.");
            Text("Coverage", "Scanning"); Text("TotalSize", "0 B"); Text("ItemCount", "0"); Text("ListCount", "Scanning…");
            cancellation = new CancellationTokenSource(); progress = null; elapsed.Restart(); timer.Start(); UpdateActions();
            try
            {
                var token = cancellation.Token;
                Task<DriveEntry> identify = Task.Run(() => DriveCatalog.Probe(path));
                await Task.WhenAny(identify, Task.Delay(1500, token));
                token.ThrowIfCancellationRequested();
                if (!identify.IsCompleted) throw new IOException("The device is taking too long to respond. Check its connection and try again.");
                scanDrive = await identify;
                if (closed || driveStale) return;
                if (scanDrive != null) scanMount = scanDrive.Path;
                ScanResult scanned = await Task.Run(delegate
                {
                    ScanResult value = Scanner.Scan(path, token, p => progress = p);
                    foreach (Node n in value.Nodes) if (n.Children != null) n.Children.Sort((a, b) => { int c = value.Nodes[b].Bytes.CompareTo(value.Nodes[a].Bytes); return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(value.Nodes[a].Name, value.Nodes[b].Name); });
                    return value;
                });
                if (closed) return;
                result = scanned; scanning = false;
                Text("TotalSize", Format.Size(result.Nodes[0].Bytes)); Text("ItemCount", (result.Nodes.Count - 1).ToString("N0"));
                Text("Duration", "Scanned in " + result.Elapsed.TotalSeconds.ToString("0.00") + " seconds");
                Text("Coverage", result.Canceled ? "Canceled" : result.Limited ? "Item limit" : result.Nodes[0].Incomplete ? "Partial" : "Complete");
                Find<Button>("Issues").Content = result.ErrorCount + " errors · " + result.Links + " links skipped  ↗";
                Text("Status", (result.Canceled ? "Canceled · partial results" : result.Limited ? "Stopped at 2 million items · partial results" : "Scan finished") + "  ·  Logical sizes; reparse points excluded");
                Navigate(0);
                if (driveStale) return;
                measuring = true; UpdateActions();
                Text("DiskTotal", "Size on disk: measuring…");
                AllocationResult allocation = await Task.Run(() => AllocationScanner.Measure(scanned, token, count => allocationProgress = count));
                if (closed) return;
                result.Allocation = allocation; measuring = false;
                Text("DiskTotal", "Size on disk: " + allocation.Display(0));
                Text("Status", (result.Canceled || allocation.Canceled ? "Stopped · partial results" : "Scan finished") + " · " + allocation.UnknownFiles + " files not measured on disk · shared file names counted separately");
                ReloadChart(); RefreshRows(); ShowHover(selection);
                if (arguments.Contains("--screenshot")) ScheduleScreenshot();
            }
            catch (Exception ex)
            {
                if (!closed) { Text("Coverage", "Failed"); Text("Status", ex.Message); Text("HoverName", "We couldn't scan this location"); Text("HoverSize", "Check the path and access permissions, then try again."); }
            }
            finally
            {
                scanning = false; measuring = false; timer.Stop(); elapsed.Stop(); cancellation.Dispose(); cancellation = null;
                if (!closed) UpdateActions();
            }
        }

        private void UpdateActions()
        {
            Find<Button>("Scan").IsEnabled = !scanning && !measuring;
            Find<Button>("Choose").IsEnabled = !scanning && !measuring;
            Find<Button>("SidebarChoose").IsEnabled = !scanning && !measuring;
            Find<StackPanel>("DriveList").IsEnabled = !scanning && !measuring;
            Find<TextBox>("PathInput").IsEnabled = !scanning && !measuring;
            Find<Button>("Cancel").Visibility = scanning || measuring ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("Duplicates").IsEnabled = result != null && !scanning && !measuring && !driveStale;
            Find<CheckBox>("DiskMetric").IsEnabled = result != null && result.Allocation != null;
            Find<Button>("Up").IsEnabled = result != null && current != 0;
            Find<Button>("Collect").IsEnabled = result != null && selection > 0 && !driveStale;
            Find<Button>("Reveal").IsEnabled = result != null && selection >= 0 && !driveStale;
            ShowDriveState();
        }
        private void Navigate(int id)
        {
            if (result == null || !result.Nodes[id].Directory || result.Nodes[id].Link) return;
            current = id; selection = -1;
            Find<TextBox>("PathInput").Text = result.PathFor(id);
            Text("ListTitle", id == 0 ? "Folder contents" : result.Nodes[id].Name);
            Find<TextBox>("Search").Text = "";
            ReloadChart(); RefreshRows(); ShowHover(-1);
            Text("SelectedPath", "Select an item to see its location."); UpdateActions();
        }
        private void GoUp() { if (result != null && current != 0) Navigate(result.Nodes[current].Parent); }
        private void NavigateSelected() { if (selection >= 0) Navigate(selection); }
        private async void RefreshRows()
        {
            if (result == null || scanning) return;
            ClearRowPreview();
            int version = ++queryVersion;
            ScanResult snapshot = result;
            int root = current;
            string query = Find<TextBox>("Search").Text.Trim();
            bool nameSort = sortName, reverse = descending, diskSort = sortDisk;
            AllocationResult allocation = snapshot.Allocation;
            List<FileRow> rows = await Task.Run(delegate
            {
                IEnumerable<int> ids = snapshot.Nodes[root].Children;
                if (query.Length > 0) ids = ids.Where(id => snapshot.Nodes[id].Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                if (nameSort) ids = reverse ? ids.OrderByDescending(id => snapshot.Nodes[id].Name, StringComparer.OrdinalIgnoreCase) : ids.OrderBy(id => snapshot.Nodes[id].Name, StringComparer.OrdinalIgnoreCase);
                else if (diskSort && allocation != null) ids = reverse ? ids.OrderByDescending(id => allocation.Sizes[id]) : ids.OrderBy(id => allocation.Sizes[id]);
                else if (!reverse) ids = ids.Reverse();
                return ids.Select(id =>
                {
                    Node node = snapshot.Nodes[id];
                    return new FileRow { Id = id, Name = node.Name, DisplayName = (node.Link ? "↗  " : node.Directory ? "▸  " : "    ") + node.Name + (node.Incomplete ? "  *" : ""), Bytes = node.Bytes, SizeText = node.Link ? "Skipped*" : Format.Size(node.Bytes), DiskBytes = allocation == null ? -1 : allocation.Sizes[id], DiskText = node.Link ? "Skipped*" : allocation == null ? "Pending…" : allocation.Display(id), Details = node.Link ? node.SkipReason : "File size counts content; size on disk counts allocated storage. Shared file names are counted separately.", Percent = node.Link ? "—" : snapshot.Nodes[root].Bytes == 0 ? "0%" : ((double)node.Bytes / snapshot.Nodes[root].Bytes).ToString("P1") };
                }).ToList();
            });
            if (closed || version != queryVersion) return;
            ClearRowPreview();
            Find<DataGrid>("Files").ItemsSource = rows;
            Text("ListCount", rows.Count.ToString("N0") + " items");
        }
        private void Select(int id)
        {
            if (result == null) return;
            selection = id; chart.Select(id); ShowHover(id);
            Text("SelectedPath", result.PathFor(id)); Find<TextBlock>("SelectedPath").ToolTip = result.PathFor(id); UpdateActions();
        }
        private void SyncRow(int id)
        {
            var files = Find<DataGrid>("Files");
            var rows = files.ItemsSource as List<FileRow>;
            if (rows == null) return;
            FileRow row = rows.FirstOrDefault(r => r.Id == id);
            if (row != null) { files.SelectedItem = row; files.ScrollIntoView(row); }
        }
        private void ShowHover(int id)
        {
            if (result == null) return;
            Node node = result.Nodes[id >= 0 ? id : current];
            Text("HoverName", node.Name);
            Text("HoverSize", node.Link ? node.SkipReason : "File size " + Format.Size(node.Bytes) + " · On disk " + (result.Allocation == null ? "pending" : result.Allocation.Display(id >= 0 ? id : current)) + (node.Incomplete ? " · Partial" : ""));
        }
        private void CollectSelected() { if (selection >= 0) AddToCollection(selection); }
        private void AddToCollection(int id)
        {
            if (driveStale || result == null || id <= 0 || id >= result.Nodes.Count) return;
            if (collection.Any(existing => result.IsAncestor(existing, id))) return;
            collection.RemoveAll(existing => result.IsAncestor(id, existing));
            collection.Add(id); UpdateCollection();
        }
        private void UpdateCollection()
        {
            long total = result == null ? 0 : collection.Sum(id => result.Nodes[id].Bytes);
            Text("CollectionSummary", "Cleanup list  ·  " + collection.Count + " items" + (collection.Count > 0 ? "  ·  " + Format.Size(total) + " selected" : ""));
        }
        private void ReviewCollection()
        {
            var dialog = new Window { Owner = Window, Title = "Review cleanup list", Width = 760, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Sunburst.ColorBrush("#141D2B"), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI") };
            var panel = new DockPanel { Margin = new Thickness(24) };
            var heading = new TextBlock { Text = "Your cleanup list", FontSize = 24, Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            var note = new TextBlock { Text = "Review only. Deletion is not enabled in this preview. Selected size is not a reclaimable-space estimate.", TextWrapping = TextWrapping.Wrap, Foreground = Sunburst.ColorBrush("#AFBDD0"), Margin = new Thickness(0, 0, 0, 18) }; DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            var list = new ListBox { Background = Sunburst.ColorBrush("#1C293B"), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            Action populate = delegate { list.ItemsSource = result == null ? new string[0] : collection.Select(id => Format.Size(result.Nodes[id].Bytes) + "    " + result.PathFor(id)).ToArray(); };
            populate();
            var remove = new Button { Content = "Remove selected", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 10, 0) };
            remove.Click += delegate { if (list.SelectedIndex >= 0) { collection.RemoveAt(list.SelectedIndex); populate(); UpdateCollection(); } }; buttons.Children.Add(remove);
            var clear = new Button { Content = "Clear list", Padding = new Thickness(12, 8, 12, 8) }; clear.Click += delegate { collection.Clear(); populate(); UpdateCollection(); }; buttons.Children.Add(clear);
            panel.Children.Add(list); dialog.Content = panel; Chrome.Apply(dialog, Window); dialog.ShowDialog();
        }
        private void CopySelected() { if (result != null && selection >= 0) Clipboard.SetText(result.PathFor(selection)); }
        private void RevealSelected()
        {
            if (driveStale || result == null || selection < 0) return;
            try { Shell.Reveal(result.PathFor(selection)); }
            catch (Exception ex) { MessageBox.Show(Window, ex.Message, "Unable to open Explorer"); }
        }
        private void ShowIssues()
        {
            if (result == null) return;
            string message = result.ErrorCount + " inaccessible locations; " + result.Links + " reparse points excluded.\n\n";
            message += "Sizes count visible directory entries, including each hard-link name. Cloud placeholders and other reparse points are excluded. Named streams and filesystem overhead are not included.\n\n";
            if (result.Canceled || result.Limited) message += "The scan stopped early. All displayed totals are partial.\n\n";
            message += string.Join("\n", result.Errors.Take(15));
            if (result.Allocation != null) message += "\n\nSize on disk: " + result.Allocation.UnknownFiles + " files not measured. Named streams, directory metadata, and filesystem overhead are not included. Hard-link names are counted separately, so totals are not unique physical usage.\n" + string.Join("\n", result.Allocation.Errors.Take(10));
            if (result.ErrorCount > 15) message += "\nAdditional errors omitted from this dialog.";
            MessageBox.Show(Window, message, "Scan coverage", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private async void RunUiTests()
        {
            var checks = new List<string>();
            try
            {
                for (int i = 0; i < 400 && (scanning || measuring || result == null); i++) await Task.Delay(50);
                if (result == null || scanning || measuring) throw new Exception("Scan did not finish. " + Find<TextBlock>("Status").Text);
                checks.Add("PASS: Window loads and background scan completes.");
                if (result.Allocation == null || !Find<TextBlock>("DiskTotal").Text.Contains("Size on disk")) throw new Exception("Disk size summary missing.");
                Find<CheckBox>("DiskMetric").IsChecked = true;
                if (!chart.OnDisk) throw new Exception("Chart metric did not switch.");
                Find<CheckBox>("DiskMetric").IsChecked = false;
                checks.Add("PASS: Both size metrics displayed and chart metric toggles.");
                if (System.Windows.Shell.WindowChrome.GetWindowChrome(Window) == null || Window.WindowStyle != WindowStyle.None) throw new Exception("Custom title bar missing.");
                SystemCommands.MaximizeWindow(Window); await Task.Delay(100);
                if (Window.WindowState != WindowState.Maximized) throw new Exception("Maximize failed.");
                SystemCommands.RestoreWindow(Window); await Task.Delay(100);
                checks.Add("PASS: Custom title bar supports maximize and restore.");
                ShowDuplicates();
                if (!duplicateUiPassed) throw new Exception("Duplicate dialog workflow failed: " + duplicateUiError);
                checks.Add("PASS: Duplicate dialog runs verification, shows results, and adds a selected result to cleanup list.");
                collection.Clear(); UpdateCollection();
                int folder = result.Nodes[0].Children.First(id => result.Nodes[id].Directory && result.Nodes[id].Children.Count > 0);
                Navigate(folder);
                await Task.Delay(150);
                if (current != folder || Find<TextBox>("PathInput").Text != result.PathFor(folder)) throw new Exception("Folder navigation failed.");
                checks.Add("PASS: Folder navigation updates path and chart.");
                int child = result.Nodes[folder].Children[0];
                Select(child); AddToCollection(child); AddToCollection(folder);
                if (collection.Count != 1 || collection[0] != folder) throw new Exception("Overlapping collection entries.");
                AddToCollection(child);
                if (collection.Count != 1) throw new Exception("Child entry duplicated.");
                checks.Add("PASS: Collection replaces descendants and ignores nested duplicates.");
                Find<TextBox>("Search").Text = "__no_matching_filename_879451__";
                await Task.Delay(200);
                if (Find<DataGrid>("Files").Items.Count != 0) throw new Exception("Filter failed.");
                checks.Add("PASS: Folder filter updates list without changing totals.");
                Find<TextBox>("Search").Text = "";
                await Task.Delay(200);
                if (Find<DataGrid>("Files").Items.Count != result.Nodes[folder].Children.Count) throw new Exception("Filter reset failed.");
                checks.Add("PASS: Clearing filter restores every direct child.");
                GoUp(); await Task.Delay(150);
                if (current != 0) throw new Exception("Parent navigation failed.");
                checks.Add("PASS: Parent navigation returns to scan root.");
                await TestNewInteractions(checks);
                Window.Width = Window.MinWidth; Window.Height = Window.MinHeight; Window.UpdateLayout();
                if (chart.ActualWidth < 200 || chart.ActualHeight < 100 || Find<DataGrid>("Files").ActualHeight < 100) throw new Exception("Minimum window layout failed.");
                checks.Add("PASS: Minimum window size keeps the chart and file list usable.");
                collection.Clear(); UpdateCollection();
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-test-results.txt"), checks);
                ScheduleScreenshot();
            }
            catch (Exception ex)
            {
                checks.Add("FAIL: " + ex);
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-test-results.txt"), checks);
                Window.Close();
            }
        }
        private void ScheduleScreenshot()
        {
            var screenshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            screenshotTimer.Tick += delegate
            {
                screenshotTimer.Stop();
                Window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(Window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"))) encoder.Save(stream);
                Window.Close();
            };
            screenshotTimer.Start();
        }
    }

    internal static class Shell
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint outAttributes);
        [DllImport("shell32.dll", PreserveSig = true)] private static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
        public static void Reveal(string path)
        {
            IntPtr pidl;
            uint attributes;
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes));
            try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0)); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
    }
}
