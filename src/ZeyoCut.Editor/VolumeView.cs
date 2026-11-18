// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZeyoCut.Core.Animation;
using ZeyoCut.Core.Editing;
using ZeyoCut.Core.Model;

namespace ZeyoCut.Editor;

// The Audio view's volume automation: a lane for each sound role with its clips on the project's
// timeline, each with its sound's waveform and its volume as a line, a dot at each volume keyframe.
// Where ducking lowers the music its lane is shaded, the deeper the darker. Pressing a clip's line
// adds a keyframe there, at the level the line has or the one it's dragged to; dragging a dot
// changes its level, and its time by whole frames; Delete takes away the selected one. Clicking the
// ruler or an empty place moves the playhead, and a clip selects its layer.
public sealed class VolumeView : FrameworkElement
{
    public const double LabelWidth = 160;
    public const double RulerHeight = 24;
    public const double LaneHeight = 76;
    private const double ClipPad = 6;
    private const double LinePad = 5;
    private const double LabelBand = 18;
    private const double RightPad = 12;
    private const double Grab = 6;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Brush Back = Frozen(Color.FromRgb(0x12, 0x14, 0x18));
    private static readonly Brush RulerBack = Frozen(Color.FromRgb(0x10, 0x12, 0x16));
    private static readonly Pen Divider = new(Frozen(Color.FromRgb(0x23, 0x26, 0x2E)), 1);
    private static readonly Pen LaneLine = new(Frozen(Color.FromRgb(0x1A, 0x1D, 0x24)), 1);
    private static readonly Pen Tick = new(Frozen(Color.FromRgb(0x2A, 0x2E, 0x37)), 1);
    private static readonly Brush TickText = Frozen(Color.FromRgb(0x7C, 0x83, 0x92));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xE8, 0xEA, 0xEE));
    private static readonly Brush Secondary = Frozen(Color.FromRgb(0x9A, 0xA1, 0xAE));
    private static readonly Brush VoiceFill = Frozen(Color.FromRgb(0x22, 0x35, 0x5E));
    private static readonly Brush VoiceWave = Frozen(Color.FromArgb(0x99, 0x6F, 0x95, 0xE8));
    private static readonly Brush SoundFill = Frozen(Color.FromRgb(0x3A, 0x2C, 0x69));
    private static readonly Brush SoundWave = Frozen(Color.FromArgb(0x5A, 0xB9, 0xA6, 0xFF));
    private static readonly Brush OffFill = Frozen(Color.FromRgb(0x22, 0x1E, 0x33));
    private static readonly Pen OffBorder = new(Frozen(Color.FromRgb(0x4A, 0x3F, 0x6E)), 1) { DashStyle = DashStyles.Dash };
    private static readonly Brush OffText = Frozen(Color.FromRgb(0xA9, 0xA2, 0xC4));
    private static readonly Brush LineBrush = Frozen(Color.FromRgb(0xFF, 0xD2, 0x7A));
    private static readonly Pen LinePen = new(LineBrush, 2) { LineJoin = PenLineJoin.Round };
    private static readonly Pen Chosen = new(Brushes.White, 2);
    private static readonly Pen PlayheadPen = new(Frozen(Color.FromRgb(0xFF, 0x5D, 0x5D)), 2);
    private static readonly Color Ducked = Color.FromRgb(0x0B, 0x0D, 0x12);

    private Project? _project;
    private IReadOnlyList<MixerLane> _lanes = [];
    private IReadOnlyList<(double Time, double Value)> _duck = [];
    private int _frames = 1;
    private int _playhead;
    private bool _scrubbing;
    private Drag? _drag;

    public VolumeView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    // The frame the playhead was moved to.
    public event EventHandler<int>? Seek;

    // A keyframe added or dragged: the edit, and the project it was made from.
    public event EventHandler<TimelineEdit>? Edit;

    // A clip clicked: its layer.
    public event EventHandler<string>? LayerChosen;

    // The sound's peaks for an asset id, once made.
    public Func<string, AudioPeaks?>? Waveform { get; set; }

    // The keyframe clicked or dragged last.
    public KeyframeRef? Selected { get; private set; }

    public bool Dragging => _drag is { Moved: true };

    public int Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<MixerLane> Lanes => _lanes;

    public void Show(Project project, IReadOnlyList<MixerLane> lanes, IReadOnlyList<(double Time, double Value)> duck, int frames)
    {
        _project = project;
        _lanes = lanes;
        _duck = duck;
        _frames = Math.Max(1, frames);
        if (Selected is { } key && ClipOf(key.LayerId)?.Volume.Keyframes.Any(k => Math.Abs(k.Time - key.Time) < 1e-9) != true)
        {
            Selected = null;
        }

        InvalidateVisual();
    }

    // Where a clip's volume line is at a time, and where the dot of its keyframe at a time is (null
    // when the clip has none there).
    public Point LinePoint(string layerId, double time)
    {
        var clip = ClipOf(layerId)!;
        return new Point(X(time), Y(clip, Interpolator.Evaluate(clip.Volume, time)));
    }

    public Point? KeyPoint(string layerId, double time) =>
        ClipOf(layerId) is { } clip && clip.Volume.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 1e-9) is { } key
            ? new Point(X(key.Time), Y(clip, key.Value))
            : null;

    // What pressing the left button at a point, moving the mouse and letting go do. The mouse's
    // events call them, and so can a test.
    public void Press(Point point)
    {
        Focus();
        if (_project is null || point.X < LabelWidth)
        {
            return;
        }

        if (point.Y < RulerHeight)
        {
            _scrubbing = true;
            CaptureMouse();
            SeekTo(point.X);
            return;
        }

        if (DotAt(point) is { } dot)
        {
            Selected = new KeyframeRef(dot.Clip.LayerId, AnimatableProperties.Volume, dot.Key.Time);
            _drag = new Drag(dot.Clip, point, dot.Key.Time, dot.Key.Value, isNew: false);
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        if (ClipAt(point) is not { } clip)
        {
            SeekTo(point.X);
            return;
        }

        var time = Snap(TimeAt(point.X));
        var line = Y(clip, Interpolator.Evaluate(clip.Volume, time));
        if (Math.Abs(point.Y - line) <= Grab && time >= clip.Start && time <= clip.End)
        {
            _drag = new Drag(clip, point, time, Interpolator.Evaluate(clip.Volume, time), isNew: true);
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        LayerChosen?.Invoke(this, clip.LayerId);
        SeekTo(point.X);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MoveTo(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed);
    }

    public void MoveTo(Point point, bool pressed)
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
            Cursor = point.X >= LabelWidth && point.Y >= RulerHeight && (DotAt(point) is not null || OnLine(point)) ? Cursors.Hand : null;
            return;
        }

        var (dx, dy) = (point.X - drag.Pressed.X, point.Y - drag.Pressed.Y);
        if (!drag.Moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
        {
            return;
        }

        drag.Moved = true;
        var (top, bottom) = LineSpan(drag.Clip);
        drag.Value = MixerModel.VolumeAt((bottom - Math.Clamp(Y(drag.Clip, drag.From) + dy, top, bottom)) / (bottom - top));
        if (!drag.IsNew)
        {
            drag.Time = Math.Max(0, Snap(drag.Time0 + dx / PerSecond));
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
        var from = _project!;
        var layer = drag.Clip.LayerId;
        if (drag.IsNew)
        {
            Selected = new KeyframeRef(layer, AnimatableProperties.Volume, drag.Time);
            InvalidateVisual();
            Edit?.Invoke(this, new TimelineEdit(new SetKeyframesOperation([new KeyframeEntry(layer, AnimatableProperties.Volume, drag.Time, JsonSerializer.SerializeToElement(Math.Round(drag.Value, 5)))]), from));
            return;
        }

        if (!drag.Moved || (drag.Time == drag.Time0 && drag.Value == drag.From))
        {
            InvalidateVisual();
            return;
        }

        Selected = new KeyframeRef(layer, AnimatableProperties.Volume, drag.Time);
        InvalidateVisual();
        Edit?.Invoke(this, new TimelineEdit(new MoveKeyframesOperation([new KeyframeMove(layer, AnimatableProperties.Volume, drag.Time0, drag.Time, drag.Value == drag.From ? null : drag.Value)]), from));
    }

    public void CancelDrag()
    {
        _drag = null;
        _scrubbing = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth;
        dc.DrawRectangle(Back, null, new Rect(RenderSize));
        dc.DrawRectangle(RulerBack, null, new Rect(LabelWidth, 0, Math.Max(0, width - LabelWidth), RulerHeight));
        dc.DrawLine(Divider, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        dc.DrawLine(Divider, new Point(LabelWidth - 0.5, 0), new Point(LabelWidth - 0.5, RulerHeight + _lanes.Count * LaneHeight));
        if (_project is null)
        {
            return;
        }

        DrawRuler(dc, dpi);
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var top = RulerHeight + i * LaneHeight;
            dc.DrawLine(LaneLine, new Point(0, top + LaneHeight - 0.5), new Point(width, top + LaneHeight - 0.5));
            dc.DrawRectangle(lane.Brush, null, new Rect(12, top + (LaneHeight - 28) / 2, 3, 28));
            var name = new FormattedText(lane.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, Text, dpi);
            dc.DrawText(name, new Point(23, top + (LaneHeight - name.Height) / 2));
            dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, top, Math.Max(0, width - LabelWidth), LaneHeight)));
            foreach (var clip in lane.Clips)
            {
                DrawClip(dc, lane, clip, top, dpi);
            }

            dc.Pop();
        }

        var x = X(FrameMath.TimeOf(_project, _playhead));
        dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, RulerHeight + _lanes.Count * LaneHeight));
    }

    private void DrawRuler(DrawingContext dc, double dpi)
    {
        var seconds = Duration;
        var step = new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.FirstOrDefault(s => s * PerSecond >= 64, 1200);
        for (var t = 0.0; t <= seconds + 1e-9; t += step)
        {
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(Tick, new Point(x, 0), new Point(x, RulerHeight));
            var label = new FormattedText(Timecode.Short(t, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, TickText, dpi);
            dc.DrawText(label, new Point(x + 4, 6));
        }
    }

    private void DrawClip(DrawingContext dc, MixerLane lane, MixerClip clip, double laneTop, double dpi)
    {
        var rect = new Rect(X(clip.Start), laneTop + ClipPad, Math.Max(2, X(clip.End) - X(clip.Start)), LaneHeight - 2 * ClipPad);
        var voice = lane.Role == AudioRole.Voice;
        dc.DrawRoundedRectangle(clip.Off is null ? voice ? VoiceFill : SoundFill : OffFill, clip.Off is null ? null : OffBorder, rect, 6, 6);
        dc.PushClip(new RectangleGeometry(rect, 6, 6));
        if (clip.Off is null && clip.Asset is { } asset && Waveform?.Invoke(asset) is { } peaks)
        {
            var area = new Rect(rect.X, rect.Y + 8, rect.Width, Math.Max(0, rect.Height - 8));
            dc.DrawGeometry(voice ? VoiceWave : SoundWave, null, peaks.Outline(clip.TrimIn, clip.TrimIn + clip.End - clip.Start, area, Math.Max(1, (int)(area.Width / 2))));
        }

        var ducked = lane.Role == AudioRole.Music && clip.Off is null && DrawDuck(dc, rect, clip);
        var label = clip.Off is { } off ? $"{clip.LayerId} · {off}"
            : ducked ? $"{clip.LayerId} · ducked {MixerModel.Decibels(_project!.Audio.DuckLevel)} under voice"
            : lane.Role == AudioRole.Effects ? clip.LayerId
            : $"{clip.LayerId} · {lane.Name.ToLowerInvariant()}";
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 10, clip.Off is null ? Text : OffText, dpi)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 12),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(rect.X + 8, rect.Y + 3));

        // The volume line, as Core's interpolation has it, with the drag shown.
        var volume = Shown(clip);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var step = 2 / PerSecond;
            var first = true;
            for (var t = clip.Start; ; t = Math.Min(clip.End, t + step))
            {
                var point = new Point(X(t), Y(clip, Interpolator.Evaluate(volume, t)));
                if (first)
                {
                    context.BeginFigure(point, false, false);
                    first = false;
                }
                else
                {
                    context.LineTo(point, true, false);
                }

                if (t >= clip.End)
                {
                    break;
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, LinePen, geometry);
        dc.Pop();

        foreach (var key in volume.Keyframes.Where(k => k.Time >= clip.Start - 1e-9 && k.Time <= clip.End + 1e-9))
        {
            var at = new Point(X(key.Time), Y(clip, key.Value));
            var chosen = _drag is { } drag ? drag.Clip.LayerId == clip.LayerId && Math.Abs(drag.Time - key.Time) < 1e-9
                : Selected is { } selected && selected.LayerId == clip.LayerId && Math.Abs(selected.Time - key.Time) < 1e-9;
            dc.DrawEllipse(LineBrush, chosen ? Chosen : null, at, 4, 4);
            if (chosen && _drag is { Moved: true })
            {
                var level = new FormattedText(MixerModel.VolumeText(key.Value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, Text, dpi);
                dc.DrawText(level, new Point(at.X + 8, Math.Max(rect.Y, at.Y - level.Height - 2)));
            }
        }
    }

    // Shades the stretches of a music clip ducking lowers, as deep as it goes; true when there are.
    private bool DrawDuck(DrawingContext dc, Rect rect, MixerClip clip)
    {
        if (_duck.Count < 2)
        {
            return false;
        }

        var floor = Math.Pow(10, _project!.Audio.DuckLevel / 20);
        double Depth(double value) => floor >= 1 ? 0 : Math.Clamp((1 - value) / (1 - floor), 0, 1);
        var any = false;
        for (var i = 0; i + 1 < _duck.Count; i++)
        {
            var (t0, v0) = _duck[i];
            var (t1, v1) = _duck[i + 1];
            var (from, to) = (Math.Max(t0, clip.Start), Math.Min(t1, clip.End));
            if (to <= from || (Depth(v0) <= 0 && Depth(v1) <= 0))
            {
                continue;
            }

            any = true;
            var brush = new LinearGradientBrush(Color.FromArgb((byte)(Depth(v0) * 120), Ducked.R, Ducked.G, Ducked.B), Color.FromArgb((byte)(Depth(v1) * 120), Ducked.R, Ducked.G, Ducked.B),
                new Point(X(t0), 0), new Point(X(t1), 0)) { MappingMode = BrushMappingMode.Absolute };
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(X(from), rect.Y, X(to) - X(from), rect.Height));
        }

        return any;
    }

    // A clip's volume with the drag on it: the dot dragged, or the one being added.
    private AnimatedValue Shown(MixerClip clip)
    {
        if (_drag is not { } drag || drag.Clip.LayerId != clip.LayerId || (!drag.Moved && !drag.IsNew))
        {
            return clip.Volume;
        }

        var keys = clip.Volume.Keyframes.Select(k => new Keyframe { Time = k.Time, Value = k.Value, Easing = k.Easing, Bezier = k.Bezier?.ToArray() }).ToList();
        if (!drag.IsNew)
        {
            keys.RemoveAll(k => Math.Abs(k.Time - drag.Time0) < 1e-9);
        }

        keys.RemoveAll(k => Math.Abs(k.Time - drag.Time) < 1e-9);
        keys.Add(new Keyframe { Time = drag.Time, Value = drag.Value });
        return new AnimatedValue(clip.Volume.Value) { Keyframes = [.. keys.OrderBy(k => k.Time)] };
    }

    private (MixerClip Clip, Keyframe Key)? DotAt(Point point)
    {
        foreach (var clip in LaneAt(point.Y)?.Clips ?? [])
        {
            foreach (var key in clip.Volume.Keyframes.Where(k => k.Time >= clip.Start - 1e-9 && k.Time <= clip.End + 1e-9))
            {
                if (Math.Abs(X(key.Time) - point.X) <= Grab && Math.Abs(Y(clip, key.Value) - point.Y) <= Grab)
                {
                    return (clip, key);
                }
            }
        }

        return null;
    }

    private bool OnLine(Point point) =>
        ClipAt(point) is { } clip && Math.Abs(Y(clip, Interpolator.Evaluate(clip.Volume, TimeAt(point.X))) - point.Y) <= Grab;

    private MixerClip? ClipAt(Point point)
    {
        var time = TimeAt(point.X);
        return LaneAt(point.Y)?.Clips.LastOrDefault(c => time >= c.Start && time <= c.End);
    }

    private MixerLane? LaneAt(double y)
    {
        var index = (int)Math.Floor((y - RulerHeight) / LaneHeight);
        return y >= RulerHeight && index < _lanes.Count ? _lanes[index] : null;
    }

    private MixerClip? ClipOf(string layerId) => _lanes.SelectMany(l => l.Clips).FirstOrDefault(c => c.LayerId == layerId);

    private (double Top, double Bottom) LineSpan(MixerClip clip)
    {
        var index = _lanes.Select((l, i) => (l, i)).First(p => p.l.Clips.Contains(clip)).i;
        var top = RulerHeight + index * LaneHeight + ClipPad;
        return (top + LabelBand, top + LaneHeight - 2 * ClipPad - LinePad);
    }

    private double Y(MixerClip clip, double volume)
    {
        var (top, bottom) = LineSpan(clip);
        return bottom - MixerModel.LevelPosition(volume) * (bottom - top);
    }

    private double Duration => _project is null ? 1 : Math.Max(1.0 / _project.Fps, FrameMath.TimeOf(_project, _frames));

    private double PerSecond => Math.Max(1, ActualWidth - LabelWidth - RightPad) / Duration;

    private double X(double time) => LabelWidth + time * PerSecond;

    private double TimeAt(double x) => Math.Clamp((x - LabelWidth) / PerSecond, 0, Duration);

    private double Snap(double time) => KeyframeWindow.Round(Math.Round(time * _project!.Fps) / _project.Fps);

    private void SeekTo(double x)
    {
        if (_project is not null)
        {
            Seek?.Invoke(this, Math.Clamp(FrameMath.NearestFrame(_project, TimeAt(x)), 0, _frames - 1));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // A dot being dragged (from its time and level) or added (on the line at a time).
    private sealed class Drag(MixerClip clip, Point pressed, double time, double value, bool isNew)
    {
        public MixerClip Clip { get; } = clip;

        public Point Pressed { get; } = pressed;

        public double Time0 { get; } = time;

        public double From { get; } = value;

        public bool IsNew { get; } = isNew;

        public double Time { get; set; } = time;

        public double Value { get; set; } = value;

        public bool Moved { get; set; }
    }
}
