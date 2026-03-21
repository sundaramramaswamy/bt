class BuildGraph
{
    /// Root directory all stored paths are relative to (typically solution dir).
    public required string RootDir { get; init; }

    public Dictionary<string, FileNode> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CommandNode> Commands { get; init; } = [];

    // file → commands that consume it
    public Dictionary<string, List<string>> FileToConsumers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // file → command that produces it
    public Dictionary<string, string> FileToProducer { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// External include path prefixes (from CAExcludePath). Files under these
    /// are SDK/generated headers — excluded from mtime dirty checking.
    public HashSet<string> ExternalPrefixes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// Per-project environment variables extracted from SetEnv tasks in the binlog.
    /// Key: project file name (e.g. "XaBench.vcxproj"), Value: env var name → value.
    public Dictionary<string, Dictionary<string, string>> ProjectEnv { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // file → synthetic (#include) commands that produce it (1:N, unlike FileToProducer)
    public Dictionary<string, List<string>> SyntheticProducers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// Check if a file path is under an external include prefix.
    public bool IsExternal(string relativePath) =>
        ExternalPrefixes.Any(p => relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public void AddConsumer(string filePath, string cmdId)
    {
        if (!FileToConsumers.TryGetValue(filePath, out var list))
        {
            list = [];
            FileToConsumers[filePath] = list;
        }
        list.Add(cmdId);
    }

    /// Convert a root-relative path back to absolute.
    public string ToAbsolute(string relativePath) =>
        Path.GetFullPath(Path.Combine(RootDir, relativePath));

    /// Convert an absolute path to root-relative, normalizing casing.
    /// Prefers the case-normalization cache (seeded from binlog paths) over filesystem.
    public string ToRelative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (_caseCache.TryGetValue(full, out var cached)) return cached;
        // For paths not in cache (e.g., headers from tlog), resolve actual
        // casing from the filesystem. This is slower but only hits for
        // tlog-discovered paths not already in the binlog.
        if (File.Exists(full) || Directory.Exists(full))
            full = GetActualCasePath(full);
        var rel = Path.GetRelativePath(RootDir, full);
        _caseCache[full] = rel;
        return rel;
    }

    /// Seed the case cache from all paths already in the graph (binlog-derived,
    /// correct casing). Call this before parsing tlog files so their ALLCAPS
    /// paths resolve via cache instead of filesystem lookups.
    public void SeedCaseCache()
    {
        foreach (var rel in Files.Keys)
        {
            var abs = Path.GetFullPath(Path.Combine(RootDir, rel));
            _caseCache[abs] = rel;  // OrdinalIgnoreCase key → correct-case value
        }
    }

    readonly Dictionary<string, string> _caseCache = new(StringComparer.OrdinalIgnoreCase);

    /// Add a single entry to the case cache (e.g., from ClInclude with correct casing).
    public void PrimeCaseCacheEntry(string absolutePath, string relativePath)
        => _caseCache.TryAdd(absolutePath, relativePath);

    /// Walk path segments and resolve actual casing from the filesystem.
    static string GetActualCasePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root == null) return path;
        var result = root.ToUpperInvariant().TrimEnd('\\');
        var rest = path[root.Length..];
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Directory.EnumerateFileSystemEntries(result, segment).FirstOrDefault();
            result = match ?? Path.Combine(result, segment);
        }
        return result;
    }

    /// Given a source file, find all files reachable downstream through the graph.
    /// Returns every node on every forward path (intermediates included).
    public HashSet<string> GetOutputsOf(string sourcePath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectForward(sourcePath, visited);
        visited.Remove(sourcePath); // exclude self
        return visited;
    }

    /// Find the nearest dev-visible outputs reachable from a source file.
    /// Walks through intermediate nodes but stops at the first visible file.
    /// Used for the DOT graph to avoid transitive shortcut edges.
    public HashSet<string> GetNearestVisibleOutputsOf(string sourcePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        visited.Add(sourcePath);
        WalkToNearestVisible(sourcePath, visited, result);
        return result;
    }

    /// Walk forward from a visible file, returning (tool, output) edges.
    /// Skips through hidden intermediates, labelling with the first command's tool.
    public HashSet<(string Tool, string Output)> GetNearestVisibleEdgesFrom(string filePath)
    {
        var result = new HashSet<(string, string)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { filePath };
        WalkToNearestVisibleEdges(filePath, null, visited, result);
        return result;
    }

    /// Given an output file, find all files reachable upstream through the graph.
    /// Returns every node on every backward path (intermediates included).
    public HashSet<string> GetSourcesOf(string outputPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectBackward(outputPath, visited);
        visited.Remove(outputPath); // exclude self
        return visited;
    }

    /// Walk forward through commands, stopping at the first dev-visible file.
    /// Hidden intermediates (.pch, .res) are walked through transparently.
    void WalkToNearestVisible(string filePath, HashSet<string> visited, HashSet<string> outputs)
    {
        if (!FileToConsumers.TryGetValue(filePath, out var consumerIds)) return;

        foreach (var cmdId in consumerIds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            foreach (var output in cmd.Outputs)
            {
                if (!visited.Add(output)) continue;
                if (Files.TryGetValue(output, out var f) && FileKinds.IsDevVisible(f.Kind))
                    outputs.Add(output);     // visible → stop here
                else
                    WalkToNearestVisible(output, visited, outputs); // intermediate → keep walking
            }
        }
    }

    /// Walk forward, collecting (tool, output) edges. Carries the originating
    /// tool label through hidden intermediates so the edge shows the first task.
    void WalkToNearestVisibleEdges(string filePath, string? originTool,
        HashSet<string> visited, HashSet<(string, string)> edges)
    {
        if (!FileToConsumers.TryGetValue(filePath, out var consumerIds)) return;

        foreach (var cmdId in consumerIds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            var tool = originTool ?? cmd.Tool;
            foreach (var output in cmd.Outputs)
            {
                if (!visited.Add(output)) continue;
                if (Files.TryGetValue(output, out var f) && FileKinds.IsDevVisible(f.Kind))
                    edges.Add((tool, output));
                else
                    WalkToNearestVisibleEdges(output, tool, visited, edges);
            }
        }
    }

    /// Get all files reachable from the given file, both forward and backward.
    /// Unlike GetOutputsOf/GetSourcesOf, this returns EVERY node on the path,
    /// not just endpoints. Used for graph filtering.
    public HashSet<string> GetReachable(string filePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectForward(filePath, result);
        result.Remove(filePath); // allow backward walk to re-enter at start node
        CollectBackward(filePath, result);
        return result;
    }

    void CollectForward(string filePath, HashSet<string> visited)
    {
        if (!visited.Add(filePath)) return;
        if (!FileToConsumers.TryGetValue(filePath, out var consumerIds)) return;
        foreach (var cmdId in consumerIds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            foreach (var output in cmd.Outputs)
                CollectForward(output, visited);
        }
    }

    void CollectBackward(string filePath, HashSet<string> visited)
    {
        if (!visited.Add(filePath)) return;
        if (!FileToProducer.TryGetValue(filePath, out var producerId)) return;
        if (!Commands.TryGetValue(producerId, out var cmd)) return;
        foreach (var input in cmd.Inputs)
            CollectBackward(input, visited);
    }

    /// Given a set of changed files, find all commands that need to re-run,
    /// returned in topological (dependency-first) order.
    /// Skips synthetic commands (#include) since they aren't real build steps.
    public List<CommandNode> GetAffectedCommands(IEnumerable<string> changedFiles)
    {
        // Walk forward from changed files, collecting affected command IDs
        var affectedCmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void WalkAffected(string filePath)
        {
            if (!visitedFiles.Add(filePath)) return;
            if (!FileToConsumers.TryGetValue(filePath, out var consumerIds)) return;
            foreach (var cmdId in consumerIds)
            {
                affectedCmds.Add(cmdId);
                if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
                foreach (var output in cmd.Outputs)
                    WalkAffected(output);
            }
        }

        foreach (var f in changedFiles)
            WalkAffected(f);

        // Topo-sort: a command comes after all commands that produce its inputs.
        // Kahn's algorithm on the affected subset.
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmdId in affectedCmds)
        {
            inDegree.TryAdd(cmdId, 0);
            dependents.TryAdd(cmdId, []);
        }
        foreach (var cmdId in affectedCmds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            foreach (var input in cmd.Inputs)
            {
                if (!FileToProducer.TryGetValue(input, out var depCmdId)) continue;
                if (!affectedCmds.Contains(depCmdId)) continue;
                inDegree[cmdId] = inDegree.GetValueOrDefault(cmdId) + 1;
                if (!dependents.TryGetValue(depCmdId, out var depList))
                {
                    depList = [];
                    dependents[depCmdId] = depList;
                }
                depList.Add(cmdId);
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(k => k));
        var result = new List<CommandNode>();
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var cmdId = queue.Dequeue();
            if (Commands.TryGetValue(cmdId, out var cmd) && !cmd.Tool.StartsWith("#"))
                result.Add(cmd);
            if (!dependents.TryGetValue(cmdId, out var deps)) continue;
            foreach (var dep in deps.OrderBy(d => d))
            {
                inDegree[dep]--;
                if (inDegree[dep] == 0) queue.Enqueue(dep);
            }
        }

        // Dedup: if multiple commands produce the same output set, keep the one
        // with the most inputs (most complete). Common with duplicate Link tasks.
        var deduped = new List<CommandNode>();
        foreach (var cmd in result)
        {
            var outputKey = string.Join("|", cmd.Outputs.OrderBy(o => o, StringComparer.OrdinalIgnoreCase));
            if (seenOutputs.Add(outputKey))
                deduped.Add(cmd);
        }
        return deduped;
    }

    /// Find dirty commands by comparing file timestamps (make/ninja-style).
    /// A command is dirty if any input is newer than any output, or if any
    /// output is missing. Dirty propagates forward through the graph.
    /// Returns commands in topological order, skipping synthetic (#include),
    /// along with a map from each dirty source file to the commands it triggered.
    public (List<CommandNode> Plan, Dictionary<string, List<CommandNode>> DirtySources) GetDirtyCommandsByMtime()
    {
        // Topo-sort ALL real commands first (Kahn's algorithm)
        var realCmds = Commands.Values.Where(c => !c.Tool.StartsWith("#")).ToList();
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in realCmds)
        {
            inDegree.TryAdd(cmd.Id, 0);
            dependents.TryAdd(cmd.Id, []);
        }
        var cmdById = realCmds.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in realCmds)
            foreach (var input in cmd.Inputs)
                if (FileToProducer.TryGetValue(input, out var depId) && cmdById.ContainsKey(depId))
                {
                    inDegree[cmd.Id] = inDegree.GetValueOrDefault(cmd.Id) + 1;
                    dependents[depId].Add(cmd.Id);
                }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(k => k));
        var topoOrder = new List<CommandNode>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (cmdById.TryGetValue(id, out var cmd)) topoOrder.Add(cmd);
            foreach (var dep in dependents.GetValueOrDefault(id, []).OrderBy(d => d))
                if (--inDegree[dep] == 0) queue.Enqueue(dep);
        }

        // Pre-stat all files in parallel: collect every path that could be
        // checked (inputs + transitive headers + outputs), then batch the
        // I/O so the OS metadata cache can service them concurrently.
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in topoOrder)
        {
            foreach (var input in cmd.Inputs)
            {
                allPaths.Add(input);
                CollectSyntheticSources(input, allPaths);
            }
            foreach (var output in cmd.Outputs)
                allPaths.Add(output);
        }

        var mtimeCache = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(allPaths, path =>
        {
            var abs = ToAbsolute(path);
            mtimeCache[path] = File.Exists(abs) ? File.GetLastWriteTimeUtc(abs) : null;
        });

        // Walk topo order, check mtime. Dirty propagates forward.
        var dirtyOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CommandNode>();
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track: which source files triggered each command
        var cmdTriggers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in topoOrder)
        {
            var outputKey = string.Join("|", cmd.Outputs.OrderBy(o => o, StringComparer.OrdinalIgnoreCase));
            if (!seenOutputs.Add(outputKey)) continue;

            bool dirty = false;
            var triggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If any input was produced by a dirty command, we're dirty too (propagation)
            foreach (var i in cmd.Inputs)
                if (dirtyOutputs.Contains(i) && FileToProducer.TryGetValue(i, out var pid) && cmdTriggers.ContainsKey(pid))
                {
                    dirty = true;
                    triggers.UnionWith(cmdTriggers[pid]);
                }

            if (!dirty)
            {
                // Check mtime: max(input mtime) > min(output mtime), or output missing.
                // For inputs produced by synthetic commands (#include), also check
                // the transitive sources (headers) so a touched .h triggers CL.
                var allInputs = new HashSet<string>(cmd.Inputs, StringComparer.OrdinalIgnoreCase);
                foreach (var input in cmd.Inputs)
                    CollectSyntheticSources(input, allInputs);

                // Find the newest input and which file it is
                DateTime maxInputTime = DateTime.MinValue;
                string? newestInput = null;
                foreach (var input in allInputs)
                {
                    var mtime = mtimeCache.GetValueOrDefault(input);
                    if (mtime is { } t && t > maxInputTime)
                    {
                        maxInputTime = t;
                        newestInput = input;
                    }
                }

                foreach (var output in cmd.Outputs)
                {
                    var mtime = mtimeCache.GetValueOrDefault(output);
                    if (mtime is null) { dirty = true; triggers.Add(output + " (missing)"); break; }
                    if (maxInputTime > mtime.Value) { dirty = true; if (newestInput != null) triggers.Add(newestInput); break; }
                }
            }

            if (dirty)
            {
                result.Add(cmd);
                cmdTriggers[cmd.Id] = triggers;
                foreach (var o in cmd.Outputs) dirtyOutputs.Add(o);
            }
        }

        // Invert: group by dirty source → commands it affects
        var dirtySources = new Dictionary<string, List<CommandNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in result)
            if (cmdTriggers.TryGetValue(cmd.Id, out var trigs))
                foreach (var src in trigs)
                {
                    if (!dirtySources.ContainsKey(src)) dirtySources[src] = [];
                    dirtySources[src].Add(cmd);
                }

        return (result, dirtySources);
    }

    /// Walk backward through synthetic (#include) commands to collect transitive header inputs.
    /// Skips external headers (SDK, generated) to avoid false mtime positives.
    void CollectSyntheticSources(string file, HashSet<string> collected)
    {
        if (!SyntheticProducers.TryGetValue(file, out var cmdIds)) return;
        foreach (var cmdId in cmdIds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            foreach (var input in cmd.Inputs)
                if (!IsExternal(input) && collected.Add(input))
                    CollectSyntheticSources(input, collected);
        }
    }
}
