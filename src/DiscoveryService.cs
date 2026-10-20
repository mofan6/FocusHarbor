using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;

namespace FocusHarbor
{
    public static class DiscoveryService
    {
        private static readonly HashSet<string> BrowserExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "msedge.exe", "vivaldi.exe", "quark.exe", "brave.exe", "firefox.exe",
            "opera.exe", "opera_gx.exe", "browser.exe", "arc.exe", "zen.exe", "waterfox.exe",
            "librewolf.exe", "floorp.exe", "chromium.exe", "thorium.exe", "sidekick.exe",
            "maxthon.exe", "360se.exe", "360chrome.exe", "qqbrowser.exe", "sogouexplorer.exe",
            "ucbrowser.exe", "wavebrowser.exe"
        };

        public static List<InstalledSoftwareEntry> DiscoverInstalledSoftware()
        {
            Dictionary<string, InstalledSoftwareEntry> results = new Dictionary<string, InstalledSoftwareEntry>(StringComparer.OrdinalIgnoreCase);
            ReadUninstallRegistry(RegistryHive.CurrentUser, RegistryView.Default, results);
            ReadUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, results);
            ReadUninstallRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, results);
            ReadStartMenuBrowsers(results);
            foreach (InstalledSoftwareEntry app in PackagedAppService.Discover())
                results["store:" + app.AppUserModelId] = app;
            return results.Values
                .Where(item => item.IsPackaged || File.Exists(item.ExecutablePath))
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static List<BrowserTargetConfig> DiscoverBrowsers()
        {
            Dictionary<string, InstalledSoftwareEntry> candidates = new Dictionary<string, InstalledSoftwareEntry>(StringComparer.OrdinalIgnoreCase);
            ReadStartMenuBrowsers(candidates);
            foreach (InstalledSoftwareEntry software in DiscoverInstalledSoftware())
            {
                if (!software.IsPackaged && LooksLikeBrowser(software.DisplayName, software.ExecutablePath))
                {
                    candidates[software.ExecutablePath] = software;
                }
            }
            ScanSoftwareDirectoriesForBrowsers(candidates);

            List<BrowserTargetConfig> browsers = new List<BrowserTargetConfig>();
            foreach (InstalledSoftwareEntry candidate in candidates.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!File.Exists(candidate.ExecutablePath))
                {
                    continue;
                }
                BrowserTargetConfig browser = CreateBrowserTarget(candidate.DisplayName, candidate.ExecutablePath);
                if (browser != null && !browsers.Any(item => String.Equals(item.ExecutablePath, browser.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                {
                    browsers.Add(browser);
                }
            }
            return browsers
                .GroupBy(
                    browser => String.Equals(browser.PolicyKind, "BlockProcess", StringComparison.OrdinalIgnoreCase)
                        ? "path:" + browser.ExecutablePath
                        : "policy:" + browser.PolicyKind + ":" + browser.PolicyRoot,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(browser => browser.ExecutablePath.Length).First())
                .OrderBy(browser => browser.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static bool RefreshBrowserTargets(FocusConfig config, bool force, out int discoveredCount)
        {
            discoveredCount = config.BrowserTargets == null ? 0 : config.BrowserTargets.Count;
            if (!force && !BrowserScanIsDue())
            {
                return false;
            }

            List<BrowserTargetConfig> discovered = DiscoverBrowsers();
            discoveredCount = discovered.Count;
            bool changed = MergeBrowserTargets(config, discovered);
            if (!config.BrowserDiscoveryCompleted)
            {
                config.BrowserDiscoveryCompleted = true;
                changed = true;
            }
            JsonStore.AtomicWrite(JsonStore.BrowserScanStampPath, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), Encoding.ASCII);
            return changed;
        }

        public static bool MergeBrowserTargets(FocusConfig config, IEnumerable<BrowserTargetConfig> browsers)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            if (config.BrowserTargets == null)
            {
                config.BrowserTargets = new List<BrowserTargetConfig>();
            }

            bool changed = false;
            HashSet<string> existing = new HashSet<string>(config.BrowserTargets.Select(BrowserIdentity), StringComparer.OrdinalIgnoreCase);
            foreach (BrowserTargetConfig browser in browsers ?? Enumerable.Empty<BrowserTargetConfig>())
            {
                string identity = BrowserIdentity(browser);
                if (browser != null && identity.Length > 0 && existing.Add(identity))
                {
                    config.BrowserTargets.Add(browser);
                    changed = true;
                }
            }
            return changed;
        }

        private static bool BrowserScanIsDue()
        {
            try
            {
                if (File.Exists(JsonStore.BrowserScanStampPath))
                {
                    DateTimeOffset stamp;
                    if (DateTimeOffset.TryParse(File.ReadAllText(JsonStore.BrowserScanStampPath, Encoding.ASCII).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp))
                    {
                        return DateTimeOffset.Now - stamp >= TimeSpan.FromHours(6);
                    }
                }
            }
            catch
            {
            }
            return true;
        }

        private static string BrowserIdentity(BrowserTargetConfig browser)
        {
            if (browser == null)
            {
                return String.Empty;
            }
            return String.Equals(browser.PolicyKind, "BlockProcess", StringComparison.OrdinalIgnoreCase)
                ? "path:" + browser.ExecutablePath
                : "policy:" + browser.PolicyKind + ":" + browser.PolicyRoot;
        }

        private static void ReadUninstallRegistry(RegistryHive hive, RegistryView view, IDictionary<string, InstalledSoftwareEntry> results)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                {
                    if (uninstall == null)
                    {
                        return;
                    }
                    foreach (string childName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey child = uninstall.OpenSubKey(childName, false))
                            {
                                if (child == null || Convert.ToInt32(child.GetValue("SystemComponent", 0)) == 1)
                                {
                                    continue;
                                }
                                string displayName = Convert.ToString(child.GetValue("DisplayName", String.Empty)).Trim();
                                if (displayName.Length == 0)
                                {
                                    continue;
                                }
                                string path = ResolveExecutable(
                                    Convert.ToString(child.GetValue("DisplayIcon", String.Empty)),
                                    Convert.ToString(child.GetValue("InstallLocation", String.Empty)),
                                    displayName);
                                AddCandidate(results, displayName, path);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ReadStartMenuBrowsers(IDictionary<string, InstalledSoftwareEntry> results)
        {
            ReadStartMenuBrowsers(RegistryHive.CurrentUser, RegistryView.Default, results);
            ReadStartMenuBrowsers(RegistryHive.LocalMachine, RegistryView.Registry64, results);
            ReadStartMenuBrowsers(RegistryHive.LocalMachine, RegistryView.Registry32, results);
        }

        private static void ReadStartMenuBrowsers(RegistryHive hive, RegistryView view, IDictionary<string, InstalledSoftwareEntry> results)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey clients = baseKey.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet", false))
                {
                    if (clients == null)
                    {
                        return;
                    }
                    foreach (string childName in clients.GetSubKeyNames())
                    {
                        using (RegistryKey child = clients.OpenSubKey(childName, false))
                        using (RegistryKey commandKey = child == null ? null : child.OpenSubKey(@"shell\open\command", false))
                        {
                            string name = child == null ? childName : Convert.ToString(child.GetValue(null, childName));
                            string command = commandKey == null ? String.Empty : Convert.ToString(commandKey.GetValue(null, String.Empty));
                            AddCandidate(results, name, ExtractExecutableFromCommand(command));
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string ResolveExecutable(string displayIcon, string installLocation, string displayName)
        {
            string iconPath = ExtractExecutableFromCommand(displayIcon);
            if (IsUsableExecutable(iconPath) && !IsHelperExecutable(Path.GetFileNameWithoutExtension(iconPath)))
            {
                return Path.GetFullPath(iconPath);
            }

            string directory = ExpandAndTrim(installLocation);
            if (!Directory.Exists(directory))
            {
                return String.Empty;
            }
            try
            {
                string[] executables = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
                string[] words = displayName.Split(new[] { ' ', '-', '_', '（', '(', '[' }, StringSplitOptions.RemoveEmptyEntries);
                string match = executables.FirstOrDefault(path => words.Any(word => word.Length >= 3 && Path.GetFileNameWithoutExtension(path).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));
                if (match == null)
                {
                    match = executables.FirstOrDefault(path => !IsHelperExecutable(Path.GetFileNameWithoutExtension(path)));
                }
                return match == null ? String.Empty : Path.GetFullPath(match);
            }
            catch
            {
                return String.Empty;
            }
        }

        private static void ScanSoftwareDirectoriesForBrowsers(IDictionary<string, InstalledSoftwareEntry> results)
        {
            List<string> roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Queue<DirectoryDepth> pending = new Queue<DirectoryDepth>();
                pending.Enqueue(new DirectoryDepth(root, 0));
                int inspected = 0;
                while (pending.Count > 0 && inspected < 6500)
                {
                    DirectoryDepth current = pending.Dequeue();
                    if (!visited.Add(current.Path))
                    {
                        continue;
                    }
                    inspected++;
                    try
                    {
                        foreach (string file in Directory.GetFiles(current.Path, "*.exe", SearchOption.TopDirectoryOnly))
                        {
                            string fileName = Path.GetFileName(file);
                            if (BrowserExecutableNames.Contains(fileName))
                            {
                                string displayName = ReadProductName(file);
                                AddCandidate(results, displayName.Length == 0 ? Path.GetFileNameWithoutExtension(file) : displayName, file);
                            }
                        }
                        if (current.Depth >= 4)
                        {
                            continue;
                        }
                        foreach (string child in Directory.GetDirectories(current.Path))
                        {
                            if (!ShouldSkipDirectory(child))
                            {
                                pending.Enqueue(new DirectoryDepth(child, current.Depth + 1));
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static BrowserTargetConfig CreateBrowserTarget(string displayName, string executablePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                return null;
            }
            string fileName = Path.GetFileName(fullPath).ToLowerInvariant();
            string policyKind = "BlockProcess";
            string policyRoot = String.Empty;
            if (fileName == "chrome.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\Google\Chrome";
            }
            else if (fileName == "msedge.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\Microsoft\Edge";
            }
            else if (fileName == "vivaldi.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\Vivaldi";
            }
            else if (fileName == "quark.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\Quark";
            }
            else if (fileName == "brave.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\BraveSoftware\Brave";
            }
            else if (fileName == "chromium.exe")
            {
                policyKind = "ChromiumPolicy";
                policyRoot = @"SOFTWARE\Policies\Chromium";
            }
            else if (fileName == "firefox.exe" && fullPath.IndexOf("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                policyKind = "FirefoxPolicy";
                policyRoot = @"SOFTWARE\Policies\Mozilla\Firefox";
            }
            return new BrowserTargetConfig
            {
                Id = "browser-" + Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant() + "-" + StableHash(fullPath),
                DisplayName = String.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(fullPath) : displayName.Trim(),
                ExecutablePath = fullPath,
                PolicyKind = policyKind,
                PolicyRoot = policyRoot
            };
        }

        private static bool LooksLikeBrowser(string displayName, string executablePath)
        {
            string fileName = Path.GetFileName(executablePath ?? String.Empty);
            if (BrowserExecutableNames.Contains(fileName))
            {
                return true;
            }
            string text = (displayName ?? String.Empty).ToLowerInvariant();
            return text.Contains("browser") || text.Contains("浏览器") || text.Contains("chrome") || text.Contains("firefox") || text.Contains("vivaldi") || text.Contains("brave") || text.Contains("opera");
        }

        private static void AddCandidate(IDictionary<string, InstalledSoftwareEntry> results, string displayName, string path)
        {
            string expanded = ExpandAndTrim(path);
            if (!IsUsableExecutable(expanded))
            {
                return;
            }
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(expanded);
            }
            catch
            {
                return;
            }
            results[fullPath] = new InstalledSoftwareEntry
            {
                DisplayName = String.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(fullPath) : displayName.Trim(),
                ExecutablePath = fullPath
            };
        }

        private static string ExtractExecutableFromCommand(string command)
        {
            string value = ExpandAndTrim(command);
            if (value.Length == 0)
            {
                return String.Empty;
            }
            if (value[0] == '"')
            {
                int closing = value.IndexOf('"', 1);
                if (closing > 1)
                {
                    return value.Substring(1, closing - 1);
                }
            }
            int exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe >= 0 ? value.Substring(0, exe + 4).Trim().Trim('"') : value.Split(',')[0].Trim().Trim('"');
        }

        private static string ExpandAndTrim(string value)
        {
            return Environment.ExpandEnvironmentVariables(value ?? String.Empty).Trim().Trim('"');
        }

        private static bool IsUsableExecutable(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }

        private static bool IsHelperExecutable(string name)
        {
            string value = (name ?? String.Empty).ToLowerInvariant();
            return value.Contains("unins") || value.Contains("uninstall") || value.Contains("setup") || value.Contains("update") || value.Contains("helper") || value.Contains("crash") || value.Contains("service");
        }

        private static bool ShouldSkipDirectory(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
            string name = Path.GetFileName(path).ToLowerInvariant();
            return name == "cache" || name == "caches" || name == "temp" || name == "tmp" || name == "packages" || name == "node_modules" || name == "windowsapps";
        }

        private static string ReadProductName(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return (info.ProductName ?? String.Empty).Trim();
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value.ToUpperInvariant())
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        private sealed class DirectoryDepth
        {
            public DirectoryDepth(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }

            public string Path { get; private set; }
            public int Depth { get; private set; }
        }
    }
}
