using System;
using System.Runtime.InteropServices;

namespace WechatDraft.Windows;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapObject
    { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct MetafilePicture
    { public int MappingMode, XExtent, YExtent; public nint Metafile; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] internal static extern uint GlobalFlags(nint memory);
    [DllImport("kernel32.dll")] internal static extern uint GetThreadLocale();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int WideCharToMultiByte(uint codePage, uint flags,
        string text, int characters, byte[]? output, int bytes, nint defaultCharacter, nint usedDefaultCharacter);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CopyImage(nint image, uint type, int width, int height, uint flags);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] internal static extern int GetBitmapObject(nint bitmap, int size, out BitmapObject value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CopyEnhMetaFile(nint metafile, string? fileName);
    [DllImport("gdi32.dll")] internal static extern uint GetEnhMetaFileBits(nint metafile, uint size, byte[]? data);
    [DllImport("gdi32.dll")] internal static extern bool DeleteEnhMetaFile(nint metafile);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CopyMetaFile(nint metafile, string? fileName);
    [DllImport("gdi32.dll")] internal static extern uint GetMetaFileBitsEx(nint metafile, uint size, byte[]? data);
    [DllImport("gdi32.dll")] internal static extern bool DeleteMetaFile(nint metafile);
    [DllImport("gdi32.dll")] internal static extern uint GetPaletteEntries(nint palette, uint start, uint count, byte[]? entries);
    [DllImport("gdi32.dll")] internal static extern nint CreatePalette(byte[] palette);
}
