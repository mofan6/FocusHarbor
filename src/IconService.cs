using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusHarbor
{
    public static class IconService
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource GetFileIcon(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }

            lock (SyncRoot)
            {
                ImageSource cached;
                if (Cache.TryGetValue(fullPath, out cached))
                {
                    return cached;
                }
            }

            ImageSource source = null;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(fullPath))
                {
                    if (icon != null)
                    {
                        BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        bitmap.Freeze();
                        source = bitmap;
                    }
                }
            }
            catch
            {
            }

            lock (SyncRoot)
            {
                Cache[fullPath] = source;
            }
            return source;
        }

        public static ImageSource GetRuleIcon(AppRuleConfig rule)
        {
            if (rule == null)
            {
                return null;
            }
            if (String.Equals(rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase))
                return GetPackagedAppIcon(rule.TargetPath);
            ImageSource direct = GetFileIcon(rule.TargetPath);
            if (direct != null)
            {
                return direct;
            }
            if (String.IsNullOrWhiteSpace(rule.RootPath) || !Directory.Exists(rule.RootPath))
            {
                return null;
            }
            try
            {
                HashSet<string> names = new HashSet<string>(rule.ProcessNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                string candidate = Directory.EnumerateFiles(rule.RootPath, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => names.Count == 0 || names.Contains(Path.GetFileNameWithoutExtension(path)));
                return GetFileIcon(candidate);
            }
            catch
            {
                return null;
            }
        }

        public static ImageSource GetPackagedAppIcon(string appUserModelId)
        {
            if (String.IsNullOrWhiteSpace(appUserModelId)) return null;
            string key = "store:" + appUserModelId;
            lock (SyncRoot)
            {
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }
            IShellItemImageFactory factory = null;
            IntPtr bitmapHandle = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                int result = SHCreateItemFromParsingName("shell:AppsFolder\\" + appUserModelId, IntPtr.Zero, ref iid, out factory);
                if (result < 0 || factory == null) return null;
                // UI calls may only use the Shell cache. Full extraction is done
                // by the software-discovery worker before binding the app list.
                bool uiThread = Application.Current != null && Application.Current.Dispatcher.CheckAccess();
                uint flags = uiThread ? 0x14u : 0x4u;
                if (factory.GetImage(new IconSize { Width = 32, Height = 32 }, flags, out bitmapHandle) < 0 || bitmapHandle == IntPtr.Zero) return null;
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                lock (SyncRoot) { Cache[key] = bitmap; }
                return bitmap;
            }
            catch { return null; }
            finally
            {
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconSize { public int Width; public int Height; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(IconSize size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string name, IntPtr bindingContext, ref Guid iid, out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr value);
    }
}
