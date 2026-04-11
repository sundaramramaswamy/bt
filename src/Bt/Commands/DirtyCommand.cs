static class DirtyCommand
{
    public static int Affected(BuildGraph g, string[] explicitFiles)
    {
        HashSet<string>? commandScope = null;
        if (explicitFiles.Length > 0)
        {
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in explicitFiles)
            {
                var r = FileResolver.Resolve(g, arg);
                if (r != null) resolved.Add(r);
            }
            if (resolved.Count == 0)
            {
                Console.Error.WriteLine($"{Clr.Dim}No files found in graph.{Clr.Reset}");
                return 0;
            }
            Console.Error.WriteLine($"{Clr.Dim}Target files ({resolved.Count}):{Clr.Reset}");
            var sortedResolved = new List<string>(resolved);
            sortedResolved.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in sortedResolved)
                Console.Error.WriteLine($"  {Clr.Green}{f}{Clr.Reset}");
            Console.Error.WriteLine();
            commandScope = g.GetCommandCone(resolved);
        }

        Console.Error.WriteLine($"{Clr.Dim}Checking file timestamps...{Clr.Reset}");
        var (mtimePlan, dirtySources) = g.GetDirtyCommandsByMtime(commandScope);
        // Only show commands we can actually execute
        var plan = new List<CommandNode>();
        foreach (var c in mtimePlan)
            if (!string.IsNullOrEmpty(c.CommandLine)) plan.Add(c);

        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
            return 0;
        }

        // Print a tree per dirty source, filtered to only dirty commands.
        var dirtyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan) dirtyIds.Add(c.Id);
        foreach (var cmd in g.Commands.Values)
        {
            if (!cmd.Tool.StartsWith("#")) continue;
            bool match = false;
            foreach (var o in cmd.Outputs)
            {
                if (!g.FileToConsumers.TryGetValue(o, out var cids)) continue;
                foreach (var cid in cids)
                    if (dirtyIds.Contains(cid)) { match = true; break; }
                if (match) break;
            }
            if (match) dirtyIds.Add(cmd.Id);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sortedSources = new List<string>(dirtySources.Keys);
        sortedSources.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sortedSources)
        {
            Console.WriteLine($"{Clr.Red}{src}{Clr.Reset}");
            GraphCommand.PrintTreeForward(g, src, "", true, seen, dirtyIds);
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{Clr.Yellow}{plan.Count} command{(plan.Count == 1 ? "" : "s")} to run.{Clr.Reset}");
        return 0;
    }
}
