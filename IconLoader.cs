using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockyDock;

internal static class IconLoader
{
    private static readonly Guid IidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string path)
    {
        if (!Cache.TryGetValue(path, out var src))
            Cache[path] = src = LoadUncached(path);
        return src;
    }

    private static ImageSource? LoadUncached(string path)
    {
        var info = new Native.SHFILEINFO();
        uint cb = (uint)Marshal.SizeOf<Native.SHFILEINFO>();
        if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
        {
            if (Native.SHGetFileInfo(path, 0, ref info, cb, Native.SHGFI_SYSICONINDEX) == IntPtr.Zero) return null;
        }
        else
        {
            // Élément shell (appli du Store, etc.) : on passe par son PIDL.
            if (Native.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
            try
            {
                if (Native.SHGetFileInfoPidl(pidl, 0, ref info, cb, Native.SHGFI_PIDL | Native.SHGFI_SYSICONINDEX) == IntPtr.Zero) return null;
            }
            finally { Native.ILFree(pidl); }
        }

        foreach (int kind in new[] { Native.SHIL_JUMBO, Native.SHIL_EXTRALARGE })
        {
            var iid = IidImageList;
            if (Native.SHGetImageList(kind, ref iid, out var list) != 0 || list == null) continue;
            IntPtr hicon = IntPtr.Zero;
            if (list.GetIcon(info.iIcon, 1, ref hicon) != 0 || hicon == IntPtr.Zero) continue;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var result = CropToContent(bmp);
                result.Freeze();
                return result;
            }
            finally { Native.DestroyIcon(hicon); }
        }
        return null;
    }

    // Les vieilles icônes 48px sont centrées dans un canvas 256px : on recadre pour qu'elles ne paraissent pas minuscules.
    private static BitmapSource CropToContent(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] > 16)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return src;

        int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        if (side >= Math.Min(w, h) * 0.8) return src;

        int x0 = Math.Clamp((minX + maxX + 1) / 2 - side / 2, 0, w - side);
        int y0 = Math.Clamp((minY + maxY + 1) / 2 - side / 2, 0, h - side);
        return new CroppedBitmap(src, new Int32Rect(x0, y0, side, side));
    }
}
