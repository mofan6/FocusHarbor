using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FocusHarbor
{
    // Only ordinary-file rules use ACLs. EXEs additionally use an exclusive
    // image-file handle and the existing exact-path process scanner.
    public static class FileAccessGuardService
    {
        private const int DeniedAccess = (int)(FileSystemRights.ReadData | FileSystemRights.ExecuteFile);

        public static Dictionary<string, string> Synchronize(FocusConfig config, bool paused)
        {
            List<AppRuleConfig> rules = config != null && config.AppGuardEnabled && !paused && config.AppRules != null
                ? config.AppRules.Where(rule => rule != null && rule.Enabled && GuardService.ShouldBlockNow(rule) &&
                    String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<AppRuleConfig>();
            return SynchronizePaths(rules.Select(rule => rule.TargetPath));
        }

        internal static Dictionary<string, string> SynchronizePaths(IEnumerable<string> paths)
        {
            Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                try { desired.Add(ValidatePath(path)); }
                catch (Exception exception) { errors[path ?? String.Empty] = exception.Message; }
            }
            if (desired.Count == 0 && !File.Exists(JsonStore.FileAccessPath) && !File.Exists(JsonStore.FileAccessBackupPath)) return errors;
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborFileAccess"))
            {
                bool taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(5));
                if (!taken) throw new IOException("文件访问权限正在更新，请稍后重试。");
                try
                {
                    FileAccessJournal journal = JsonStore.LoadRedundant<FileAccessJournal>(JsonStore.FileAccessPath, JsonStore.FileAccessBackupPath);
                    if (journal == null)
                    {
                        if (File.Exists(JsonStore.FileAccessPath) || File.Exists(JsonStore.FileAccessBackupPath))
                            throw new IOException("文件权限恢复记录损坏，已停止添加新的权限限制。");
                        journal = new FileAccessJournal { Records = new List<FileAccessRecord>() };
                    }
                    if (journal.Records == null) throw new IOException("文件权限恢复记录无效。");
                    foreach (FileAccessRecord record in journal.Records.ToList())
                    {
                        if (desired.Contains(record.Path)) continue;
                        try
                        {
                            Restore(record);
                            journal.Records.Remove(record);
                            Save(journal);
                        }
                        catch (Exception exception) { errors[record.Path] = "恢复文件权限失败：" + exception.Message; }
                    }
                    foreach (string path in desired)
                    {
                        try
                        {
                            FileAccessRecord record = journal.Records.FirstOrDefault(item => String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                            string identity = FileIdentity(path);
                            if (record == null && journal.Records.Any(item => item.Identity == identity)) continue; // Hard-link alias: one ACL, one recovery record.
                            if (record != null && record.Identity != identity)
                            {
                                // A replacement at the same path is not the old file.
                                Restore(record);
                                journal.Records.Remove(record);
                                Save(journal);
                                record = null;
                            }
                            FileSecurity security = File.GetAccessControl(path, AccessControlSections.Access);
                            string current = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                            if (record != null && current == record.AppliedDacl) continue;
                            if (record != null)
                            {
                                Restore(record);
                                journal.Records.Remove(record);
                                Save(journal);
                                security = File.GetAccessControl(path, AccessControlSections.Access);
                                current = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                            }
                            string sid = WindowsIdentity.GetCurrent().User.Value;
                            RawSecurityDescriptor descriptor = new RawSecurityDescriptor(current);
                            if (descriptor.DiscretionaryAcl == null) throw new IOException("该文件没有可安全修改的访问控制表。");
                            // Verify recovery by stable file ID *before* changing ACLs.
                            string recoveredPath = ResolveIdentity(path, identity, true);
                            if (String.IsNullOrWhiteSpace(recoveredPath)) throw new IOException("无法验证该文件的恢复路径。");
                            descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied,
                                DeniedAccess, new SecurityIdentifier(sid), false, null));
                            record = new FileAccessRecord { Path = path, Identity = identity, UserSid = sid,
                                OriginalDacl = current, AppliedDacl = descriptor.GetSddlForm(AccessControlSections.Access) };
                            journal.Records.Add(record);
                            Save(journal); // Write-ahead recovery record, including redundant copy.
                            SetDacl(path, record.AppliedDacl);
                            JsonStore.AppendLog("DENY FILE ACCESS " + path);
                        }
                        catch (Exception exception) { errors[path] = "文件权限限制未生效：" + exception.Message; }
                    }
                }
                finally { if (taken) mutex.ReleaseMutex(); }
            }
            return errors;
        }

        private static void Save(FileAccessJournal journal)
        {
            JsonStore.SaveRedundant(JsonStore.FileAccessPath, JsonStore.FileAccessBackupPath, journal);
        }

        private static void Restore(FileAccessRecord record)
        {
            string path = ResolveIdentity(record.Path, record.Identity);
            if (path == null) return; // Original file ID no longer exists on the same, online volume.
            string current = File.GetAccessControl(path, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            if (current == record.OriginalDacl) return; // Journal written, ACL application interrupted.
            if (current == record.AppliedDacl) SetDacl(path, record.OriginalDacl);
            else
            {
                // Preserve unrelated permission changes; remove only our exact ACE.
                RawSecurityDescriptor descriptor = new RawSecurityDescriptor(current);
                bool found = false;
                if (descriptor.DiscretionaryAcl != null)
                    for (int index = 0; index < descriptor.DiscretionaryAcl.Count; index++)
                    {
                        CommonAce ace = descriptor.DiscretionaryAcl[index] as CommonAce;
                        if (ace != null && ace.AceFlags == AceFlags.None && ace.AceQualifier == AceQualifier.AccessDenied &&
                            ace.AccessMask == DeniedAccess && ace.SecurityIdentifier.Value == record.UserSid)
                        { descriptor.DiscretionaryAcl.RemoveAce(index); found = true; break; }
                    }
                if (found) SetDacl(path, descriptor.GetSddlForm(AccessControlSections.Access));
                else if (current != record.OriginalDacl)
                    throw new IOException("访问控制表已被外部修改，保留恢复记录以免覆盖其他权限。");
            }
            JsonStore.AppendLog("RESTORE FILE ACCESS " + path);
        }

        private static void SetDacl(string path, string sddl)
        {
            FileSecurity security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
            File.SetAccessControl(path, security);
        }

        private static string ValidatePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new IOException("请选择本机文件的完整路径。");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0) throw new IOException("不对网络路径或备用数据流修改权限。");
            FileInfo file = new FileInfo(full);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("目标不存在或不是普通文件。");
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), JsonStore.StateDirectory,
                JsonStore.PauseStateDirectory, JsonStore.GuardStateDirectory })
                if (!String.IsNullOrWhiteSpace(root) && full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("系统与 Focus Harbor 状态文件不允许修改权限。");
            for (DirectoryInfo parent = file.Directory; parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不对链接目录中的文件修改权限。");
            return full;
        }

        private static string FileIdentity(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                FileInformation info;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return info.Volume.ToString("X8") + ":" + (((ulong)info.IndexHigh << 32) | info.IndexLow).ToString("X16");
            }
        }

        private static string ResolveIdentity(string originalPath, string identity, bool forceById = false)
        {
            if (!forceById && File.Exists(originalPath) && FileIdentity(originalPath) == identity) return Path.GetFullPath(originalPath);
            string[] pieces = identity.Split(':');
            if (pieces.Length != 2) throw new IOException("文件身份记录无效。");
            using (SafeFileHandle hint = CreateFile(Path.GetPathRoot(originalPath), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (hint.IsInvalid || !FileIdentity(Path.GetPathRoot(originalPath)).StartsWith(pieces[0] + ":", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("原磁盘尚未就绪或盘符已指向其他磁盘，恢复记录已保留。");
                FileIdDescriptor id = new FileIdDescriptor { Size = 24, Type = 0, Id = UInt64.Parse(pieces[1], System.Globalization.NumberStyles.HexNumber) };
                using (SafeFileHandle handle = OpenFileById(hint, ref id, 0, 7, IntPtr.Zero, 0))
                {
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 2) return null;
                        if (error == 87 && !forceById)
                        {
                            // NTFS can report an invalid/stale file ID after
                            // deletion. Verify the same API still works on the
                            // original volume before retiring a recovery record.
                            FileIdDescriptor rootId = new FileIdDescriptor { Size = 24, Type = 0,
                                Id = UInt64.Parse(FileIdentity(Path.GetPathRoot(originalPath)).Split(':')[1], System.Globalization.NumberStyles.HexNumber) };
                            using (SafeFileHandle probe = OpenFileById(hint, ref rootId, 0, 7, IntPtr.Zero, 0x02000000))
                                if (!probe.IsInvalid) return null;
                        }
                        throw new Win32Exception(error, "无法定位原文件，恢复记录已保留（Windows 错误 " + error + "）。");
                    }
                    StringBuilder name = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(handle, name, name.Capacity, 0);
                    if (length == 0 || length >= name.Capacity) throw new IOException("无法读取原文件的恢复路径。");
                    string resolved = name.ToString();
                    if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
                    if (FileIdentity(resolved) != identity) throw new IOException("恢复目标身份不匹配。");
                    return resolved;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct FileIdDescriptor
        {
            [FieldOffset(0)] public uint Size;
            [FieldOffset(4)] public int Type;
            [FieldOffset(8)] public ulong Id;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FileIdDescriptor id, uint access, uint share, IntPtr security, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int capacity, uint flags);
    }

    public sealed class FileAccessJournal { public List<FileAccessRecord> Records { get; set; } }
    public sealed class FileAccessRecord
    {
        public string Path { get; set; }
        public string Identity { get; set; }
        public string UserSid { get; set; }
        public string OriginalDacl { get; set; }
        public string AppliedDacl { get; set; }
    }
}
