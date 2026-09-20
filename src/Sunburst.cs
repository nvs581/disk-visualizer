using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DiskVisualizer
{
    internal sealed class Sunburst : FrameworkElement
    {
        private sealed class Sector
        {
            public int Id;
            public double Start, End, Inner, Outer;
            public Brush Brush;
            public Geometry Geometry;
        }
        private readonly List<Sector> sectors = new List<Sector>();
        private ScanResult data;
        public bool OnDisk;
        private long Bytes(int id) { return data.SizeFor(id, OnDisk); }
        private int root, hovered = -1, selected = -1, preview = -1;
        internal int PreviewId { get { return preview; } }
        internal int SelectedId { get { return selected; } }
        internal int GeometryBuildCount { get; private set; }
        private Point center;
        private double hole;
        private bool dirty = true;
        private int levels = 3;
        public event Action<int, bool> Picked;
        public event Action<int> Hovered;
        internal static readonly string[] Palette = { "#AE94F9", "#6CBEEB", "#62D6C5", "#E8BC76", "#EA8DAB", "#829CF0", "#B5D984", "#CB9FDA" };
        public Sunburst()
        {
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            SizeChanged += delegate { dirty = true; InvalidateVisual(); };
            MouseMove += OnMove;
            MouseLeave += delegate { hovered = -1; if (Hovered != null) Hovered(-1); InvalidateVisual(); };
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                Point point = e.GetPosition(this);
                if ((point - center).Length < hole && data != null)
                {
                    if (Picked != null) Picked(data.Nodes[root].Parent < 0 ? root : data.Nodes[root].Parent, true);
                }
                else if (hovered >= 0 && Picked != null) Picked(hovered, e.ClickCount > 1);
            };
        }
        public void SetData(ScanResult result, int id) { data = result; root = id; hovered = -1; selected = -1; preview = -1; dirty = true; InvalidateVisual(); }
        public void Select(int id) { selected = id; InvalidateVisual(); }
        public void Preview(int id) { if (preview == id) return; preview = id; InvalidateVisual(); }
        internal bool HasVisibleSector(int id) { EnsureGeometry(); return sectors.Exists(s => s.Id == id); }
        internal bool InBranch(int id, int branch)
        {
            if (data == null || branch < 0) return false;
            for (int node = id; node >= 0; node = data.Nodes[node].Parent)
            {
                if (node == branch) return true;
                if (node == root) break;
            }
            return false;
        }
        private void OnMove(object sender, MouseEventArgs e)
        {
            Vector delta = e.GetPosition(this) - center;
            double radius = delta.Length;
            double angle = Math.Atan2(delta.Y, delta.X) + Math.PI / 2;
            if (angle < 0) angle += Math.PI * 2;
            int hit = -1;
            foreach (Sector sector in sectors)
                if (radius >= sector.Inner && radius <= sector.Outer && angle >= sector.Start && angle <= sector.End) { hit = sector.Id; break; }
            if (hit == hovered) return;
            hovered = hit;
            if (Hovered != null) Hovered(hit);
            InvalidateVisual();
        }
        private void EnsureGeometry()
        {
            center = new Point(ActualWidth / 2, ActualHeight / 2);
            double radius = Math.Max(30, Math.Min(ActualWidth, ActualHeight) / 2 - 28);
            hole = Math.Min(90, radius * 0.55);
            if (dirty)
            {
                sectors.Clear();
                levels = data == null ? 3 : VisibleDepth(root, 0);
                if (data != null && Bytes(root) > 0) Build(root, 0, 0, Math.PI * 2, -1, hole, (radius - hole) / levels);
                dirty = false;
                GeometryBuildCount++;
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            EnsureGeometry();
            double radius = Math.Max(30, Math.Min(ActualWidth, ActualHeight) / 2 - 28);
            int highlighted = hovered >= 0 ? hovered : preview;
            bool visibleHighlight = sectors.Exists(s => s.Id == highlighted);
            Brush divider = ColorBrush("#131B29"), outline = ColorBrush("#F1EDFF");
            var dividerPen = new Pen(divider, 1.6);
            for (int i = 0; i < levels; i++) dc.DrawEllipse(null, new Pen(ColorBrush("#222B3B"), 1), center, hole + (radius - hole) * (i + 1) / levels, hole + (radius - hole) * (i + 1) / levels);
            foreach (Sector sector in sectors)
            {
                dc.PushOpacity(visibleHighlight && !InBranch(sector.Id, highlighted) ? 0.40 : 1);
                dc.DrawGeometry(sector.Brush, dividerPen, sector.Geometry);
                dc.Pop();
            }
            foreach (Sector sector in sectors)
            {
                if (visibleHighlight && InBranch(sector.Id, highlighted)) dc.DrawGeometry(null, new Pen(outline, sector.Id == highlighted ? 2.5 : 1), sector.Geometry);
                else if (!visibleHighlight && sector.Id == selected) dc.DrawGeometry(null, new Pen(outline, 2), sector.Geometry);
            }
            dc.DrawEllipse(ColorBrush("#141D2B"), new Pen(ColorBrush("#2C3547"), 1), center, hole - 5, hole - 5);
            string title = data == null ? "Your disk," : Format.Size(Bytes(root)) + (OnDisk && data.Allocation != null && !data.Allocation.Known[root] ? "*" : "");
            string subtitle = data == null ? "made clear." : (OnDisk ? "SIZE ON DISK" : "FILE SIZE");
            Text(dc, title, Math.Min(26, hole * 0.32), "#F0F2F8", center.Y - 24, true);
            Text(dc, subtitle, data == null ? 21 : 10, data == null ? "#AD96F5" : "#9BA8BD", center.Y + 11, false);
            if (data != null) Text(dc, OnDisk && data.Allocation != null && !data.Allocation.Known[root] ? "* KNOWN BYTES ONLY" : root == 0 ? "SCAN ROOT" : "CLICK TO GO UP", 9, "#69798E", center.Y + 34, false);
        }
        private int VisibleDepth(int id, int depth)
        {
            if (depth >= 5 || data.Nodes[id].Children == null) return Math.Max(1, depth);
            int max = Math.Max(1, depth);
            foreach (int child in data.Nodes[id].Children)
            {
                if (Bytes(child) <= 0) continue;
                if ((double)Bytes(child) / Math.Max(1, Bytes(root)) < 0.002) continue;
                max = Math.Max(max, VisibleDepth(child, depth + 1));
                if (max >= 5) break;
            }
            return max;
        }
        private void Text(DrawingContext dc, string text, double size, string color, double y, bool bold)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, ColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, y));
        }
        private void Build(int id, int depth, double start, double end, int color, double inner, double width)
        {
            Node node = data.Nodes[id];
            if (depth >= levels || node.Children == null || Bytes(id) <= 0 || sectors.Count >= 5000) return;
            double angle = start;
            int index = 0;
            foreach (int childId in node.Children)
            {
                Node child = data.Nodes[childId];
                double span = (end - start) * ((double)Bytes(childId) / Bytes(id));
                int branchColor = depth == 0 ? index % Palette.Length : color;
                if (span * (inner + width) >= 1.5 && sectors.Count < 5000)
                {
                    Color baseColor = (Color)ColorConverter.ConvertFromString(Palette[branchColor]);
                    double fade = 1 - depth * 0.09;
                    var brush = new SolidColorBrush(Color.FromRgb((byte)(baseColor.R * fade), (byte)(baseColor.G * fade), (byte)(baseColor.B * fade)));
                    brush.Freeze();
                    sectors.Add(new Sector { Id = childId, Start = angle, End = angle + span, Inner = inner, Outer = inner + width, Brush = brush, Geometry = Wedge(angle, angle + span, inner, inner + width) });
                    Build(childId, depth + 1, angle, angle + span, branchColor, inner + width, width);
                }
                angle += span;
                index++;
            }
        }
        private Point Polar(double angle, double radius) { return new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius); }
        private Geometry Wedge(double start, double end, double inner, double outer)
        {
            end = Math.Min(end, start + Math.PI * 2 - 0.00001);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(Polar(start, inner), true, true);
                ctx.LineTo(Polar(start, outer), true, false);
                ctx.ArcTo(Polar(end, outer), new Size(outer, outer), 0, end - start > Math.PI, SweepDirection.Clockwise, true, false);
                ctx.LineTo(Polar(end, inner), true, false);
                ctx.ArcTo(Polar(start, inner), new Size(inner, inner), 0, end - start > Math.PI, SweepDirection.Counterclockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }
        internal static SolidColorBrush ColorBrush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    }
}
