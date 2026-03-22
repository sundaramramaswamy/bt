static class DirtyCommand
{
    public static int Affected(BuildGraph g, string[] explicitFiles)
    {
        List<CommandNode> plan;

        if (explicitFiles.Length > 0)
        {
            // Explicit files: resolve and walk forward
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
            Console.Error.WriteLine($"{Clr.Dim}Explicit files ({resolved.Count}):{Clr.Reset}");
            var sortedResolved = new List<string>(resolved);
            sortedResolved.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in sortedResolved)
                Console.Error.WriteLine($"  {Clr.Green}{f}{Clr.Reset}");
            Console.Error.WriteLine();
            plan = g.GetAffectedCommands(resolved);
        }
        else
        {
            // Default: mtime-based dirty detection (make/ninja-style)
            Console.Error.WriteLine($"{Clr.Dim}Checking file timestamps...{Clr.Reset}");
            var (mtimePlan, dirtySources) = g.GetDirtyCommandsByMtime();
            // Only show commands we can actually execute
            plan = new List<CommandNode>();
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

        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
            return 0;
        }

        Console.Error.WriteLine($"{Clr.Yellow}Build plan ({plan.Count} commands):{Clr.Reset}");
        Console.Error.WriteLine();
        int step = 0;
        foreach (var cmd in plan)
        {
            step++;
            Console.WriteLine($"{Clr.Bold}{step,3}. [{cmd.Tool}]{Clr.Reset}  {Clr.Dim}{cmd.Project}{Clr.Reset}");
            foreach (var i in cmd.Inputs)
                Console.WriteLine($"       in:  {Clr.Green}{i}{Clr.Reset}");
            foreach (var o in cmd.Outputs)
                Console.WriteLine($"       out: {Clr.Yellow}{o}{Clr.Reset}");
        }
        return 0;
    }
}
