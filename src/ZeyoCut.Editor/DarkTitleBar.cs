// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZeyoCut.Editor;

// Asks Windows (10 20H1 and later) to draw the window's title bar dark, to match the editor.
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var on = 1;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
