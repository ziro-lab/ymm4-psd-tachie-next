global using AlphaMode = Vortice.DCommon.AlphaMode;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace PsdTachieNext.Direct2D;

/// <summary>
/// Build-time bridge for the exact host's signed pitch overload. The standalone 3.8.3 renderer
/// passes uint; the verified 4.56.1.0 host surface accepts int. No drawing semantics change.
/// This file is compiled only by PsdTachieNext.Ymm4, not by the standalone renderer project.
/// </summary>
internal static class HostGraphicsCompat
{
    public static ID2D1Bitmap1 CreateBitmap(this ID2D1DeviceContext context, SizeI size,
        IntPtr data, uint pitch, BitmapProperties1 properties)
        => context.CreateBitmap(size, data, checked((int)pitch), properties);
}
