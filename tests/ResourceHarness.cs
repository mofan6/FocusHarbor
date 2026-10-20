using System;
using System.Linq;
using System.Reflection;

internal static class ResourceHarness
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            return 64;
        }
        string[] resources = Assembly.LoadFrom(args[0]).GetManifestResourceNames();
        Console.WriteLine("RESOURCES=" + String.Join(",", resources));
        bool passed = resources.Contains("FocusHarbor.MainWindow.xaml") && resources.Contains("FocusHarbor.UserGuide.md");
        Console.WriteLine(passed ? "EMBEDDED_RESOURCES_OK" : "EMBEDDED_RESOURCES_MISSING");
        return passed ? 0 : 2;
    }
}
