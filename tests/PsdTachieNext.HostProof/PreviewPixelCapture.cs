using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PsdTachieNext.HostProof;

/// <summary>Read-only capture of this synthetic host's actual WinForms preview client from the desktop.
/// No D2D drawing, Source Update, Seek, or private player access is performed.</summary>
internal static class PreviewPixelCapture
{
    internal sealed record Frame(int Width, int Height, byte[] Pixels, long PreviewHandle, bool Foreground)
    {
        internal void Save(string path)
        {
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file);
        }
    }
    internal static Frame Capture(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var host = Descendants(window).OfType<FrameworkElement>()
            .Where(x => x.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.PreviewViewModel")
            .Select(x => Public(x.DataContext!, "Host")).FirstOrDefault(x => x is not null)
            ?? throw new InvalidOperationException("No public PreviewViewModel.Host on this window.");
        var child = Public(host, "Child") ?? throw new InvalidOperationException("Preview has no WinForms child.");
        var handle = (IntPtr)(Public(child, "Handle") ?? IntPtr.Zero);
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || !GetClientRect(handle, out var rect))
            throw new InvalidOperationException("Actual preview client is not visible.");
        var point = new PointI();
        if (!ClientToScreen(handle, ref point)) throw new InvalidOperationException("Preview screen coordinates unavailable.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Preview client has no area.");
        var foreground = GetAncestor(GetForegroundWindow(), 2) == new WindowInteropHelper(window).Handle;
        if (!foreground) throw new InvalidOperationException("Synthetic host is not foreground; capture could be occluded.");
        var screen = GetDC(IntPtr.Zero); var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size=40, Width=width, Height=-height, Planes=1, BitCount=32 };
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) { DeleteDC(memory); ReleaseDC(IntPtr.Zero,screen); throw new InvalidOperationException("DIB allocation failed."); }
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory,0,0,width,height,screen,point.X,point.Y,0x00CC0020))
                throw new InvalidOperationException("Preview screen BitBlt failed.");
            var pixels=new byte[checked(width*height*4)]; Marshal.Copy(bits,pixels,0,pixels.Length);
            return new(width,height,pixels,handle.ToInt64(),foreground);
        }
        finally { SelectObject(memory,previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero,screen); }
    }
    private static object? Public(object value,string name) => value.GetType().GetProperty(name)?.GetValue(value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
                foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width,Height; public ushort Planes,BitCount; public uint Compression,SizeImage; public int XPels,YPels; public uint ClrUsed,ClrImportant; public uint Color; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out RectI rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref PointI point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint operation);
}
