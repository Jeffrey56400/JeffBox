using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TodoApp.Services;

/// <summary>从 exe/lnk/文件/文件夹提取 Shell 图标</summary>
public static class IconExtract
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>取 32x32 图标；folder=true 时 path 为目录。文件不存在时回退为按扩展名的通用图标。</summary>
    public static BitmapSource? Get(string path, bool folder = false)
    {
        try
        {
            var info = new SHFILEINFO();
            var attrs = folder ? FILE_ATTRIBUTE_DIRECTORY : 0u;
            var flags = SHGFI_ICON | SHGFI_LARGEICON | (folder ? SHGFI_USEFILEATTRIBUTES : 0u);
            var res = SHGetFileInfo(path, attrs, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if ((res == IntPtr.Zero || info.hIcon == IntPtr.Zero) && !folder)
            {
                // 程序被移动/卸载：用扩展名对应的通用图标，避免磁贴空白
                info = new SHFILEINFO();
                res = SHGetFileInfo(path, 0u, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            }
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze(); // 跨线程/缓存安全
                return bs;
            }
            finally
            {
                DestroyIcon(info.hIcon); // 任何路径都不泄漏 GDI 句柄
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>图标缓存：同一图标文件路径只提取一次</summary>
    private static readonly Dictionary<string, BitmapSource?> Cache = new();

    public static BitmapSource? GetCached(string path, bool folder = false)
    {
        var key = (folder ? "dir:" : "") + path.ToLowerInvariant();
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var icon = Get(path, folder);
        if (icon != null) Cache[key] = icon; // 失败不缓存：文件出现后仍可重试
        return icon;
    }
}
