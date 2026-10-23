using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EwCapture.Core;
using EwCapture.Core.Recognition;

namespace EwCapture;

/// <summary>Finds the running game and copies (parts of) its client area off the screen.</summary>
static class GameWindow
{
    public sealed record Capture(Frame Frame, Size2 Client, MonitorInfo Monitor, string Process, string WindowMode)
    {
        /// <summary>The pixels as a PNG, for diagnostic uploads only.</summary>
        public byte[] ToPng()
        {
            var handle = GCHandle.Alloc(Frame.Bgra, GCHandleType.Pinned);
            try
            {
                using var bmp = new Bitmap(Frame.Width, Frame.Height, Frame.Width * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally { handle.Free(); }
        }
    }

    /// <summary>Where the game's client area is on screen right now.</summary>
    public sealed record Window(IntPtr Handle, string Process, Point Origin, int Width, int Height)
    {
        public double Scale => Height / 1440.0;
    }

    // Listing every process is the expensive part of finding the game; reuse the window while it lives.
    static (IntPtr Handle, string Process, DateTime At) _cached;
    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(15);

    /// <summary>The game window, or null with a reason the user can act on.</summary>
    /// <summary>True while a Path of Exile process is up, minimised or not: the updater never restarts then.</summary>
    public static bool GameRunning() =>
        Process.GetProcesses().Any(p => p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase));

    public static (Window? Window, string? Problem) Find(bool onlyIfInFront)
    {
        if (_cached.Handle == IntPtr.Zero || !Native.IsWindow(_cached.Handle) || DateTime.UtcNow - _cached.At > Recheck)
        {
            var proc = Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            _cached = proc is null ? default : (proc.MainWindowHandle, proc.ProcessName, DateTime.UtcNow);
        }
        var hwnd = _cached.Handle;
        if (hwnd == IntPtr.Zero) return (null, "Path of Exile 2 isn't running.");
        if (Native.IsIconic(hwnd)) return (null, "The game is minimised.");
        if (onlyIfInFront && Native.GetForegroundWindow() != hwnd) return (null, "The game isn't in front.");

        Native.GetClientRect(hwnd, out var rect);
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return (null, "The game window has no size.");
        return (new Window(hwnd, _cached.Process, new Point(origin.X, origin.Y), width, height), null);
    }

    /// <summary>
    /// Just the rows the stash-open check needs (a few kilobytes), at 1440p scale:
    /// row 0 is 1440p row <see cref="TabStrip.GateTop"/>.
    /// </summary>
    public static Frame GrabGateStrip(Window w)
    {
        double s = w.Scale;
        int y = (int)Math.Floor(TabStrip.GateTop * s), h = Math.Max(1, (int)Math.Ceiling(TabStrip.GateRows * s));
        int width = Math.Min(w.Width, (int)Math.Ceiling(TabStrip.GateWidth * s));
        return Copy(w, 0, y, width, h).Resized(TabStrip.GateWidth, TabStrip.GateRows);
    }

    /// <summary>
    /// The client area, or with <paramref name="panelOnly"/> only its left part where the
    /// stash panel sits (a third of an ultrawide). Plain screen copy, the way poe2pricer
    /// does it: the game must be in front, and exclusive fullscreen comes out black.
    /// </summary>
    public static Capture Grab(Window w, bool panelOnly)
    {
        int width = panelOnly ? Math.Min(w.Width, (int)Math.Ceiling(1100 * w.Scale)) : w.Width;
        var frame = Copy(w, 0, 0, width, w.Height);
        var screen = Screen.FromHandle(w.Handle);
        var monitor = new MonitorInfo(screen.Bounds.Width, screen.Bounds.Height, Native.GetDpiForWindow(w.Handle) / 96.0);
        var hasCaption = (Native.GetWindowLongPtr(w.Handle, Native.GWL_STYLE) & Native.WS_CAPTION) != 0;
        var fillsScreen = w.Origin.X == screen.Bounds.X && w.Origin.Y == screen.Bounds.Y
            && w.Width == screen.Bounds.Width && w.Height == screen.Bounds.Height;
        var mode = hasCaption ? "windowed" : fillsScreen ? "borderless" : "borderless-partial";
        return new Capture(frame, new Size2(w.Width, w.Height), monitor, w.Process, mode);
    }

    /// <summary>The diagnostic screenshot path: the whole client area, or a reason.</summary>
    public static (Capture? Capture, string? Problem) Grab()
    {
        var (w, problem) = Find(onlyIfInFront: false);
        return w is null ? (null, problem) : (Grab(w, panelOnly: false), null);
    }

    static Frame Copy(Window w, int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(w.Origin.X + x, w.Origin.Y + y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bgra = new byte[width * height * 4];
        try
        {
            // Rows are copied one by one: the bitmap's stride can be wider than the row.
            for (int r = 0; r < height; r++) Marshal.Copy(bits.Scan0 + r * bits.Stride, bgra, r * width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(bits); }
        return new Frame(width, height, bgra);
    }
}
