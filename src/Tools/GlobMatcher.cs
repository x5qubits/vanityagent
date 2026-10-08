using System.Text;
using System.Text.RegularExpressions;

namespace VanityAgent.Tools;

/// <summary>Translates glob patterns (*, ?, **, {a,b}, [set]) into anchored regexes over '/'-separated paths.
/// The the `grep` and `glob` tools filter files with it.</summary>
internal static class GlobMatcher
{
    public static Regex ToRegex(string glob, bool caseInsensitive = true)
    {
        var pattern = Translate(glob.Replace('\\', '/'));
        var opts = RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled;
        if (caseInsensitive) opts |= RegexOptions.IgnoreCase;
        return new Regex("^" + pattern + "$", opts);
    }

    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?', '[', '{']) >= 0;

    private static string Translate(string glob)
    {
        var sb = new StringBuilder();
        var braceDepth = 0;
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 3; }
                        else { sb.Append(".*"); i += 2; }
                    }
                    else { sb.Append("[^/]*"); i++; }
                    break;
                case '?': sb.Append("[^/]"); i++; break;
                case '[':
                    var end = glob.IndexOf(']', i + 1 == glob.Length ? i : i + 1 + (glob[i + 1] is '!' or '^' ? 1 : 0));
                    if (end < 0) { sb.Append(Regex.Escape("[")); i++; }
                    else
                    {
                        var body = glob[(i + 1)..end];
                        if (body.StartsWith('!')) body = "^" + body[1..];
                        sb.Append('[').Append(body).Append(']');
                        i = end + 1;
                    }
                    break;
                case '{': braceDepth++; sb.Append("(?:"); i++; break;
                case '}' when braceDepth > 0: braceDepth--; sb.Append(')'); i++; break;
                case ',' when braceDepth > 0: sb.Append('|'); i++; break;
                default: sb.Append(Regex.Escape(c.ToString())); i++; break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Recursive file enumeration that skips excluded directory names, reparse points and unreadable folders.</summary>
internal static class FileWalker
{
    /// <summary>True for the workspace's .vanity folder itself or anything inside it: the harness's own state.</summary>
    public static bool IsHarnessDir(string fullPath, string workspace)
    {
        var vanity = Path.GetFullPath(Path.Combine(workspace, ".vanity")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(p, vanity, StringComparison.OrdinalIgnoreCase) || p.StartsWith(vanity + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Never part of a search: the harness's own state, version control, dependency and build output.</summary>
    public static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", "__pycache__" };

    public static IEnumerable<FileInfo> EnumerateFiles(string root, ISet<string>? excludedDirectories = null)
    {
        var excluded = excludedDirectories ?? DefaultExcluded;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            FileInfo[] files; DirectoryInfo[] subdirs;
            try { files = dir.GetFiles(); subdirs = dir.GetDirectories(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var sub in subdirs)
            {
                if (excluded.Contains(sub.Name)) continue;
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(sub);
            }
        }
    }
}
