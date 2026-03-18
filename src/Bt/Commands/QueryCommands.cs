static class QueryCommands
{
    public static int OutputsOf(BuildGraph g, string[] files)
    {
        foreach (var file in files)
        {
            var resolved = FileResolver.Resolve(g, file);
            if (resolved == null) continue;
            Console.WriteLine($"{Clr.Cyan}{resolved}{Clr.Reset}");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GraphCommand.PrintTreeForward(g, resolved, "", true, seen);
        }
        return 0;
    }

    public static int SourcesOf(BuildGraph g, string[] files)
    {
        foreach (var file in files)
        {
            var resolved = FileResolver.Resolve(g, file);
            if (resolved == null) continue;
            Console.WriteLine($"{Clr.Cyan}{resolved}{Clr.Reset}");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GraphCommand.PrintTreeBackward(g, resolved, "", true, seen);
        }
        return 0;
    }
}
