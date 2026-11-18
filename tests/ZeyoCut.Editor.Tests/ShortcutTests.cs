// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace ZeyoCut.Editor.Tests;

// The shortcuts table the window's keys run from and the Keyboard shortcuts view lists.
public class ShortcutTests
{
    private static List<List<string>> Keys(string action) =>
        Shortcuts.Groups(null).SelectMany(g => g.Rows).Single(r => r.Action == action).Keys.Select(k => k.Caps.ToList()).ToList();

    [Fact]
    public void EveryCommandHasAKeyAndEveryKeyOneCommand()
    {
        Assert.Empty(Enum.GetValues<Command>().Except(Shortcuts.All.Select(s => s.Command)));
        Assert.All(Shortcuts.All.GroupBy(s => (s.Key, s.Modifiers)), keys => Assert.Single(keys));
        Assert.All(Shortcuts.All, s => Assert.Equal(s.Command, Shortcuts.For(s.Key, s.Modifiers).Single().Command));
        Assert.Empty(Shortcuts.For(Key.Q, ModifierKeys.None));

        // Every row of the table is listed once, in its group.
        Assert.Equal(["Playback", "Editing", "Timeline", "Workspaces"], Shortcuts.Groups(null).Select(g => g.Name));
        Assert.Equal(Shortcuts.All.Select(s => (s.Group, s.Action)).Distinct(), Shortcuts.Groups(null).SelectMany(g => g.Rows.Select(r => (g.Name, r.Action))));
    }

    [Fact]
    public void TheListShowsEachWayToPressAnActionAsItsCaps()
    {
        Assert.Equal([["Ctrl", "Shift", "Z"], ["Ctrl", "Y"]], Keys("Redo"));
        Assert.Equal([["Ctrl", "D"]], Keys("Duplicate"));
        Assert.Equal([["←"], ["→"]], Keys("Previous / next frame"));
        Assert.Equal([["Alt", "←"], ["Alt", "→"]], Keys("Previous / next keyframe"));
        Assert.Equal([["Del"], ["Backspace"]], Keys("Delete"));
        Assert.Equal([["+"], ["−"]], Keys("Zoom in / out"));
        Assert.Equal([["?"], ["F1"]], Keys("Keyboard shortcuts"));

        // The digit row's 1 and the keypad's are one key in the list.
        Assert.Equal([["Ctrl", "1"]], Keys("Edit"));
        Assert.Equal([["Ctrl", "5"]], Keys("Captions"));
        Assert.Equal([["Ctrl", "6"]], Keys("Activity"));
        Assert.Equal([true, false], Shortcuts.Groups(null).SelectMany(g => g.Rows).Single(r => r.Action == "Redo").Keys.Select(k => k.First));
    }

    [Fact]
    public void SearchFindsAnActionByItsNameItsGroupOrItsKeys()
    {
        static List<string> Found(string search) => Shortcuts.Groups(search).SelectMany(g => g.Rows).Select(r => r.Action).ToList();

        Assert.Equal(["Duplicate"], Found("ctrl d"));
        Assert.Equal(["Duplicate"], Found("Ctrl+D"));
        Assert.Equal(["Delete"], Found("del"));
        Assert.Equal(["Snap on / off"], Found("snap"));
        Assert.Equal(["Redo"], Found("ctrl y"));
        Assert.Equal(["Keyboard shortcuts"], Found("?"));
        Assert.Equal(Shortcuts.Groups(null).Single(g => g.Name == "Timeline").Rows.Select(r => r.Action), Found("timeline"));
        Assert.Empty(Found("ctrl q"));
        Assert.Equal(Shortcuts.Groups(null).Count, Shortcuts.Groups("  ").Count);
    }
}
