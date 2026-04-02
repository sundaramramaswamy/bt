static class GraphCommand
{
    public static int ShowGraph(BuildGraph g, string[]? filterFiles, string[]? filterProjects, bool includeHeaders = false)
    {
        // Compute the set of allowed nodes when filters are active.
        HashSet<string>? allowed = null;

        if (filterFiles is { Length: > 0 })
        {
            allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in filterFiles)
            {
                var resolved = FileResolver.Resolve(g, arg);
                if (resolved == null) continue;
                // Include everything reachable forward and backward (all intermediate nodes too)
                foreach (var r in g.GetReachable(resolved, includeHeaders)) allowed.Add(r);
            }
            if (allowed.Count == 0) { Console.Error.WriteLine("No matching files in graph."); return 1; }
        }

        if (filterProjects is { Length: > 0 })
        {
            // Match projects by name, ignoring .vcxproj/.csproj extensions.
            var allProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in g.Commands.Values) allProjects.Add(c.Project);
            var matchedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in filterProjects)
                foreach (var p in allProjects)
                    if (p.Equals(filter, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(p).Equals(filter, StringComparison.OrdinalIgnoreCase))
                        matchedProjects.Add(p);

            var projFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cmd in g.Commands.Values)
            {
                if (!matchedProjects.Contains(cmd.Project)) continue;
                foreach (var i in cmd.Inputs) projFiles.Add(i);
                foreach (var o in cmd.Outputs) projFiles.Add(o);
            }
            if (projFiles.Count == 0)
            {
                Console.Error.WriteLine($"No commands found for project(s): {string.Join(", ", filterProjects)}");
                Console.Error.WriteLine("Available projects:");
                var sortedProjects = new List<string>();
                foreach (var p in allProjects)
                    if (!string.IsNullOrEmpty(p)) sortedProjects.Add(p);
                sortedProjects.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var p in sortedProjects)
                    Console.Error.WriteLine($"  {Path.GetFileNameWithoutExtension(p)}");
                return 1;
            }
            if (allowed == null)
                allowed = projFiles;
            else
            {
                var intersection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in allowed)
                    if (projFiles.Contains(f)) intersection.Add(f);
                allowed = intersection;
            }
        }

        // Developer-centric graph: sources, intermediates (.obj), and outputs are visible.
        // Only hidden intermediates (.pch, .res) are collapsed — edges skip through them.
        var visible = new List<FileNode>();
        foreach (var f in g.Files.Values)
            if (FileKinds.IsDevVisible(f.Kind) && (allowed == null || allowed.Contains(f.Path)))
                visible.Add(f);

        // Walk forward from each visible file to its nearest visible outputs.
        var edges = new HashSet<(string src, string tool, string output)>();
        foreach (var f in visible)
            foreach (var (tool, output) in g.GetNearestVisibleEdgesFrom(f.Path))
                if (allowed == null || allowed.Contains(output))
                    edges.Add((f.Path, tool, output));

        Console.WriteLine("digraph build {");
        Console.WriteLine("  rankdir=LR;");
        Console.WriteLine("  node [fontname=\"Consolas\" fontsize=10];");
        Console.WriteLine("  edge [fontname=\"Consolas\" fontsize=8];");
        Console.WriteLine();

        var nodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, _, o) in edges) { nodeSet.Add(s); nodeSet.Add(o); }

        // Include visible nodes that are produced but have no consumers — terminal outputs.
        foreach (var f in visible)
            if (g.FileToProducer.ContainsKey(f.Path)
                && !g.FileToConsumers.ContainsKey(f.Path))
                nodeSet.Add(f.Path);

        foreach (var path in nodeSet)
        {
            if (!g.Files.TryGetValue(path, out var f)) continue;
            var shape = f.Kind switch
            {
                FileKind.Source => "note",
                FileKind.Intermediate => "ellipse",
                FileKind.Output => "box3d",
                _ => "box"
            };
            Console.WriteLine($"  {Dot.Id(path)} [label=\"{Dot.Escape(path)}\" shape={shape}];");
        }
        Console.WriteLine();

        foreach (var (s, tool, o) in edges)
            Console.WriteLine($"  {Dot.Id(s)} -> {Dot.Id(o)} [label=\"{Dot.Escape(tool)}\"];");

        Console.WriteLine("}");

        if (allowed != null)
            Console.Error.WriteLine($"{Clr.Dim}Filter: {nodeSet.Count} nodes, {edges.Count} edges{Clr.Reset}");
        return 0;
    }

    public static string FileClr(BuildGraph g, string path) =>
        g.Files.TryGetValue(path, out var f) ? f.Kind switch
        {
            FileKind.Source => Clr.Green,
            FileKind.Output => Clr.Yellow,
            _ => Clr.Dim
        } : Clr.Dim;

    public static void PrintTreeForward(BuildGraph g, string filePath, string indent, bool last, HashSet<string> seen, HashSet<string>? dirtyIds = null)
    {
        if (!g.FileToConsumers.TryGetValue(filePath, out var consumerIds)) return;

        // Collect (tool, output) pairs from all consuming commands, deduplicated
        var children = new List<(string tool, string output)>();
        var childSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmdId in consumerIds)
            if (g.Commands.TryGetValue(cmdId, out var cmd))
                if (dirtyIds == null || dirtyIds.Contains(cmdId))
                    foreach (var o in cmd.Outputs)
                        if (childSeen.Add(o))
                            children.Add((cmd.Tool, o));
        children.Sort((a, b) => string.Compare(a.output, b.output, StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < children.Count; i++)
        {
            var (tool, output) = children[i];
            var isLast = i == children.Count - 1;
            var branch = isLast ? "└── " : "├── ";
            var cont   = isLast ? "    " : "│   ";

            if (!seen.Add(output))
            {
                Console.WriteLine($"{indent}{branch}{Clr.Dim}[{tool}] {output} (↑ above){Clr.Reset}");
                continue;
            }
            Console.WriteLine($"{indent}{branch}{Clr.Dim}[{tool}]{Clr.Reset} {FileClr(g, output)}{output}{Clr.Reset}");
            PrintTreeForward(g, output, indent + cont, isLast, seen, dirtyIds);
        }
    }

    public static void PrintTreeBackward(BuildGraph g, string filePath, string indent, bool last, HashSet<string> seen, bool includeHeaders = false)
    {
        var children = new List<(string tool, string input)>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Real producer chain (CL → .obj, LINK → .exe, etc.)
        if (g.FileToProducer.TryGetValue(filePath, out var producerId)
            && g.Commands.TryGetValue(producerId, out var cmd)
            && !cmd.Tool.StartsWith('#'))
        {
            foreach (var input in cmd.Inputs)
            {
                children.Add((cmd.Tool, input));
                added.Add(input);
            }
        }

        // Synthetic #include headers (from tlog) — skip duplicates
        if (includeHeaders && g.SyntheticProducers.TryGetValue(filePath, out var synIds))
        {
            foreach (var synId in synIds)
            {
                if (!g.Commands.TryGetValue(synId, out var synCmd)) continue;
                foreach (var input in synCmd.Inputs)
                    if (!g.IsExternal(input) && added.Add(input))
                        children.Add((synCmd.Tool, input));
            }
        }

        children.Sort((a, b) => string.Compare(a.input, b.input, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < children.Count; i++)
        {
            var (tool, input) = children[i];
            var isLast = i == children.Count - 1;
            var branch = isLast ? "└── " : "├── ";
            var cont   = isLast ? "    " : "│   ";

            if (!seen.Add(input))
            {
                Console.WriteLine($"{indent}{branch}{Clr.Dim}[{tool}] {input} (↑ above){Clr.Reset}");
                continue;
            }
            Console.WriteLine($"{indent}{branch}{Clr.Dim}[{tool}]{Clr.Reset} {FileClr(g, input)}{input}{Clr.Reset}");
            PrintTreeBackward(g, input, indent + cont, isLast, seen, includeHeaders);
        }
    }
}
