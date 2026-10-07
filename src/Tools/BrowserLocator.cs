namespace VanityAgent.Tools;

internal static class BrowserLocator
{
    public static string? FindBrowser()
    {
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(pf,    "Microsoft", "Edge",   "Application", "msedge.exe"),
            Path.Combine(pf86,  "Microsoft", "Edge",   "Application", "msedge.exe"),
            Path.Combine(pf,    "Google",    "Chrome",  "Application", "chrome.exe"),
            Path.Combine(pf86,  "Google",    "Chrome",  "Application", "chrome.exe"),
            Path.Combine(local, "Google",    "Chrome",  "Application", "chrome.exe"),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}
