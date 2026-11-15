using System;
using System.Runtime.InteropServices;

namespace Lueur.Core;

/// <summary>
/// Capture l'ecran principal en tres basse resolution (GDI StretchBlt en mode HALFTONE, donc la carte
/// graphique fait la moyenne) et en deduit la couleur dominante. Quelques millisecondes par image.
/// Limite : un jeu en plein ecran exclusif peut renvoyer du noir ; le mode "fenetre sans bordure" marche.
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    const int W = 48, H = 27;

    readonly IntPtr _screenDc, _memDc, _bitmap, _oldBitmap;
    readonly byte[] _pixels = new byte[W * H * 4];
    readonly BITMAPINFO _info;

    public ScreenSampler()
    {
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateCompatibleBitmap(_screenDc, W, H);
        _oldBitmap = SelectObject(_memDc, _bitmap);
        SetStretchBltMode(_memDc, HALFTONE);
        SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
        _info = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(),
            biWidth = W,
            biHeight = -H, // lignes de haut en bas
            biPlanes = 1,
            biBitCount = 32,
        };
    }

    /// <summary>
    /// Couleur dominante : moyenne ponderee par la saturation et la luminosite de chaque pixel, pour que
    /// les zones colorees comptent plus que le noir et le gris (sinon tout tire vers le gris).
    /// </summary>
    public Rgb Sample()
    {
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        StretchBlt(_memDc, 0, 0, W, H, _screenDc, 0, 0, sw, sh, SRCCOPY);
        var info = _info;
        GetDIBits(_memDc, _bitmap, 0, H, _pixels, ref info, DIB_RGB_COLORS);

        double r = 0, g = 0, b = 0, weight = 0, brightness = 0;
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            double pb = _pixels[i], pg = _pixels[i + 1], pr = _pixels[i + 2];
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double sat = max == 0 ? 0 : (max - min) / max;
            double w = 0.05 + sat * sat * (max / 255);
            r += pr * w; g += pg * w; b += pb * w; weight += w;
            brightness += max;
        }
        r /= weight; g /= weight; b /= weight;

        // La couleur garde sa teinte, mais sa luminosite suit celle de l'ecran (scene sombre = LEDs douces).
        double peak = Math.Max(r, Math.Max(g, b));
        double level = Math.Clamp(brightness / (_pixels.Length / 4) / 255 * 1.6, 0.06, 1);
        double scale = peak <= 0 ? 0 : 255 * level / peak;
        return new Rgb(Clamp(r * scale), Clamp(g * scale), Clamp(b * scale));
    }

    static byte Clamp(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));

    public void Dispose()
    {
        SelectObject(_memDc, _oldBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }

    // ------------------------------------------------------------------ Win32

    const int SRCCOPY = 0x00CC0020, HALFTONE = 4, DIB_RGB_COLORS = 0, SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, int start, int lines, byte[] bits, ref BITMAPINFO info, int usage);
}
