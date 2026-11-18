// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace ZeyoCut.Editor;

// What a key does in the window. MainWindow.Handler says how and when it can.
public enum Command
{
    Play,
    PreviousFrame,
    NextFrame,
    BackSecond,
    ForwardSecond,
    FirstFrame,
    LastFrame,
    PreviousEdit,
    NextEdit,
    FullScreen,
    Split,
    Delete,
    Duplicate,
    Undo,
    Redo,
    Import,
    Cancel,
    AddMarker,
    Snap,
    ZoomIn,
    ZoomOut,
    Fit,
    SelectTool,
    BladeTool,
    PreviousKeyframe,
    NextKeyframe,
    Edit,
    Activity,
    Export,
    NewProject,
    OpenProject,
    Shortcuts,
}

// A key and the command it runs, in a row of the Keyboard shortcuts list: Group and Action name the
// row, and a row can have several keys, such as Left and Right for Previous / next frame. Caps
// replaces the keys the list shows, as ? for Shift and the key it's on.
public sealed record Shortcut(string Group, string Action, Command Command, Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public IReadOnlyList<string>? Caps { get; init; }

    // The keys to press, as the list shows them.
    public IReadOnlyList<string> Keys => Caps ??
    [
        .. Modifiers.HasFlag(ModifierKeys.Control) ? ["Ctrl"] : Array.Empty<string>(),
        .. Modifiers.HasFlag(ModifierKeys.Shift) ? ["Shift"] : Array.Empty<string>(),
        .. Modifiers.HasFlag(ModifierKeys.Alt) ? ["Alt"] : Array.Empty<string>(),
        Shortcuts.KeyName(Key),
    ];
}

// A row of the list: an action and the keys that run it, each key a set of caps.
public sealed record ShortcutRow(string Action, IReadOnlyList<ShortcutKeys> Keys);

// One way to run a row's action; First is false for the ones after it, which the list puts a / before.
public sealed record ShortcutKeys(IReadOnlyList<string> Caps, bool First);

public sealed record ShortcutGroup(string Name, IReadOnlyList<ShortcutRow> Rows);

// The editor's keys: the Keyboard shortcuts list shows this table, and the window's key handling
// runs the first shortcut for a key whose command can run, so the two can't disagree.
public static class Shortcuts
{
    public const string Playback = "Playback";
    public const string Editing = "Editing";
    public const string Timeline = "Timeline";
    public const string Workspaces = "Workspaces";

    public static readonly IReadOnlyList<Shortcut> All =
    [
        new(Playback, "Play / pause", Command.Play, Key.Space),
        new(Playback, "Previous / next frame", Command.PreviousFrame, Key.Left),
        new(Playback, "Previous / next frame", Command.NextFrame, Key.Right),
        new(Playback, "Back / forward a second", Command.BackSecond, Key.Left, ModifierKeys.Shift),
        new(Playback, "Back / forward a second", Command.ForwardSecond, Key.Right, ModifierKeys.Shift),
        new(Playback, "Go to start / end", Command.FirstFrame, Key.Home),
        new(Playback, "Go to start / end", Command.LastFrame, Key.End),
        new(Playback, "Previous / next edit", Command.PreviousEdit, Key.Up),
        new(Playback, "Previous / next edit", Command.NextEdit, Key.Down),
        new(Playback, "Full screen", Command.FullScreen, Key.F),
        new(Editing, "Split at playhead", Command.Split, Key.S),
        new(Editing, "Split at playhead", Command.Split, Key.B, ModifierKeys.Control),
        new(Editing, "Delete", Command.Delete, Key.Delete),
        new(Editing, "Delete", Command.Delete, Key.Back),
        new(Editing, "Duplicate", Command.Duplicate, Key.D, ModifierKeys.Control),
        new(Editing, "Undo", Command.Undo, Key.Z, ModifierKeys.Control),
        new(Editing, "Redo", Command.Redo, Key.Z, ModifierKeys.Control | ModifierKeys.Shift),
        new(Editing, "Redo", Command.Redo, Key.Y, ModifierKeys.Control),
        new(Editing, "Import media", Command.Import, Key.I, ModifierKeys.Control),
        new(Editing, "Cancel a drag, leave the blade, clear the selection", Command.Cancel, Key.Escape),
        new(Timeline, "Add marker", Command.AddMarker, Key.M),
        new(Timeline, "Snap on / off", Command.Snap, Key.N),
        new(Timeline, "Zoom in / out", Command.ZoomIn, Key.OemPlus),
        new(Timeline, "Zoom in / out", Command.ZoomIn, Key.Add),
        new(Timeline, "Zoom in / out", Command.ZoomOut, Key.OemMinus),
        new(Timeline, "Zoom in / out", Command.ZoomOut, Key.Subtract),
        new(Timeline, "Fit timeline", Command.Fit, Key.Z, ModifierKeys.Shift),
        new(Timeline, "Select tool / blade", Command.SelectTool, Key.V),
        new(Timeline, "Select tool / blade", Command.BladeTool, Key.B),
        new(Timeline, "Previous / next keyframe", Command.PreviousKeyframe, Key.Left, ModifierKeys.Alt),
        new(Timeline, "Previous / next keyframe", Command.NextKeyframe, Key.Right, ModifierKeys.Alt),
        new(Workspaces, "Edit", Command.Edit, Key.D1, ModifierKeys.Control),
        new(Workspaces, "Edit", Command.Edit, Key.NumPad1, ModifierKeys.Control),
        new(Workspaces, "Activity", Command.Activity, Key.D6, ModifierKeys.Control),
        new(Workspaces, "Activity", Command.Activity, Key.NumPad6, ModifierKeys.Control),
        new(Workspaces, "Export", Command.Export, Key.E, ModifierKeys.Control),
        new(Workspaces, "New project", Command.NewProject, Key.N, ModifierKeys.Control),
        new(Workspaces, "Open project", Command.OpenProject, Key.O, ModifierKeys.Control),
        new(Workspaces, "Keyboard shortcuts", Command.Shortcuts, Key.OemQuestion, ModifierKeys.Shift) { Caps = ["?"] },
        new(Workspaces, "Keyboard shortcuts", Command.Shortcuts, Key.F1),
    ];

    // The shortcuts for a key, in the order the key handling tries them.
    public static IEnumerable<Shortcut> For(Key key, ModifierKeys modifiers) =>
        All.Where(s => s.Key == key && s.Modifiers == modifiers);

    // The list as the Keyboard shortcuts view shows it, in groups: the rows whose group or action has
    // every word of the search in it, or that have a key with every word as one of its caps (any
    // case: "ctrl d" and "Ctrl+D" find Duplicate, "del" finds Delete).
    public static IReadOnlyList<ShortcutGroup> Groups(string? search)
    {
        var words = (search ?? "").Replace('+', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.GroupBy(s => s.Group)
            .Select(group => new ShortcutGroup(group.Key, group.GroupBy(s => s.Action)
                .Select(row => new ShortcutRow(row.Key, row.Select(s => s.Keys).DistinctBy(k => string.Join(" ", k))
                    .Select((caps, i) => new ShortcutKeys(caps, i == 0)).ToList()))
                .Where(row => Mentions(group.Key, row, words))
                .ToList()))
            .Where(group => group.Rows.Count > 0)
            .ToList();
    }

    public static string KeyName(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Delete => "Del",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.OemPlus or Key.Add => "+",
        Key.OemMinus or Key.Subtract => "−",
        Key.OemQuestion => "/",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((int)(key - Key.NumPad0)).ToString(),
        _ => key.ToString(),
    };

    private static bool Mentions(string group, ShortcutRow row, string[] words) =>
        words.All(w => group.Contains(w, StringComparison.OrdinalIgnoreCase))
        || words.All(w => row.Action.Contains(w, StringComparison.OrdinalIgnoreCase))
        || row.Keys.Any(k => words.All(w => k.Caps.Any(c => c.Equals(w, StringComparison.OrdinalIgnoreCase))));
}
