// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

public enum KeyframeMode
{
    DopeSheet,
    Graph,
}

// The Animate view's lower half. On the left the property tree: the animated layers with their
// values at the playhead. On the right their keyframes on a ruler, as a dope sheet (a row for each
// value, an image layer's drawings too) or as the graph of the values' curves, drawn from Core's
// interpolation, each fitted to the height. Clicking a key selects it (Ctrl adds or takes it away,
// Shift adds); dragging keys retimes them by whole frames, and in the graph changes their values
// too (Shift keeps to one way), through MoveKeyframesOperation. Clicking the ruler or an empty place
// moves the playhead. In the tree a layer's name selects the layer and its arrow opens it; a value
// selects its keys in the dope sheet and shows or hides its curve in the graph.
public sealed class KeyframeView : FrameworkElement
{
    public const double TreeWidth = 260;
    public const double RulerHeight = 24;
    public const double RowHeight = 32;
    public const double LegendHeight = 30;
    private const double Pad = 12;
    private const double Grab = 7;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x7B, 0x93, 0xFF), Color.FromRgb(0xF0, 0xA9, 0x4B), Color.FromRgb(0x3D, 0xD6, 0x8C),
        Color.FromRgb(0xFF, 0x7A, 0xA8), Color.FromRgb(0x4C, 0xC9, 0xD9), Color.FromRgb(0xE8, 0xD4, 0x4D),
    ];

    private static readonly Brush Unshown = Frozen(Color.FromRgb(0x5C, 0x62, 0x70));
    private static readonly Brush Grid = Frozen(Color.FromRgb(0x1E, 0x21, 0x28));
    private static readonly Brush KeyBrush = Frozen(Color.FromRgb(0xB4, 0xBA, 0xC6));
    private static readonly Brush SummaryBrush = Frozen(Color.FromRgb(0x7C, 0x83, 0x92));

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<string> _shown = [];
    private readonly HashSet<KeyframeRef> _selection = [];
    private Project? _project;
    private IReadOnlyList<AnimateLayer> _layers = [];
    private string? _selectedLayer;
    private string? _shownFor;
    private int _frames = 1;
    private int _playhead;
    private double _scroll;
    private KeyframeMode _mode;
    private bool _fit;
    private bool _scrubbing;
    private Drag? _drag;

    // A drag let go, drawn where it was dropped until the project with its edit is shown.
    private Drag? _landing;

    public KeyframeView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    // The frame the playhead was moved to.
    public event EventHandler<int>? Seek;

    // Keys dragged: the edit, and the project it was made from.
    public event EventHandler<TimelineEdit>? Edit;

    public event EventHandler? SelectionChanged;

    // A layer's name clicked in the tree.
    public event EventHandler<string>? LayerChosen;

    public KeyframeMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _scroll = 0;
            InvalidateVisual();
        }
    }

    // The graph's ruler spans the shown curves' keys rather than the whole project.
    public bool FitCurves
    {
        get => _fit;
        set
        {
            _fit = value;
            InvalidateVisual();
        }
    }

    public int Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    public bool Dragging => _drag is { Active: true };

    // The selected keys, as the project shown has them.
    public IReadOnlyList<AnimateKey> Selection => AllKeys().Where(k => _selection.Contains(k.Ref)).ToList();

    // The value clicked last in the tree, or the one of the selected keys: what Alt+K keys.
    public AnimateRow? CurrentRow { get; private set; }

    // The values whose curves the graph draws, in the order of their colors.
    public IReadOnlyList<AnimateRow> ShownRows => _shown.Select(Row).OfType<AnimateRow>().ToList();

    public IReadOnlyList<AnimateLayer> Layers => _layers;

    public void Show(Project project, IReadOnlyList<AnimateLayer> layers, string? selectedLayer, int frames)
    {
        _project = project;
        _layers = layers;
        _frames = Math.Max(1, frames);
        _landing = null;
        _selectedLayer = selectedLayer;
        var reveal = false;
        if (selectedLayer != _shownFor)
        {
            // A layer newly selected opens, and the graph shows its curves.
            _shownFor = selectedLayer;
            if (selectedLayer is not null)
            {
                _expanded.Add(selectedLayer);
                reveal = true;
                _shown.Clear();
                _shown.AddRange(layers.Where(l => l.LayerId == selectedLayer).SelectMany(l => l.Rows).Where(r => !r.Drawing).Take(Palette.Length).Select(r => r.Key));
            }
        }

        _shown.RemoveAll(key => Row(key) is null);
        var keys = AllKeys().Select(k => k.Ref).ToHashSet();
        _selection.RemoveWhere(r => !keys.Contains(r));
        if (CurrentRow is { } current)
        {
            CurrentRow = Row(current.Key);
        }

        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        if (reveal && selectedLayer is not null)
        {
            Reveal(selectedLayer);
        }

        InvalidateVisual();
    }

    public void Select(IEnumerable<KeyframeRef> keys)
    {
        _selection.Clear();
        _selection.UnionWith(keys);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ClearSelection() => Select([]);

    // Where a key is drawn now: on its row in the dope sheet, on its curve in the graph; null when
    // it isn't on screen.
    public Point? PointOf(AnimateKey key)
    {
        var (left, right) = Columns();
        var x = XOf(key.Time, left, right);
        if (_mode == KeyframeMode.Graph)
        {
            if (!_shown.Contains($"{key.LayerId}.{key.Property}") || Row($"{key.LayerId}.{key.Property}") is not { } row)
            {
                return null;
            }

            var (top, bottom) = PlotRows();
            var (min, max) = Range(row, null);
            return new Point(x, YOf(key.Value, min, max, top, bottom));
        }

        return Lines().FirstOrDefault(l => l.Row?.Key == $"{key.LayerId}.{key.Property}") is { } line ? new Point(x, line.Top + RowHeight / 2) : null;
    }

    // A tree row's place: a layer's (property null) or one of its values'.
    public Rect? RowBounds(string layerId, string? property) =>
        Lines().FirstOrDefault(l => l.Layer.LayerId == layerId && l.Row?.Property == property) is { } line ? new Rect(0, line.Top, TreeWidth, RowHeight) : null;

    // What pressing the left button at a point, moving the mouse and letting go do. The mouse's
    // events call them, and so can a test.
    public void Press(Point point, ModifierKeys modifiers)
    {
        Focus();
        if (_project is null)
        {
            return;
        }

        if (point.X < TreeWidth)
        {
            PressTree(point, modifiers);
            return;
        }

        if (point.Y < RulerHeight)
        {
            _scrubbing = true;
            CaptureMouse();
            SeekTo(point.X);
            return;
        }

        var hit = KeysAt(point);
        if (hit.Count == 0)
        {
            if (!modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift) && _selection.Count > 0)
            {
                ClearSelection();
            }

            SeekTo(point.X);
            return;
        }

        var refs = hit.Select(k => k.Ref).ToList();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var all = refs.All(_selection.Contains);
            Select(all ? _selection.Except(refs).ToList() : _selection.Union(refs).ToList());
            return;
        }

        var alone = false;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            Select(_selection.Union(refs).ToList());
        }
        else if (!refs.All(_selection.Contains))
        {
            Select(refs);
        }
        else
        {
            // Let go without dragging, a click on a selected key selects it alone.
            alone = _selection.Count > refs.Count;
        }

        CurrentRow = Row($"{hit[0].LayerId}.{hit[0].Property}") ?? CurrentRow;
        var ranges = ShownRows.ToDictionary(r => r.Key, r => Range(r, null), StringComparer.Ordinal);
        _drag = new Drag(point, Selection, ranges, alone ? refs : null);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Press(e.GetPosition(this), Keyboard.Modifiers);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed, Keyboard.Modifiers);
    }

    public void MoveTo(Point point, bool pressed, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_scrubbing)
        {
            if (pressed)
            {
                SeekTo(point.X);
            }

            return;
        }

        if (_drag is not { } drag || !pressed)
        {
            Cursor = point.X >= TreeWidth && point.Y >= RulerHeight && KeysAt(point).Count > 0 ? Cursors.SizeAll : null;
            return;
        }

        var dx = point.X - drag.Pressed.X;
        var dy = point.Y - drag.Pressed.Y;
        if (!drag.Active && Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
        {
            return;
        }

        drag.Active = true;
        if (_mode == KeyframeMode.Graph && modifiers.HasFlag(ModifierKeys.Shift))
        {
            (dx, dy) = Math.Abs(dx) >= Math.Abs(dy) ? (dx, 0.0) : (0.0, dy);
        }

        var (left, right) = Columns();
        var (from, to) = Span();
        var fps = _project!.Fps;
        var seconds = Math.Round(dx / Math.Max(1, right - left) * (to - from) * fps) / fps;
        drag.Seconds = drag.Keys.Count == 0 ? 0 : Math.Max(seconds, -drag.Keys.Min(k => k.Time));
        if (_mode == KeyframeMode.Graph)
        {
            var (top, bottom) = PlotRows();
            drag.Lift = -dy / Math.Max(1, bottom - top);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Release();
    }

    public void Release()
    {
        if (_scrubbing)
        {
            _scrubbing = false;
            ReleaseMouseCapture();
            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        ReleaseMouseCapture();
        if (!drag.Active)
        {
            if (drag.Alone is { } alone)
            {
                Select(alone);
            }

            return;
        }

        var moves = drag.Keys.Select(k => new KeyframeMove(k.LayerId, k.Property, k.Time, KeyframeWindow.Round(k.Time + drag.Seconds), Lifted(drag, k))).ToList();
        if (moves.All(m => m.NewTime == m.Time && m.Value is null))
        {
            InvalidateVisual();
            return;
        }

        _landing = drag;
        var from = _project!;
        Select(moves.Select(m => new KeyframeRef(m.LayerId, m.Property, m.NewTime)).ToList());
        Edit?.Invoke(this, new TimelineEdit(new MoveKeyframesOperation(moves), from));
    }

    public void CancelDrag()
    {
        _drag = null;
        _scrubbing = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_mode == KeyframeMode.Graph && e.GetPosition(this).X >= TreeWidth)
        {
            return;
        }

        _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * RowHeight * 1.5, 0, MaxScroll);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var panel = Resource("PanelBrush", Brushes.Black);
        var divider = new Pen(Resource("DividerBrush", Brushes.DimGray), 1);
        var text = Resource("TextBrush", Brushes.White);
        var secondary = Resource("SecondaryTextBrush", Brushes.Gray);
        dc.DrawRectangle(panel, null, new Rect(0, 0, width, height));
        if (_project is null)
        {
            return;
        }

        var (left, right) = Columns();
        DrawRuler(dc, left, right, width, divider, secondary, dpi);
        if (_layers.Count == 0)
        {
            var empty = new FormattedText("Nothing here is animated yet. Select a layer in the Edit view and use the diamond next to a value in its inspector, or ask Claude to animate it.",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, secondary, dpi) { MaxTextWidth = Math.Max(40, width - 48) };
            dc.DrawText(empty, new Point(24, RulerHeight + 20));
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, width, Math.Max(0, height - RulerHeight))));
        if (_mode == KeyframeMode.Graph)
        {
            DrawGraph(dc, left, right, secondary, dpi);
        }
        else
        {
            DrawDopeSheet(dc, left, right, divider, secondary, dpi);
        }

        DrawTree(dc, panel, divider, text, secondary, dpi);
        dc.Pop();

        var x = XOf(FrameMath.TimeOf(_project, _playhead), left, right);
        if (x >= left - 1 && x <= right + 1)
        {
            dc.DrawLine(new Pen(Resource("PlayheadBrush", Brushes.Red), 2), new Point(x, 0), new Point(x, height - (_mode == KeyframeMode.Graph ? LegendHeight : 0)));
        }
    }

    private void PressTree(Point point, ModifierKeys modifiers)
    {
        if (point.Y < RulerHeight || Lines().FirstOrDefault(l => point.Y >= l.Top && point.Y < l.Top + RowHeight) is not { } line)
        {
            return;
        }

        if (line.Row is not { } row)
        {
            if (point.X < 24 + line.Layer.Depth * 14)
            {
                if (!_expanded.Remove(line.Layer.LayerId))
                {
                    _expanded.Add(line.Layer.LayerId);
                }

                _scroll = Math.Clamp(_scroll, 0, MaxScroll);
                InvalidateVisual();
            }
            else if (line.Layer.LayerId != Camera.Id)
            {
                _expanded.Add(line.Layer.LayerId);
                LayerChosen?.Invoke(this, line.Layer.LayerId);
            }

            return;
        }

        CurrentRow = row;
        if (_mode == KeyframeMode.Graph && !row.Drawing)
        {
            if (!_shown.Remove(row.Key))
            {
                _shown.Add(row.Key);
                if (_shown.Count > Palette.Length)
                {
                    _shown.RemoveAt(0);
                }
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        var refs = row.Keys.Select(k => k.Ref).ToList();
        Select(modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift) ? _selection.Union(refs).ToList() : refs);
    }

    // The keys under a point: on a value's row or curve, or every key of a layer at that time on its
    // own row in the dope sheet.
    private List<AnimateKey> KeysAt(Point point)
    {
        var (left, right) = Columns();
        if (_mode == KeyframeMode.Graph)
        {
            var (top, bottom) = PlotRows();
            foreach (var row in ShownRows.AsEnumerable().Reverse())
            {
                var (min, max) = Range(row, null);
                var key = row.Keys.Where(k => Near(new Point(XOf(k.Time, left, right), YOf(k.Value, min, max, top, bottom)), point)).MinBy(k => Math.Abs(XOf(k.Time, left, right) - point.X));
                if (key is not null)
                {
                    return [key];
                }
            }

            return [];
        }

        if (Lines().FirstOrDefault(l => point.Y >= l.Top && point.Y < l.Top + RowHeight) is not { } line)
        {
            return [];
        }

        var keys = line.Row?.Keys ?? line.Layer.Rows.SelectMany(r => r.Keys).ToList();
        var nearest = keys.Where(k => Math.Abs(XOf(k.Time, left, right) - point.X) <= Grab).MinBy(k => Math.Abs(XOf(k.Time, left, right) - point.X));
        if (nearest is null)
        {
            return [];
        }

        return line.Row is null ? keys.Where(k => Math.Abs(k.Time - nearest.Time) < KeyframeEditor.TimeTolerance).ToList() : [nearest];
    }

    private void DrawRuler(DrawingContext dc, double left, double right, double width, Pen divider, Brush secondary, double dpi)
    {
        dc.DrawRectangle(Resource("HeaderBrush", Brushes.Black), null, new Rect(TreeWidth, 0, Math.Max(0, width - TreeWidth), RulerHeight));
        dc.DrawLine(divider, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        var label = new FormattedText(_mode == KeyframeMode.Graph ? "CURVES" : "PROPERTIES", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 10, secondary, dpi);
        dc.DrawText(label, new Point(12, (RulerHeight - label.Height) / 2));
        var (from, to) = Span();
        var step = TimelineScale.LabelStep((right - left) / (to - from), 72);
        for (var i = (long)Math.Ceiling(from / step - 1e-9); i * step <= to + 1e-9; i++)
        {
            var time = i * step;
            var x = Math.Round(XOf(time, left, right)) + 0.5;
            dc.DrawLine(divider, new Point(x, 0), new Point(x, RulerHeight));
            var tick = new FormattedText(Seconds(time, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, secondary, dpi);
            if (x + 4 + tick.Width <= width)
            {
                dc.DrawText(tick, new Point(x + 4, (RulerHeight - tick.Height) / 2));
            }
        }
    }

    private void DrawTree(DrawingContext dc, Brush panel, Pen divider, Brush text, Brush secondary, double dpi)
    {
        dc.DrawRectangle(panel, null, new Rect(0, RulerHeight, TreeWidth, Math.Max(0, ActualHeight - RulerHeight)));
        dc.DrawLine(divider, new Point(TreeWidth - 0.5, 0), new Point(TreeWidth - 0.5, ActualHeight));
        var chosen = Resource("SelectionBrush", Brushes.DarkSlateBlue);
        var accent = Resource("AccentBrush", Brushes.SteelBlue);
        var icon = Resource("IconTextBrush", Brushes.LightGray);
        var time = FrameMath.TimeOf(_project!, _playhead);
        foreach (var line in Lines())
        {
            if (line.Top + RowHeight < RulerHeight || line.Top > ActualHeight)
            {
                continue;
            }

            var indent = line.Layer.Depth * 14;
            if (line.Row is not { } row)
            {
                if (line.Layer.LayerId == _selectedLayer)
                {
                    dc.DrawRectangle(accent, null, new Rect(0, line.Top + 6, 2, RowHeight - 12));
                }

                var open = _expanded.Contains(line.Layer.LayerId);
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    var (cx, cy) = (12 + indent + 4, line.Top + RowHeight / 2);
                    g.BeginFigure(open ? new Point(cx - 4, cy - 2) : new Point(cx - 2, cy - 4), false, false);
                    g.LineTo(open ? new Point(cx, cy + 2) : new Point(cx + 2, cy), true, false);
                    g.LineTo(open ? new Point(cx + 4, cy - 2) : new Point(cx - 2, cy + 4), true, false);
                }

                arrow.Freeze();
                dc.DrawGeometry(null, new Pen(open ? text : icon, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arrow);
                var name = new FormattedText(line.Layer.LayerId, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 13, open ? text : icon, dpi)
                {
                    MaxTextWidth = Math.Max(10, TreeWidth - 90 - indent),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(name, new Point(28 + indent, line.Top + (RowHeight - name.Height) / 2));
                var kind = new FormattedText(line.Layer.Kind, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, secondary, dpi);
                dc.DrawText(kind, new Point(28 + indent + name.WidthIncludingTrailingWhitespace + 6, line.Top + (RowHeight - kind.Height) / 2 + 1));
                continue;
            }

            var shownIndex = _shown.IndexOf(row.Key);
            var shown = _mode == KeyframeMode.Graph && shownIndex >= 0;
            if (shown || CurrentRow?.Key == row.Key && _mode == KeyframeMode.DopeSheet)
            {
                dc.DrawRectangle(chosen, null, new Rect(0, line.Top, TreeWidth - 1, RowHeight));
            }

            var swatch = shown ? Frozen(Palette[shownIndex]) : Unshown;
            dc.DrawRoundedRectangle(row.Drawing ? Unshown : swatch, null, new Rect(28 + indent, line.Top + RowHeight / 2 - 5, 10, 10), 2, 2);
            var label = new FormattedText(row.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, shown ? text : icon, dpi)
            {
                MaxTextWidth = Math.Max(10, TreeWidth - 130 - indent),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(label, new Point(46 + indent, line.Top + (RowHeight - label.Height) / 2));
            var value = new FormattedText(AnimateModel.ValueAt(_project!, row, time), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, secondary, dpi)
            {
                MaxTextWidth = 76,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Right,
            };
            dc.DrawText(value, new Point(TreeWidth - 12 - 76, line.Top + (RowHeight - value.Height) / 2));
        }
    }

    private void DrawDopeSheet(DrawingContext dc, double left, double right, Pen divider, Brush secondary, double dpi)
    {
        var accent = Resource("AccentBrush", Brushes.SteelBlue);
        var white = Resource("TextBrush", Brushes.White);
        var bar = new Pen(Resource("BorderBrush", Brushes.DimGray), 2);
        var gridPen = new Pen(Grid, 1);
        foreach (var line in Lines())
        {
            if (line.Top + RowHeight < RulerHeight || line.Top > ActualHeight)
            {
                continue;
            }

            var y = line.Top + RowHeight / 2;
            dc.DrawLine(gridPen, new Point(TreeWidth, line.Top + RowHeight - 0.5), new Point(ActualWidth, line.Top + RowHeight - 0.5));
            if (line.Row is not { } row)
            {
                foreach (var time in line.Layer.Rows.SelectMany(r => r.Keys).Select(k => Moved(k).Time).Distinct().Order())
                {
                    Diamond(dc, XOf(time, left, right), y, 3.5, SummaryBrush, null);
                }

                continue;
            }

            var keys = row.Keys.Select(k => (Key: k, Time: Moved(k).Time)).OrderBy(k => k.Time).ToList();
            for (var i = 0; i + 1 < keys.Count; i++)
            {
                if (keys[i].Key.Easing != Easing.Hold && !row.Drawing)
                {
                    dc.DrawLine(bar, new Point(XOf(keys[i].Time, left, right) + 6, y), new Point(XOf(keys[i + 1].Time, left, right) - 6, y));
                }
            }

            foreach (var (key, time) in keys)
            {
                var x = XOf(time, left, right);
                var selected = _selection.Contains(key.Ref);
                Diamond(dc, x, y, selected ? 5.5 : 4.5, selected ? accent : KeyBrush, selected ? new Pen(white, 1.5) : null);
                if (key.Name is { } name)
                {
                    var label = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, secondary, dpi);
                    dc.DrawText(label, new Point(x + 8, y - label.Height / 2));
                }
            }
        }
    }

    private void DrawGraph(DrawingContext dc, double left, double right, Brush secondary, double dpi)
    {
        var (top, bottom) = PlotRows();
        var gridPen = new Pen(Grid, 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(top + (bottom - top) * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(TreeWidth, y), new Point(ActualWidth, y));
        }

        var rows = ShownRows;
        if (rows.Count == 0)
        {
            var hint = new FormattedText("Click a value on the left to draw its curve.", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, secondary, dpi);
            dc.DrawText(hint, new Point(left, top + 12));
        }

        var white = Resource("TextBrush", Brushes.White);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var color = Frozen(Palette[i]);
            if (AnimateModel.Value(_project!, row.LayerId, row.Property) is not { } value)
            {
                continue;
            }

            var curve = Curve(value, row);
            var (min, max) = Range(row, _drag?.Ranges);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (var x = left; x <= right + 0.5; x += 2)
                {
                    var point = new Point(x, YOf(Interpolator.Evaluate(curve, TimeAt(x, left, right)), min, max, top, bottom));
                    if (x == left)
                    {
                        g.BeginFigure(point, false, false);
                    }
                    else
                    {
                        g.LineTo(point, true, false);
                    }
                }
            }

            line.Freeze();
            dc.DrawGeometry(null, new Pen(color, i == 0 ? 2.5 : 2), line);
            foreach (var key in row.Keys)
            {
                var (time, at) = Moved(key);
                var point = new Point(XOf(time, left, right), YOf(at, min, max, top, bottom));
                Diamond(dc, point.X, point.Y, 4.5, color, new Pen(Resource("PanelBrush", Brushes.Black), 1));
                if (_selection.Contains(key.Ref))
                {
                    Diamond(dc, point.X, point.Y, 8, null, new Pen(white, 2));
                }
            }
        }

        // The legend: each curve's color and name.
        var lx = left;
        for (var i = 0; i < rows.Count; i++)
        {
            var name = new FormattedText(_layers.Count(l => l.Rows.Count > 0) > 1 ? $"{rows[i].LayerId} · {rows[i].Label}" : rows[i].Label,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, Resource("IconTextBrush", Brushes.LightGray), dpi);
            var y = ActualHeight - LegendHeight / 2;
            dc.DrawRoundedRectangle(Frozen(Palette[i]), null, new Rect(lx, y - 1.5, 14, 3), 1.5, 1.5);
            dc.DrawText(name, new Point(lx + 20, y - name.Height / 2));
            lx += 20 + name.WidthIncludingTrailingWhitespace + 16;
        }
    }

    // A value's curve as drawn: during a drag, with the dragged keys where they're going.
    private AnimatedValue Curve(AnimatedValue value, AnimateRow row)
    {
        var drag = _drag is { Active: true } ? _drag : _landing;
        if (drag is null || !drag.Keys.Any(k => k.LayerId == row.LayerId && k.Property == row.Property))
        {
            return value;
        }

        var keys = value.Keyframes.Select(k =>
        {
            var view = row.Keys.FirstOrDefault(v => Math.Abs(v.Time - k.Time) < KeyframeEditor.TimeTolerance);
            var (time, at) = view is null ? (k.Time, k.Value) : Moved(view);
            return new Keyframe { Time = time, Value = at, Easing = k.Easing, Bezier = k.Bezier };
        }).OrderBy(k => k.Time).ToList();
        return new AnimatedValue(value.Value) { Keyframes = keys };
    }

    // A key's time and value as drawn: moved by the drag, or by the one let go.
    private (double Time, double Value) Moved(AnimateKey key)
    {
        var drag = _drag is { Active: true } ? _drag : _landing;
        if (drag is null || !drag.Keys.Any(k => k.Ref == key.Ref))
        {
            return (key.Time, key.Value);
        }

        return (KeyframeWindow.Round(key.Time + drag.Seconds), Lifted(drag, key) ?? key.Value);
    }

    // The value a graph drag gives a key of a curve shown, rounded as the tree shows it; null for
    // the others, which keep theirs.
    private double? Lifted(Drag drag, AnimateKey key)
    {
        if (_mode != KeyframeMode.Graph || key.Drawing || drag.Lift == 0 || !drag.Ranges.TryGetValue($"{key.LayerId}.{key.Property}", out var range)
            || Row($"{key.LayerId}.{key.Property}") is not { } row)
        {
            return null;
        }

        var value = key.Value + drag.Lift * (range.Max - range.Min);
        return Math.Round(value * row.Factor, 1) / row.Factor;
    }

    // The values a curve is drawn between, at the bottom and the top: its lowest and highest over the
    // ruler, with room; the other way up for a value that grows down the frame.
    private (double Min, double Max) Range(AnimateRow row, IReadOnlyDictionary<string, (double Min, double Max)>? frozen)
    {
        if (frozen is not null && frozen.TryGetValue(row.Key, out var kept))
        {
            return kept;
        }

        if (AnimateModel.Value(_project!, row.LayerId, row.Property) is not { } value)
        {
            return (0, 1);
        }

        var (from, to) = Span();
        var samples = Enumerable.Range(0, 201).Select(i => Interpolator.Evaluate(value, from + (to - from) * i / 200)).Concat(row.Keys.Select(k => k.Value)).ToList();
        var (min, max) = (samples.Min(), samples.Max());
        if (max - min < 1e-9)
        {
            var spread = Math.Max(Math.Abs(min) * 0.1, 1 / row.Factor);
            (min, max) = (min - spread, max + spread);
        }

        var pad = (max - min) * 0.12;
        return row.Downward ? (max + pad, min - pad) : (min - pad, max + pad);
    }

    // The times the ruler spans: the whole project, or in the graph with Fit curves the shown curves'
    // keys with room around them.
    private (double From, double To) Span()
    {
        var duration = Math.Max(_project?.Duration ?? 1, 0.1);
        if (_mode == KeyframeMode.Graph && _fit && ShownRows.SelectMany(r => r.Keys).Select(k => k.Time).ToList() is { Count: > 0 } times)
        {
            var (first, last) = (times.Min(), times.Max());
            var room = Math.Max((last - first) * 0.08, 0.1);
            return (Math.Max(0, first - room), last + room);
        }

        return (0, duration);
    }

    private (double Left, double Right) Columns() => (TreeWidth + Pad, Math.Max(TreeWidth + Pad + 1, ActualWidth - Pad));

    private (double Top, double Bottom) PlotRows() => (RulerHeight + 16, Math.Max(RulerHeight + 17, ActualHeight - LegendHeight - 8));

    private double XOf(double time, double left, double right)
    {
        var (from, to) = Span();
        return left + (time - from) / (to - from) * (right - left);
    }

    private double TimeAt(double x, double left, double right)
    {
        var (from, to) = Span();
        return from + (x - left) / Math.Max(1, right - left) * (to - from);
    }

    private static double YOf(double value, double min, double max, double top, double bottom) =>
        bottom - (value - min) / (max - min) * (bottom - top);

    private void SeekTo(double x)
    {
        var (left, right) = Columns();
        var frame = Math.Clamp(FrameMath.NearestFrame(_project!, Math.Max(0, TimeAt(x, left, right))), 0, _frames - 1);
        if (frame != _playhead)
        {
            Seek?.Invoke(this, frame);
        }
    }

    // Scrolls the list so a layer and its values are in view, or as much of them as fits from the
    // layer's own row.
    private void Reveal(string layerId)
    {
        var lines = Lines();
        var index = lines.FindIndex(l => l.Layer.LayerId == layerId && l.Row is null);
        var visible = ActualHeight - RulerHeight - (_mode == KeyframeMode.Graph ? LegendHeight : 0);
        if (index < 0 || visible <= RowHeight)
        {
            return;
        }

        var top = lines[index].Top + _scroll - RulerHeight - 4;
        var height = (1 + (_expanded.Contains(layerId) ? lines[index].Layer.Rows.Count : 0)) * RowHeight;
        if (top < _scroll)
        {
            _scroll = top;
        }
        else if (top + height > _scroll + visible)
        {
            _scroll = Math.Min(top, top + height - visible + 8);
        }

        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    private List<Line> Lines()
    {
        var lines = new List<Line>();
        var top = RulerHeight + 4 - _scroll;
        foreach (var layer in _layers)
        {
            lines.Add(new Line(layer, null, top));
            top += RowHeight;
            if (_expanded.Contains(layer.LayerId))
            {
                foreach (var row in layer.Rows)
                {
                    lines.Add(new Line(layer, row, top));
                    top += RowHeight;
                }
            }
        }

        return lines;
    }

    private double MaxScroll =>
        Math.Max(0, _layers.Sum(l => 1 + (_expanded.Contains(l.LayerId) ? l.Rows.Count : 0)) * RowHeight + 8 - (ActualHeight - RulerHeight));

    private AnimateRow? Row(string key) => _layers.SelectMany(l => l.Rows).FirstOrDefault(r => r.Key == key);

    private IEnumerable<AnimateKey> AllKeys() => _layers.SelectMany(l => l.Rows).SelectMany(r => r.Keys);

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= Grab && Math.Abs(a.Y - b.Y) <= Grab;

    private static string Seconds(double time, double step) =>
        time.ToString(step < 1 ? "0.0#" : "0", CultureInfo.InvariantCulture) + "s";

    private static void Diamond(DrawingContext dc, double x, double y, double size, Brush? fill, Pen? pen)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(x, y - size), fill is not null, true);
            g.LineTo(new Point(x + size, y), true, false);
            g.LineTo(new Point(x, y + size), true, false);
            g.LineTo(new Point(x - size, y), true, false);
        }

        shape.Freeze();
        dc.DrawGeometry(fill, pen, shape);
    }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record Line(AnimateLayer Layer, AnimateRow? Row, double Top);

    // Keys being dragged: by whole frames (Seconds) and, in the graph, by a share of each curve's
    // height (Lift), with the curves' ranges as they were when it began. Alone: the keys to select
    // on their own when it turns out to be a click.
    private sealed class Drag(Point pressed, IReadOnlyList<AnimateKey> keys, IReadOnlyDictionary<string, (double Min, double Max)> ranges, IReadOnlyList<KeyframeRef>? alone)
    {
        public Point Pressed { get; } = pressed;

        public IReadOnlyList<AnimateKey> Keys { get; } = keys;

        public IReadOnlyDictionary<string, (double Min, double Max)> Ranges { get; } = ranges;

        public IReadOnlyList<KeyframeRef>? Alone { get; } = alone;

        public bool Active { get; set; }

        public double Seconds { get; set; }

        public double Lift { get; set; }
    }
}
