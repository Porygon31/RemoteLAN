using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteLAN.Security;

namespace RemoteLAN.Capture;

/// <summary>
/// Draws the real mouse cursor onto a copy of a captured frame. Neither DXGI nor GDI capture includes it.
/// </summary>
public sealed class CursorOverlay : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int CURSOR_SHOWING = 1;
    private const uint DI_NORMAL = 3;

    private Bitmap? _composite;

    /// <summary>
    /// Returns an internally reused bitmap holding the frame and the cursor.
    /// The source stays untouched: DXGI returns its previous bitmap when nothing changed, so drawing on it would leave cursor trails.
    /// </summary>
    public Bitmap Compose(Bitmap source)
    {
        if (_composite == null || _composite.Size != source.Size)
        {
            _composite?.Dispose();
            _composite = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgb);
        }

        using var graphics = Graphics.FromImage(_composite);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(source, new Rectangle(Point.Empty, source.Size));
        DrawCursor(graphics, source.Size);
        return _composite;
    }

    private static void DrawCursor(Graphics graphics, Size frame)
    {
        // GetCursorInfo only works from a thread attached to the input desktop (Default vs Winlogon)
        DesktopManager.EnsureThreadOnInputDesktop(out _);

        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0) return;
        if (!GetIconInfo(info.hCursor, out ICONINFO icon)) return;

        DeleteObject(icon.hbmMask);
        DeleteObject(icon.hbmColor);

        var screen = new Size(GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        Point position = MapToFrame(new Point(info.ptScreenPos.X, info.ptScreenPos.Y), screen, frame);

        IntPtr hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, position.X - icon.xHotspot, position.Y - icon.yHotspot, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    /// <summary>
    /// Cursor position and screen metrics share the process's DPI view while DXGI frames are always physical pixels,
    /// so the ratio is right in every DPI mode. A cursor on a secondary screen lands outside the frame and is clipped.
    /// </summary>
    internal static Point MapToFrame(Point cursor, Size screen, Size frame)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return cursor;
        return new Point(cursor.X * frame.Width / screen.Width, cursor.Y * frame.Height / screen.Height);
    }

    public void Dispose()
    {
        _composite?.Dispose();
        _composite = null;
    }
}
