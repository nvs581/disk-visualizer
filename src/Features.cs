using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DiskVisualizer
{
    internal static class Chrome
    {
        public static void Apply(Window window, Window styleSource)
        {
            if (styleSource != null) window.Resources = styleSource.Resources;
            var content = (UIElement)window.Content;
            window.Content = null;
            window.WindowStyle = WindowStyle.None;
            WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            var outer = new Border { BorderBrush = Sunburst.ColorBrush("#344059"), BorderThickness = new Thickness(1), Background = window.Background };
            var layout = new DockPanel(); outer.Child = layout;
            var title = new DockPanel { Height = 40, Background = Sunburst.ColorBrush("#111A28"), LastChildFill = true };
            DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
            var controls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(controls, Dock.Right); title.Children.Add(controls);
            var minimize = CaptionButton("─", "Minimize", window);
            var maximize = CaptionButton("□", "Maximize", window);
            var close = CaptionButton("×", "Close", window);
            minimize.Click += delegate { SystemCommands.MinimizeWindow(window); };
            maximize.Click += delegate { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); };
            close.Click += delegate { SystemCommands.CloseWindow(window); };
            close.MouseEnter += delegate { close.Background = Sunburst.ColorBrush("#B8425B"); };
            close.MouseLeave += delegate { close.Background = Brushes.Transparent; };
            controls.Children.Add(minimize); controls.Children.Add(maximize); controls.Children.Add(close);
            title.Children.Add(new TextBlock { Text = "◉   " + window.Title, Foreground = Sunburst.ColorBrush("#B5BFD1"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(17, 0, 0, 0) });
            layout.Children.Add(content); window.Content = outer;
            window.StateChanged += delegate
            {
                outer.Padding = window.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                maximize.Content = window.WindowState == WindowState.Maximized ? "❐" : "□";
                AutomationProperties.SetName(maximize, window.WindowState == WindowState.Maximized ? "Restore" : "Maximize");
            };
        }
        private static Button CaptionButton(string text, string label, Window window)
        {
            var button = new Button { Content = text, Width = 46, Height = 38, FontSize = 16, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = label, Focusable = true };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true); AutomationProperties.SetName(button, label); return button;
        }
    }
    internal sealed class DuplicateRow
    {
        public int Id { get; set; }
        public string Group { get; set; }
        public string Path { get; set; }
        public string Size { get; set; }
        public string Verified { get; set; }
    }
    internal sealed partial class MainController
    {
        private bool measuring, sortDisk;
        private volatile int allocationProgress;
        private DuplicateResult duplicates;
        private bool duplicateUiPassed;
        private string duplicateUiError;
        private void SetupFeatures()
        {
            Chrome.Apply(Window, null);
            Button("Duplicates", ShowDuplicates);
            Find<CheckBox>("DiskMetric").Checked += delegate { chart.OnDisk = true; ReloadChart(); };
            Find<CheckBox>("DiskMetric").Unchecked += delegate { chart.OnDisk = false; ReloadChart(); };
        }
        private void ShowDuplicates()
        {
            if (driveStale || result == null || scanning || measuring) return;
            var dialog = new Window { Owner = Window, Title = "Find duplicate files", Width = 1050, Height = 650, MinWidth = 800, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Sunburst.ColorBrush("#141D2B"), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI") };
            var panel = new DockPanel { Margin = new Thickness(24) };
            var intro = new StackPanel(); DockPanel.SetDock(intro, Dock.Top); panel.Children.Add(intro);
            intro.Children.Add(new TextBlock { Text = "Same contents. Different copies.", FontSize = 25, FontWeight = FontWeights.SemiBold });
            intro.Children.Add(new TextBlock { Text = "Scope: " + result.RootPath, Foreground = Sunburst.ColorBrush("#B5BFD1"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 8) });
            intro.Children.Add(new TextBlock { Text = "Checks file contents, regardless of names. Matching files are verified byte for byte. Nothing is deleted automatically.", TextWrapping = TextWrapping.Wrap, Foreground = Sunburst.ColorBrush("#97A7BD"), Margin = new Thickness(0, 0, 0, 12) });
            var status = new TextBlock { Text = "Ready. This optional check reads files and may take longer than the storage scan.", TextWrapping = TextWrapping.Wrap, Foreground = Sunburst.ColorBrush("#C5B1FF"), Margin = new Thickness(0, 0, 0, 12) }; intro.Children.Add(status);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) }; intro.Children.Add(actions);
            var start = new Button { Content = "Find duplicates", Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel check", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            var errors = new Button { Content = "Skipped files", Margin = new Thickness(0, 0, 8, 0) };
            actions.Children.Add(start); actions.Children.Add(cancel); actions.Children.Add(errors);
            var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
            footer.Children.Add(new TextBlock { Text = "Identical content does not mean safe to remove. Shared hard-link names and empty files are omitted. Verification applies at the time shown; files can change later. Only the main data stream is compared.", TextWrapping = TextWrapping.Wrap, Foreground = Sunburst.ColorBrush("#97A7BD"), FontSize = 11 });
            var rowActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; footer.Children.Add(rowActions);
            var reveal = new Button { Content = "Show in Explorer", Margin = new Thickness(0, 0, 8, 0) };
            var add = new Button { Content = "Add selected to cleanup list" }; rowActions.Children.Add(reveal); rowActions.Children.Add(add);
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, Background = Brushes.Transparent, BorderThickness = new Thickness(0), GridLinesVisibility = DataGridGridLinesVisibility.None, EnableRowVirtualization = true };
            grid.Columns.Add(new DataGridTextColumn { Header = "GROUP", Binding = new Binding("Group"), Width = new DataGridLength(65) });
            grid.Columns.Add(new DataGridTextColumn { Header = "FILE LOCATION", Binding = new Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "FILE SIZE", Binding = new Binding("Size"), Width = new DataGridLength(95) });
            grid.Columns.Add(new DataGridTextColumn { Header = "VERIFIED AT", Binding = new Binding("Verified"), Width = new DataGridLength(100) });
            panel.Children.Add(grid);
            DuplicateResult shown = duplicates;
            Action populate = delegate
            {
                if (shown == null) return;
                var rows = new List<DuplicateRow>(); int groupId = 0;
                foreach (DuplicateGroup group in shown.Groups.OrderByDescending(g => g.Files[0].Snapshot.Length * (g.Files.Count - 1)))
                {
                    groupId++;
                    foreach (DuplicateFile file in group.Files) rows.Add(new DuplicateRow { Id = file.Id, Group = groupId.ToString(), Path = file.Path, Size = Format.Size(file.Snapshot.Length), Verified = group.VerifiedUtc.ToLocalTime().ToString("HH:mm:ss") });
                }
                grid.ItemsSource = rows;
                status.Text = (shown.Canceled ? "Canceled — partial results. " : "Check finished. ") + shown.Groups.Count + " verified groups · " + rows.Count + " files · " + shown.Skipped + " unreadable/changed · " + shown.HardLinks + " shared names omitted · " + shown.Elapsed.TotalSeconds.ToString("0.0") + " s" + (result.Nodes[0].Incomplete ? " · Storage scan was partial." : "");
            };
            populate();
            CancellationTokenSource source = null;
            bool alive = true;
            string progressText = "";
            var update = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            update.Tick += delegate { status.Text = progressText; };
            Action disconnected = delegate { update.Stop(); if (source != null) source.Cancel(); start.IsEnabled = false; reveal.IsEnabled = false; add.IsEnabled = false; status.Text = "Drive unavailable. Results are stale; close and rescan the drive."; };
            DriveUnavailable += disconnected;
            start.Click += async delegate
            {
                if (driveStale || source != null) return;
                start.IsEnabled = false; cancel.IsEnabled = true; grid.ItemsSource = null;
                source = new CancellationTokenSource(); progressText = "Grouping files by size…"; update.Start();
                try
                {
                    ScanResult snapshot = result;
                    DuplicateResult found = await Task.Run(() => DuplicateScanner.Find(snapshot, source.Token, text => progressText = text));
                    if (!alive) return;
                    shown = found; duplicates = found; populate();
                }
                catch (Exception ex) { if (alive) status.Text = "Check failed: " + ex.Message; }
                finally { update.Stop(); source.Dispose(); source = null; if (alive) { start.IsEnabled = !driveStale; cancel.IsEnabled = false; if (driveStale) disconnected(); } }
            };
            cancel.Click += delegate { if (source != null) { source.Cancel(); status.Text = "Canceling…"; } };
            errors.Click += delegate { if (shown != null) MessageBox.Show(dialog, shown.Skipped + " unreadable or changed candidates.\n" + shown.HardLinks + " shared names and " + shown.EmptyFiles + " empty files omitted.\nLinks/cloud placeholders were already skipped by the storage scan.\n\n" + string.Join("\n", shown.Errors), "Duplicate check coverage"); };
            reveal.Click += delegate { var row = grid.SelectedItem as DuplicateRow; if (!driveStale && row != null) { try { Shell.Reveal(row.Path); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } } };
            add.Click += delegate { var row = grid.SelectedItem as DuplicateRow; if (!driveStale && row != null) { AddToCollection(row.Id); status.Text = "Added to cleanup list. Nothing has been deleted."; } };
            dialog.Closed += delegate { DriveUnavailable -= disconnected; alive = false; update.Stop(); if (source != null) source.Cancel(); };
            if (arguments.Contains("--ui-test")) dialog.Loaded += async delegate
            {
                try
                {
                    start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    for (int i = 0; i < 1200 && !start.IsEnabled; i++) await Task.Delay(50);
                    if (!start.IsEnabled || shown == null) throw new Exception("Duplicate check did not complete.");
                    if (shown.Groups.Count > 0)
                    {
                        grid.SelectedIndex = 0;
                        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        if (!collection.Contains(((DuplicateRow)grid.SelectedItem).Id)) throw new Exception("Duplicate result was not added to cleanup list.");
                    }
                    dialog.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "duplicates-preview.png"))) encoder.Save(stream);
                    duplicateUiPassed = true;
                }
                catch (Exception ex) { duplicateUiError = ex.Message; }
                finally { dialog.Close(); }
            };
            dialog.Content = panel; Chrome.Apply(dialog, Window); dialog.ShowDialog();
        }
    }
}
