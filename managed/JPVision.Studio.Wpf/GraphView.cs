using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JPVision.Studio;

public sealed class GraphView : FrameworkElement
{
    private const double CardWidth = 198;
    private FlowDocument? flow;
    private string? selected, dragging;
    private (string Node, string Port)? wire;
    private JsonObject? gestureDefinition;
    private Point pointer, start, dragOrigin, offset = new(25, 20);
    private bool panning;
    private double zoom = 1;
    public string? SelectedId => selected;
    private readonly HashSet<string> selection = new();
    public IReadOnlyCollection<string> SelectedIds => selection;
    public event Action<string?>? SelectionChanged;
    public event Action<string, EditorPoint>? AddRequested;
    public event Action<string>? Error;
    public event Action<double>? ZoomChanged;
    public event Action<string>? EditRequested;
    public Dictionary<string, string> NodeStates { get; } = new();
    public GraphView()
    {
        Focusable = true;
        AllowDrop = true;
        Cursor = Cursors.Arrow;
    }
    public void SetDocument(FlowDocument document)
    {
        EndGesture();
        if (flow is not null)
            flow.Changed -= Refresh;
        flow = document;
        flow.Changed += Refresh;
        selected = null;
        selection.Clear();
        Refresh();
    }
    public void Refresh()
    {
        if (flow is not null) selection.RemoveWhere(id => !flow.Positions.ContainsKey(id));
        if (flow is not null && (wire is not null || dragging is not null || panning) &&
            (!ReferenceEquals(gestureDefinition, flow.Definition) ||
             wire is { } w && !flow.Nodes.Any(n => FlowDocument.Id(n!) == w.Node) ||
             dragging is string moving && !flow.Positions.ContainsKey(moving)))
            EndGesture();
        if (selected is not null && flow is not null && !flow.Nodes.Any(n => FlowDocument.Id(n!) == selected))
            Select(null);
        InvalidateVisual();
    }
    private void EndGesture()
    {
        wire = null;
        dragging = null;
        panning = false;
        gestureDefinition = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }
    public void Select(string? id)
    {
        selection.Clear();
        if (id is not null) selection.Add(id);
        selected = id;
        InvalidateVisual();
        SelectionChanged?.Invoke(id);
    }
    private static string Text(JsonNode n, string key) => n[key]!.GetValue<string>();
    private static double CardHeight(NodeSpec spec) => Math.Max(98, 54 + Math.Max(spec.VisibleInputs.Count, spec.VisibleOutputs.Count) * 18);
    private Point World(Point p) => new((p.X - offset.X) / zoom, (p.Y - offset.Y) / zoom);
    private Point Port(string id, string port, bool output)
    {
        var spec = flow!.Spec(id);
        var ports = output ? spec.VisibleOutputs : spec.VisibleInputs;
        int i = ports.ToList().FindIndex(p => p.Id == port);
        if (i < 0 && (output ? spec.Outputs : spec.Inputs).Any(p => p.Id == port && p.Hidden))
            i = ports.ToList().FindIndex(p => spec.ImagePipeline ? p.Type == "Image" : p.Type == "Result");
        if (i < 0)
            throw new InvalidOperationException("连接的端口不存在：" + port);
        var pos = flow.Positions[id];
        return new(pos.X + (output ? CardWidth : 0), pos.Y + 49 + i * 18);
    }
    private (string Node, string Port, bool Output)? HitPort(Point p)
    {
        if (flow is null)
            return null;
        foreach (var node in flow.Nodes.Reverse())
        {
            string id = FlowDocument.Id(node!);
            var spec = flow.Spec(id);
            foreach (bool output in new[] { true, false })
            foreach (var port in output ? spec.VisibleOutputs : spec.VisibleInputs)
                if ((p - Port(id, port.Id, output)).Length < 9 / zoom)
                    return (id, port.Id, output);
        }
        return null;
    }
    private string? HitNode(Point p) => flow?.Nodes.Reverse().Select(n => FlowDocument.Id(n!)).FirstOrDefault(id =>
    {
        var pos = flow.Positions[id];
        return new Rect(pos.X, pos.Y, CardWidth, CardHeight(flow.Spec(id))).Contains(p);
    });
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(13, 13, 15)), null, new Rect(RenderSize));
        double grid = 20 * zoom;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(29, 29, 32)), .7);
        for (double x = offset.X % grid; x < ActualWidth; x += grid)
            dc.DrawLine(gridPen, new(x, 0), new(x, ActualHeight));
        for (double y = offset.Y % grid; y < ActualHeight; y += grid)
            dc.DrawLine(gridPen, new(0, y), new(ActualWidth, y));
        if (flow is null || flow.Nodes.Count == 0)
        {
            Label(dc, "从左侧拖入算子，连接相同类型的端口", new(25, 35), 14, Brushes.Gray);
            return;
        }
        dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, offset.X, offset.Y));
        var accent = new SolidColorBrush(Color.FromRgb(255, 48, 56));
        foreach (var edge in flow.Connections)
            Bezier(dc, Port(Text(edge!, "fromNode"), Text(edge!, "fromPort"), true), Port(Text(edge!, "toNode"), Text(edge!, "toPort"), false), new Pen(accent, 2));
        if (wire is { } w)
            Bezier(dc, Port(w.Node, w.Port, true), World(pointer), new Pen(Brushes.White, 1.6) { DashStyle = DashStyles.Dash });
        foreach (var node in flow.Nodes)
        {
            string id = FlowDocument.Id(node!);
            var spec = flow.Spec(id);
            var pos = flow.Positions[id];
            var rect = new Rect(pos.X, pos.Y, CardWidth, CardHeight(spec));
            var fill = new LinearGradientBrush(Color.FromRgb(43, 43, 47), Color.FromRgb(22, 22, 25), 90);
            dc.DrawRoundedRectangle(fill, new Pen(selection.Contains(id) ? accent : new SolidColorBrush(Color.FromRgb(113, 113, 119)), selection.Contains(id) ? 1.6 : 1), rect, 5, 5);
            dc.DrawLine(new Pen(accent, 2.4), new(pos.X + 6, pos.Y + 1), new(pos.X + CardWidth - 6, pos.Y + 1));
            Label(dc, spec.Type is "ExternalImage" or "ToGray" ? "▧" : spec.Type.Contains("Circle") ? "◎" : spec.Type.Contains("Match") || spec.Type == "FastLocate" ? "⊕" : "◇", new(pos.X + 12, pos.Y + 9), 21, accent);
            Label(dc, spec.Label, new(pos.X + 43, pos.Y + 11), 12, Brushes.White, true);
            for (int i = 0; i < spec.VisibleInputs.Count; i++)
            {
                var p = spec.VisibleInputs[i];
                var point = Port(id, p.Id, false);
                dc.DrawEllipse(accent, new Pen(Brushes.White, .7), point, 4.5, 4.5);
                Label(dc, spec.VisibleInputs.Count > 1 ? p.Label : p.Type, new(pos.X + 12, point.Y - 7), 10, Brushes.LightGray);
            }
            for (int i = 0; i < spec.VisibleOutputs.Count; i++)
            {
                var p = spec.VisibleOutputs[i];
                var point = Port(id, p.Id, true);
                bool connected = flow.Connections.Any(e => Text(e!, "fromNode") == id &&
                    (Text(e!, "fromPort") == p.Id || (p.Type == "Result" || spec.ImagePipeline && p.Type == "Image") && spec.Outputs.Any(old => old.Id == Text(e!, "fromPort") && old.Hidden)));
                dc.DrawEllipse(connected ? accent : new SolidColorBrush(Color.FromRgb(60, 60, 66)), new Pen(connected ? Brushes.White : Brushes.Gray, 1), point, 4.5, 4.5);
                string label = p.Type == "Result" ? "全部结果" : spec.VisibleOutputs.Count == 1 ? p.Type : p.Id + " · " + p.Type;
                Label(dc, label, new(pos.X + CardWidth - 12, point.Y - 7), 10, Brushes.LightGray, false, true);
            }
            if (NodeStates.TryGetValue(id, out var state))
                Label(dc, state, new(pos.X + 10, pos.Y + rect.Height - 15), 9, state.StartsWith("错误") ? accent : Brushes.Gray);
        }
        dc.Pop();
    }
    private void Label(DrawingContext dc, string text, Point point, double size, Brush color, bool bold = false, bool right = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (right)
            point.X -= formatted.Width;
        dc.DrawText(formatted, point);
    }
    private static void Bezier(DrawingContext dc, Point a, Point b, Pen pen)
    {
        double bend = Math.Max(60, Math.Abs(b.X - a.X) * .5);
        var path = new StreamGeometry();
        using (var c = path.Open())
        {
            c.BeginFigure(a, false, false);
            c.BezierTo(new(a.X + bend, a.Y), new(b.X - bend, b.Y), b, true, false);
        }
        path.Freeze();
        dc.DrawGeometry(null, pen, path);
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (flow is null)
            return;
        pointer = e.GetPosition(this);
        start = pointer;
        var hit = HitPort(World(pointer));
        if (e.ChangedButton == MouseButton.Right)
        {
            if (hit is { Output: false } h)
                flow.Disconnect(h.Node, h.Port);
            else if (HitNode(World(pointer)) is string id)
            {
                Select(id);
                var menu = new System.Windows.Controls.ContextMenu();
                var delete = new System.Windows.Controls.MenuItem { Header = "删除节点" };
                delete.Click += (_, _) => flow.Delete(id);
                menu.Items.Add(delete);
                menu.IsOpen = true;
            }
            return;
        }
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            panning = true;
            gestureDefinition = flow.Definition;
            dragOrigin = offset;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (hit is { Output: true } port)
        {
            wire = (port.Node, port.Port);
            gestureDefinition = flow.Definition;
            CaptureMouse();
            return;
        }
        string? node = HitNode(World(pointer));
        if (node is not null && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!selection.Add(node)) selection.Remove(node);
            selected = selection.Contains(node) ? node : selection.LastOrDefault();
            SelectionChanged?.Invoke(selected); InvalidateVisual(); return;
        }
        Select(node);
        if (node is not null)
        {
            if (e.ClickCount == 2)
            {
                EditRequested?.Invoke(node);
                return;
            }
            dragging = node;
            gestureDefinition = flow.Definition;
            var p = flow.Positions[node];
            dragOrigin = new(p.X, p.Y);
            CaptureMouse();
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        pointer = e.GetPosition(this);
        if (panning)
            offset = dragOrigin + (pointer - start);
        if (dragging is not null && flow is not null)
            flow.Positions[dragging] = new(Math.Max(0, dragOrigin.X + (pointer.X - start.X) / zoom), Math.Max(0, dragOrigin.Y + (pointer.Y - start.Y) / zoom));
        if (panning || dragging is not null || wire is not null)
            InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (flow is not null && wire is { } w && HitPort(World(e.GetPosition(this))) is { Output: false } target)
            try
            {
                var candidates = flow.GetConnectionCandidates(w.Node, w.Port, target.Node, target.Port);
                if (candidates.Count <= 1)
                    flow.Connect(w.Node, w.Port, target.Node, target.Port);
                else
                {
                    var document = flow;
                    var definition = document.Definition;
                    var menu = new ContextMenu();
                    menu.Items.Add(new MenuItem { Header = "选择发送的结果字段", IsEnabled = false });
                    foreach (var candidate in candidates)
                    {
                        var field = candidate;
                        var item = new MenuItem { Header = field.Label };
                        item.Click += (_, _) =>
                        {
                            if (!ReferenceEquals(flow, document) || !ReferenceEquals(document.Definition, definition))
                                return;
                            try { document.Connect(w.Node, field.Id, target.Node, target.Port); } catch (Exception ex) { Error?.Invoke(ex.Message); }
                        };
                        menu.Items.Add(item);
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(flow, document) && ReferenceEquals(document.Definition, definition))
                            menu.IsOpen = true;
                    });
                }
            }
            catch (Exception ex) { Error?.Invoke(ex.Message); }
        if (flow is not null && dragging is string id && flow.Positions.TryGetValue(id, out var final))
        {
            flow.Positions[id] = new(dragOrigin.X, dragOrigin.Y);
            if (final != flow.Positions[id])
                flow.Move(id, final);
        }
        EndGesture();
        InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.12 : 1 / 1.12, e.GetPosition(this));
        e.Handled = true;
    }
    public void Zoom(double factor, Point? anchor = null)
    {
        var p = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var world = World(p);
        zoom = Math.Clamp(zoom * factor, .2, 2.5);
        offset = new(p.X - world.X * zoom, p.Y - world.Y * zoom);
        InvalidateVisual();
        ZoomChanged?.Invoke(zoom);
    }
    public void Fit()
    {
        if (flow is null || flow.Nodes.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
            return;
        double left = flow.Positions.Values.Min(p => p.X), top = flow.Positions.Values.Min(p => p.Y), right = flow.Positions.Values.Max(p => p.X + CardWidth), bottom = flow.Nodes.Max(n => flow.Positions[FlowDocument.Id(n!)].Y + CardHeight(flow.Spec(FlowDocument.Id(n!))));
        zoom = Math.Clamp(Math.Min((ActualWidth - 60) / (right - left), (ActualHeight - 50) / (bottom - top)), .2, 1.25);
        offset = new(30 - left * zoom, 25 - top * zoom);
        InvalidateVisual();
        ZoomChanged?.Invoke(zoom);
    }
    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData("JPVision.Component") is string type)
        {
            var p = World(e.GetPosition(this));
            AddRequested?.Invoke(type, new(Math.Max(0, p.X), Math.Max(0, p.Y)));
            e.Handled = true;
        }
    }
    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("JPVision.Component") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
