using FocusHarbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Threading;

internal static class FileGuardHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception exception)
        {
            Console.WriteLine("FILE_GUARD_AUDIT_FAILED=" + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "child") { Thread.Sleep(30000); return 0; }
        if (args.Length == 2 && args[0] == "recover-audit")
        {
            string audit = Path.GetFullPath(args[1]);
            if (Path.GetDirectoryName(audit) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(audit).StartsWith("FocusHarbor-fileguard-audit-", StringComparison.Ordinal)) return 64;
            RedirectState(Path.Combine(audit, "state"));
            var recovery = FileAccessGuardService.SynchronizePaths(new string[0]);
            foreach (var error in recovery) Console.WriteLine(error.Key + "=" + error.Value);
            Console.WriteLine("AUDIT_RECOVERY_ERRORS=" + recovery.Count);
            return recovery.Count == 0 ? 0 : 1;
        }
        string root = Path.Combine(Path.GetTempPath(), "FocusHarbor-fileguard-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RedirectState(Path.Combine(root, "state"));
        Console.WriteLine("ISOLATED_ROOT=" + root);
        Dictionary<string, FileStream> locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string document = Path.Combine(root, "sample.txt");
            File.WriteAllText(document, "unchanged study data", Encoding.UTF8);
            string originalDacl = File.GetAccessControl(document, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            using (FileStream alreadyOpen = new FileStream(document, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Dictionary<string, string> errors = FileAccessGuardService.SynchronizePaths(new[] { document });
                Assert(errors.Count == 0, "ACL apply failed: " + String.Join(";", errors.Values));
                AssertCannotRead(document);
                Assert(alreadyOpen.ReadByte() >= 0, "pre-existing document handle should not be destroyed");
                FileAccessGuardService.SynchronizePaths(new[] { document });
                Assert(JsonStore.Load<FileAccessJournal>(JsonStore.FileAccessPath).Records.Count == 1, "duplicate permission recovery records");
                // Simulate a crash/restart and a damaged primary journal.
                File.WriteAllText(JsonStore.FileAccessPath, "broken");
                errors = FileAccessGuardService.SynchronizePaths(new string[0]);
                Assert(errors.Count == 0, "backup recovery failed: " + String.Join(";", errors.Values));
                Assert(File.ReadAllText(document, Encoding.UTF8) == "unchanged study data", "document not restored intact");
            }
            Assert(File.GetAccessControl(document, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == originalDacl, "original permissions not restored");
            Console.WriteLine("OPEN_DOCUMENT_NEW_READ_BLOCKED_AND_BACKUP_RESTORE_OK");

            FocusConfig fileConfig = DefaultConfigFactory.Create();
            fileConfig.AppGuardEnabled = true;
            AppRuleConfig documentRule = MainWindowController.CreateCustomPathRule(document);
            fileConfig.AppRules.Add(documentRule);
            Assert(FileAccessGuardService.Synchronize(fileConfig, false).Count == 0, "file rule did not enforce");
            AssertCannotRead(document);
            Assert(FileAccessGuardService.Synchronize(fileConfig, true).Count == 0, "pause did not restore access");
            Assert(File.ReadAllText(document, Encoding.UTF8) == "unchanged study data", "pause changed data");
            documentRule.Mode = "Scheduled";
            documentRule.AllowedWindows.Add(new TimeWindowConfig { StartMinute = 0, EndMinute = 1440 });
            Assert(FileAccessGuardService.Synchronize(fileConfig, false).Count == 0, "allowed window failed");
            File.ReadAllText(document);
            documentRule.AllowedWindows.Clear();
            Assert(FileAccessGuardService.Synchronize(fileConfig, false).Count == 0, "blocked window failed");
            AssertCannotRead(document);
            fileConfig.AppGuardEnabled = false;
            Assert(FileAccessGuardService.Synchronize(fileConfig, false).Count == 0, "master-off did not restore access");
            File.ReadAllText(document);
            Console.WriteLine("PAUSE_ALLOWED_TIME_AND_MASTER_OFF_RESTORE_OK");

            Assert(FileAccessGuardService.SynchronizePaths(new[] { document }).Count == 0, "second protection failed");
            string renamed = Path.Combine(root, "renamed.txt");
            File.Move(document, renamed);
            File.WriteAllText(document, "replacement must not be changed");
            Dictionary<string, string> movedErrors = FileAccessGuardService.SynchronizePaths(new string[0]);
            Assert(movedErrors.Count == 0, "file-ID recovery failed: " + String.Join(";", movedErrors.Values));
            Assert(File.ReadAllText(renamed, Encoding.UTF8) == "unchanged study data", "renamed file remains restricted");
            Assert(File.ReadAllText(document) == "replacement must not be changed", "replacement path was touched");
            Console.WriteLine("RENAMED_FILE_ID_RECOVERY_AND_REPLACEMENT_SAFETY_OK");

            Assert(FileAccessGuardService.SynchronizePaths(new[] { renamed }).Count == 0, "deleted-file setup failed");
            File.Delete(renamed);
            Dictionary<string, string> deletionErrors = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                deletionErrors = FileAccessGuardService.SynchronizePaths(new string[0]);
                if (deletionErrors.Count == 0) break;
                Thread.Sleep(100);
            }
            Assert(deletionErrors.Count == 0, "a deleted target trapped recovery forever: " + String.Join(";", deletionErrors.Values));
            Assert(JsonStore.Load<FileAccessJournal>(JsonStore.FileAccessPath).Records.Count == 0, "deleted target record was retained");
            Console.WriteLine("DELETED_FILE_RECOVERY_DOES_NOT_TRAP_PAUSE_OK");

            Assert(FileAccessGuardService.SynchronizePaths(new[] { document }).Count == 0, "permission-merge setup failed");
            FileSecurity changed = File.GetAccessControl(document, AccessControlSections.Access);
            var readers = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
            changed.AddAccessRule(new FileSystemAccessRule(readers, FileSystemRights.ReadAttributes, AccessControlType.Allow));
            File.SetAccessControl(document, changed);
            Assert(FileAccessGuardService.SynchronizePaths(new string[0]).Count == 0, "unrelated permission change prevented restore");
            File.ReadAllText(document);
            Assert(File.GetAccessControl(document, AccessControlSections.Access).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(entry => entry.IdentityReference.Value == readers.Value && entry.AccessControlType == AccessControlType.Allow), "external permission change was overwritten");
            Console.WriteLine("UNRELATED_PERMISSION_CHANGE_PRESERVED_OK");
            string alias = Path.Combine(root, "hard-link.txt");
            Assert(CreateHardLink(alias, document, IntPtr.Zero), "hard-link fixture creation failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Assert(FileAccessGuardService.SynchronizePaths(new[] { document, alias }).Count == 0, "hard-link protection failed");
            Assert(JsonStore.Load<FileAccessJournal>(JsonStore.FileAccessPath).Records.Count == 1, "hard-link aliases created conflicting ACL backups");
            Assert(FileAccessGuardService.SynchronizePaths(new[] { alias }).Count == 0, "removing one alias broke remaining rule");
            AssertCannotRead(alias);
            Assert(FileAccessGuardService.SynchronizePaths(new string[0]).Count == 0, "hard-link recovery failed");
            File.ReadAllText(document);
            Console.WriteLine("HARD_LINK_ALIAS_RECOVERY_OK");

            string executable = Path.Combine(root, "target.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, executable);
            AppRuleConfig rule = MainWindowController.CreateCustomPathRule(executable);
            Assert(rule.TargetKind == "Executable", "EXE misclassified");
            Assert(GuardService.IsCompatibleGuardExecutable(executable, Assembly.GetExecutingAssembly().Location), "another copy of the same app was not recognized");
            Assert(!GuardService.IsCompatibleGuardExecutable(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable), "an unrelated program was trusted as the guard");
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            object shortcut = ((dynamic)shell).CreateShortcut(Path.Combine(root, "target.lnk"));
            try
            {
                ((dynamic)shortcut).TargetPath = executable;
                ((dynamic)shortcut).Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            AppRuleConfig linkRule = MainWindowController.CreateCustomPathRule(Path.Combine(root, "target.lnk"));
            Assert(linkRule.TargetKind == "Executable" && linkRule.TargetPath == executable, "shortcut resolved to link instead of target EXE");
            Console.WriteLine("SHORTCUT_TARGET_CLASSIFICATION_OK");
            Dictionary<string, string> report = new Dictionary<string, string>();
            GuardService.SynchronizeFileLocks(new[] { rule }, locks, new Dictionary<string, string>(), report);
            Assert(locks.Count == 1, "EXE image lock not acquired");
            bool denied = false;
            try
            {
                using (Process unexpected = Process.Start(new ProcessStartInfo(executable, "child") { UseShellExecute = false, CreateNoWindow = true }))
                { unexpected.Kill(); unexpected.WaitForExit(); }
            }
            catch (System.ComponentModel.Win32Exception) { denied = true; }
            Assert(denied, "exclusive image lock did not deny CreateProcess");
            foreach (FileStream stream in locks.Values) stream.Dispose();
            locks.Clear();
            using (Process target = Process.Start(new ProcessStartInfo(executable, "child") { UseShellExecute = false, CreateNoWindow = true }))
            {
                Thread.Sleep(200);
                Assert(!target.HasExited, "fixture did not start after unlock");
                FocusConfig config = DefaultConfigFactory.Create();
                config.AppGuardEnabled = true;
                config.AppRules.Add(rule);
                typeof(GuardService).GetMethod("Enforce", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { config, locks });
                Assert(target.WaitForExit(4000), "already-running target was not terminated by exact-path rule");
            }
            foreach (FileStream stream in locks.Values) stream.Dispose();
            locks.Clear();
            Console.WriteLine("EXE_CREATEPROCESS_DENIED_REOPEN_AND_RUNNING_PROCESS_BLOCK_OK");

            Assert(JsonStore.IsExecutableTargetPath("tool.COM") && JsonStore.IsExecutableTargetPath("tool.scr"), "executable variants misclassified");
            Assert(MainWindowController.CreateCustomPathRule(document).TargetKind == "File", "document misclassified");
            Assert(FileAccessGuardService.SynchronizePaths(new[] { JsonStore.ConfigPath }).Count == 1, "state path was not rejected");
            Console.WriteLine("FILE_GUARD_AUDIT_OK");
            return 0;
        }
        finally
        {
            foreach (FileStream stream in locks.Values) stream.Dispose();
            Dictionary<string, string> errors = FileAccessGuardService.SynchronizePaths(new string[0]);
            if (errors.Count != 0)
            {
                Console.WriteLine("FIXTURE_RECOVERY_PENDING=" + String.Join(";", errors.Values));
                throw new IOException("Fixture permissions were not restored.");
            }
        }
    }

    private static void RedirectState(string root)
    {
        string[] roots = { JsonStore.StateDirectory, JsonStore.PauseStateDirectory, JsonStore.GuardStateDirectory };
        foreach (FieldInfo field in typeof(JsonStore).GetFields(BindingFlags.Public | BindingFlags.Static).Where(item => item.FieldType == typeof(string)))
        {
            string value = (string)field.GetValue(null);
            int index = Array.FindIndex(roots, prefix => value == prefix || value.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert(index >= 0, "unknown state path");
            field.SetValue(null, Path.Combine(root, "state" + index) + value.Substring(roots[index].Length));
        }
        JsonStore.EnsureDirectories();
    }
    private static void AssertCannotRead(string path)
    {
        bool denied = false;
        try { File.ReadAllText(path); }
        catch (UnauthorizedAccessException) { denied = true; }
        Assert(denied, "protected file was still readable");
    }
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string alias, string existing, IntPtr security);
}
