namespace FcnFn;

internal static class IconFactory
{
    private const int Size = 16;

    // Colors are 0x00BBGGRR (COLORREF).
    private const uint Green = 0x0033AA22;
    private const uint Grey  = 0x00888888;
    private const uint White = 0x00FFFFFF;

    public static IntPtr CreateFnIcon(bool enabled)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memdc = Native.CreateCompatibleDC(screen);
        IntPtr color = Native.CreateCompatibleBitmap(screen, Size, Size);
        // Monochrome mask: all zero => whole icon opaque.
        IntPtr mask = Native.CreateBitmap(Size, Size, 1, 1, IntPtr.Zero);

        IntPtr oldBmp = Native.SelectObject(memdc, color);

        var rect = new Native.RECT { Left = 0, Top = 0, Right = Size, Bottom = Size };
        IntPtr brush = Native.CreateSolidBrush(enabled ? Green : Grey);
        Native.FillRect(memdc, ref rect, brush);
        Native.DeleteObject(brush);

        Native.SetBkMode(memdc, Native.TRANSPARENT);
        Native.SetTextColor(memdc, White);
        Native.DrawTextW(memdc, "Fn", 2, ref rect,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);

        Native.SelectObject(memdc, oldBmp);

        var ii = new Native.ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr icon = Native.CreateIconIndirect(ref ii);

        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        Native.DeleteDC(memdc);
        Native.ReleaseDC(IntPtr.Zero, screen);
        return icon;
    }
}
