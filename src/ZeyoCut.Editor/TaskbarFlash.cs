// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZeyoCut.Editor;

// Flashes the window's taskbar button until the window comes to the front, as Windows does for a
// program that wants attention (an export of the Render queue that finished).
internal static class TaskbarFlash
{
    private const uint Tray = 0x2;
    private const uint UntilFront = 0xC;

    public static void Flash(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = Tray | UntilFront,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        _ = FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
