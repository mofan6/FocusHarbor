using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FocusHarbor
{
    internal static class PackagedAppService
    {
        internal static List<InstalledSoftwareEntry> Discover()
        {
            List<InstalledSoftwareEntry> result = new List<InstalledSoftwareEntry>();
            object shellObject = null;
            object folderObject = null;
            object itemsObject = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result;
                shellObject = Activator.CreateInstance(shellType);
                dynamic shell = shellObject;
                folderObject = shell.NameSpace("shell:AppsFolder");
                if (folderObject == null) return result;
                dynamic folder = folderObject;
                itemsObject = folder.Items();
                dynamic items = itemsObject;
                int count = items.Count;
                for (int index = 0; index < count; index++)
                {
                    object itemObject = null;
                    try
                    {
                        itemObject = items.Item(index);
                        dynamic item = itemObject;
                        string id = Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID"));
                        string family = Convert.ToString(item.ExtendedProperty("System.AppUserModel.PackageFamilyName"));
                        InstalledSoftwareEntry entry = CreateEntry(Convert.ToString(item.Name), id, family);
                        if (entry != null) result.Add(entry);
                    }
                    catch { /* Skip one stale Shell entry without losing other apps. */ }
                    finally { Release(itemObject); }
                }
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Microsoft Store app discovery failed: " + exception.Message);
            }
            finally
            {
                Release(itemsObject);
                Release(folderObject);
                Release(shellObject);
            }
            return result.GroupBy(entry => entry.AppUserModelId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        }

        internal static InstalledSoftwareEntry CreateEntry(string displayName, string id, string family)
        {
            id = (id ?? String.Empty).Trim();
            family = (family ?? String.Empty).Trim();
            int separator = id.IndexOf('!');
            if (separator <= 0 || separator == id.Length - 1 || id.IndexOf('!', separator + 1) >= 0) return null;
            string derivedFamily = id.Substring(0, separator);
            if (family.Length == 0) family = derivedFamily;
            if (!String.Equals(family, derivedFamily, StringComparison.OrdinalIgnoreCase) ||
                family.IndexOf('_') <= 0 || !family.All(IsIdentityCharacter) || !id.Substring(separator + 1).All(IsIdentityCharacter)) return null;
            return new InstalledSoftwareEntry
            {
                DisplayName = String.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim(),
                ExecutablePath = String.Empty,
                AppUserModelId = id,
                PackageFamilyName = family
            };
        }

        private static bool IsIdentityCharacter(char value)
        {
            return Char.IsLetterOrDigit(value) || value == '.' || value == '-' || value == '_';
        }

        internal static AppRuleConfig CreateRule(InstalledSoftwareEntry entry)
        {
            if (entry == null || CreateEntry(entry.DisplayName, entry.AppUserModelId, entry.PackageFamilyName) == null)
                throw new InvalidOperationException("商店应用标识无效，请刷新软件列表后重试。");
            return new AppRuleConfig
            {
                Id = "custom-store-" + Guid.NewGuid().ToString("N"),
                DisplayName = entry.DisplayName,
                RootPath = String.Empty,
                TargetKind = "PackagedApplication",
                TargetPath = entry.AppUserModelId,
                PackageFamilyName = entry.PackageFamilyName,
                Mode = "AlwaysBlocked",
                Enabled = true,
                IsCustom = true,
                ProcessNames = new List<string>(),
                AllowedWindows = new List<TimeWindowConfig>()
            };
        }

        private static void Release(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
            catch { }
        }
    }
}
