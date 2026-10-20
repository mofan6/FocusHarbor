using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FocusHarbor
{
    internal static class GuardedPathResolver
    {
        internal static string Resolve(string path)
        {
            string full = Path.GetFullPath(path);
            for (int depth = 0; String.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase); depth++)
            {
                if (depth >= 5) throw new IOException("快捷方式存在循环，请直接选择目标文件。");
                object shell = null;
                object shortcut = null;
                try
                {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                    shortcut = ((dynamic)shell).CreateShortcut(full);
                    string target = (string)((dynamic)shortcut).TargetPath;
                    if (String.IsNullOrWhiteSpace(target)) throw new IOException("快捷方式没有普通文件目标，请直接选择应用的 EXE。");
                    full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target));
                    if (!File.Exists(full)) throw new IOException("快捷方式目标不存在，请重新选择文件。");
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
            }
            return full;
        }
    }
}
