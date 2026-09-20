using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskVisualizer
{
    internal sealed partial class MainController
    {
        private int rowPreview = -1;
        private void SetupRowPreview(DataGrid files)
        {
            files.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed) return;
                PreviewContainer(files, e.OriginalSource as DependencyObject);
            };
            files.MouseLeave += delegate { ClearRowPreview(); };
            files.GotKeyboardFocus += delegate(object sender, KeyboardFocusChangedEventArgs e) { PreviewContainer(files, e.NewFocus as DependencyObject); };
            files.LostKeyboardFocus += delegate { if (!files.IsKeyboardFocusWithin) ClearRowPreview(); };
            files.UnloadingRow += delegate(object sender, DataGridRowEventArgs e)
            {
                var row = e.Row.Item as FileRow;
                if (row != null && row.Id == rowPreview) ClearRowPreview();
            };
            files.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(delegate { ClearRowPreview(); }));
            chart.MouseEnter += delegate { ClearRowPreview(); };
        }
        private void PreviewContainer(DataGrid files, DependencyObject source)
        {
            var container = source == null ? null : ItemsControl.ContainerFromElement(files, source) as DataGridRow;
            var row = container == null ? null : container.Item as FileRow;
            if (row == null) ClearRowPreview(); else PreviewRow(row.Id);
        }
        private void PreviewRow(int id)
        {
            if (result == null || id < 0 || id >= result.Nodes.Count || result.Nodes[id].Parent != current) { ClearRowPreview(); return; }
            if (rowPreview == id) return;
            rowPreview = id;
            chart.Preview(id);
            ShowHover(id);
            if (!chart.HasVisibleSector(id)) Find<TextBlock>("HoverSize").Text += " · No visible segment at this zoom";
        }
        private void ClearRowPreview()
        {
            rowPreview = -1;
            chart.Preview(-1);
            if (result != null) ShowHover(selection);
        }
        private void ReloadChart()
        {
            ClearRowPreview();
            chart.SetData(result, current);
            chart.Select(selection);
        }
    }
}
