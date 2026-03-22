/// Try to match a user-supplied filename/path against the graph's file index.
/// Graph stores root-relative paths; user may pass absolute or partial names.
static class FileResolver
{
    public static string? Resolve(BuildGraph g, string arg)
    {
        // Normalize forward slashes so Unix-style paths work on Windows
        arg = arg.Replace('/', '\\');

        // If user gave an absolute path, convert to root-relative for lookup
        var key = Path.IsPathRooted(arg) ? g.ToRelative(Path.GetFullPath(arg)) : arg;

        // Exact match (case-insensitive)
        if (g.Files.ContainsKey(key)) return key;

        // Suffix match: user might type "main.cpp" to mean "XaBench\main.cpp"
        var matches = new List<string>();
        foreach (var k in g.Files.Keys)
            if (k.EndsWith(arg, StringComparison.OrdinalIgnoreCase)
                && (k.Length == arg.Length || k[k.Length - arg.Length - 1] is '\\' or '/'))
                matches.Add(k);

        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"{Clr.Yellow}{arg}{Clr.Reset}: ambiguous — matches {matches.Count} files:");
            foreach (var m in matches) Console.Error.WriteLine($"  {Clr.Dim}{m}{Clr.Reset}");
            return null;
        }
        Console.Error.WriteLine($"{Clr.Red}{arg}{Clr.Reset}: not found in graph");
        return null;
    }
}
