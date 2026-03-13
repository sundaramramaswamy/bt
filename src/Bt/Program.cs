using System.CommandLine;
using System.Text.Json;
using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// -- Global options (Recursive = visible on all subcommands) --
var binlogOption = new Option<string>("--binlog") { Description = "Path to .binlog file", Recursive = true };
binlogOption.DefaultValueFactory = _ => "msbuild.binlog";

var colorOption = new Option<string>("--color") { Description = "Coloured output: auto, always, never", Recursive = true };
colorOption.Aliases.Add("--colour");
colorOption.DefaultValueFactory = _ => "auto";
colorOption.AcceptOnlyFromAmong("auto", "always", "never");

// -- Subcommands --
var graphCmd = new Command("graph", "Emit Graphviz DOT dependency graph");
var graphFileOption = new Option<string[]>("--file") { Description = "Filter graph to subgraph reachable from/to these files", Arity = ArgumentArity.OneOrMore };
graphFileOption.Aliases.Add("-f");
var graphProjectOption = new Option<string[]>("--project") { Description = "Filter graph to nodes belonging to these projects", Arity = ArgumentArity.OneOrMore };
graphProjectOption.Aliases.Add("-p");
graphCmd.Add(graphFileOption);
graphCmd.Add(graphProjectOption);

var outputsFilesArg = new Argument<string[]>("files") { Description = "Source files to query", Arity = ArgumentArity.OneOrMore };
var outputsOfCmd = new Command("outputs-of", "What outputs get built when <file> changes?");
outputsOfCmd.Add(outputsFilesArg);

var sourcesFilesArg = new Argument<string[]>("files") { Description = "Output files to query", Arity = ArgumentArity.OneOrMore };
var sourcesOfCmd = new Command("sources-of", "What source files feed into <file>?");
sourcesOfCmd.Add(sourcesFilesArg);

// -- Wire up --
var root = new RootCommand("bt — MSBuild dependency graph explorer");
root.Add(binlogOption);
root.Add(colorOption);
root.Add(graphCmd);
root.Add(outputsOfCmd);
root.Add(sourcesOfCmd);

graphCmd.SetAction(result =>
{
    var g = Setup(result);
    var filterFiles = result.GetValue(graphFileOption);
    var filterProjects = result.GetValue(graphProjectOption);
    return ShowGraph(g, filterFiles, filterProjects);
});

outputsOfCmd.SetAction(result =>
{
    var g = Setup(result);
    var files = result.GetValue(outputsFilesArg)!;
    return OutputsOf(g, files);
});

sourcesOfCmd.SetAction(result =>
{
    var g = Setup(result);
    var files = result.GetValue(sourcesFilesArg)!;
    return SourcesOf(g, files);
});

return root.Parse(args).Invoke();

// -- Helpers --
BuildGraph Setup(ParseResult result)
{
    var binlog = result.GetValue(binlogOption)!;
    var color = result.GetValue(colorOption)!;
    Clr.SetMode(color);

    // If default path doesn't exist, try common variants
    if (!File.Exists(binlog) && binlog == "msbuild.binlog")
        foreach (var alt in new[] { "msbuild_debug.binlog", "msbuild_release.binlog" })
            if (File.Exists(alt)) { binlog = alt; break; }

    return LoadGraph(Path.GetFullPath(binlog));
}

// ============================================================
// Commands
// ============================================================

static int ShowGraph(BuildGraph g, string[]? filterFiles, string[]? filterProjects)
{
    // Compute the set of allowed nodes when filters are active.
    HashSet<string>? allowed = null;

    if (filterFiles is { Length: > 0 })
    {
        allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in filterFiles)
        {
            var resolved = ResolveFileArg(g, arg);
            if (resolved == null) continue;
            // Include everything reachable forward and backward (all intermediate nodes too)
            foreach (var r in g.GetReachable(resolved)) allowed.Add(r);
        }
        if (allowed.Count == 0) { Console.Error.WriteLine("No matching files in graph."); return 1; }
    }

    if (filterProjects is { Length: > 0 })
    {
        // Match projects by name, ignoring .vcxproj/.csproj extensions.
        // "XaBench" matches "XaBench.vcxproj".
        var allProjects = g.Commands.Values.Select(c => c.Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            foreach (var p in allProjects.Where(p => !string.IsNullOrEmpty(p)).OrderBy(p => p))
                Console.Error.WriteLine($"  {Path.GetFileNameWithoutExtension(p)}");
            return 1;
        }
        allowed = allowed == null ? projFiles : new HashSet<string>(allowed.Intersect(projFiles, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    // Developer-centric graph: sources, intermediates (.obj), and outputs are visible.
    // Only hidden intermediates (.pch, .res) are collapsed — edges skip through them.
    var visible = g.Files.Values
        .Where(f => FileKinds.IsDevVisible(f.Kind) && (allowed == null || allowed.Contains(f.Path)))
        .ToList();

    // Walk forward from each visible file to its nearest visible outputs.
    // Returns edges as (source, tool, output) triples.
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

    // Include visible nodes that are produced (have a command) but have no
    // consumers — these are terminal outputs like .pri that the walk won't reach.
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

static int OutputsOf(BuildGraph g, string[] files)
{
    foreach (var file in files)
    {
        var resolved = ResolveFileArg(g, file);
        if (resolved == null) continue;

        var outputs = g.GetOutputsOf(resolved);
        Console.WriteLine($"{Clr.Cyan}{file}{Clr.Reset}:");
        foreach (var o in outputs)
            Console.WriteLine($"  → {Clr.Yellow}{g.ToAbsolute(o)}{Clr.Reset}");
    }
    return 0;
}

static int SourcesOf(BuildGraph g, string[] files)
{
    foreach (var file in files)
    {
        var resolved = ResolveFileArg(g, file);
        if (resolved == null) continue;

        var sources = g.GetSourcesOf(resolved);
        Console.WriteLine($"{Clr.Cyan}{file}{Clr.Reset}:");
        foreach (var s in sources)
        {
            var clr = s.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".hxx", StringComparison.OrdinalIgnoreCase)
                    ? Clr.Magenta : Clr.Green;
            Console.WriteLine($"  ← {clr}{g.ToAbsolute(s)}{Clr.Reset}");
        }
    }
    return 0;
}

/// Try to match a user-supplied filename/path against the graph's file index.
/// Graph stores root-relative paths; user may pass absolute or partial names.
static string? ResolveFileArg(BuildGraph g, string arg)
{
    // If user gave an absolute path, convert to root-relative for lookup
    var key = Path.IsPathRooted(arg) ? g.ToRelative(Path.GetFullPath(arg)) : arg;

    // Exact match (case-insensitive)
    if (g.Files.ContainsKey(key)) return key;

    // Suffix match: user might type "main.cpp" to mean "XaBench\main.cpp"
    var matches = g.Files.Keys
        .Where(k => k.EndsWith(arg, StringComparison.OrdinalIgnoreCase)
                  && (k.Length == arg.Length || k[k.Length - arg.Length - 1] is '\\' or '/'))
        .ToList();

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

static BuildGraph LoadGraph(string binlogPath)
{
    if (!File.Exists(binlogPath))
    {
        Console.Error.WriteLine($"{Clr.Red}error:{Clr.Reset} binlog not found: {Clr.Yellow}{binlogPath}{Clr.Reset}");
        Console.Error.WriteLine($"Run a full build with: {Clr.Dim}msbuild /bl{Clr.Reset}");
        Environment.Exit(1);
    }

    var binlogDir = Path.GetDirectoryName(Path.GetFullPath(binlogPath)) ?? ".";
    var cacheDir = Path.Combine(binlogDir, ".bt");
    var cacheName = Path.GetFileNameWithoutExtension(binlogPath) + ".graph.json";
    var cachePath = Path.Combine(cacheDir, cacheName);
    var binlogStamp = File.GetLastWriteTimeUtc(binlogPath);

    // Try loading from cache
    if (File.Exists(cachePath))
    {
        try
        {
            var cached = GraphCache.Load(cachePath);
            if (cached != null && cached.BinlogTimestamp == binlogStamp.Ticks)
            {
                Console.Error.WriteLine($"{Clr.Dim}cache:{Clr.Reset} {cachePath}");
                return cached.ToGraph();
            }
            Console.Error.WriteLine($"{Clr.Dim}cache stale, rebuilding{Clr.Reset}");
        }
        catch { Console.Error.WriteLine($"{Clr.Dim}cache corrupt, rebuilding{Clr.Reset}"); }
    }

    // Parse binlog and build graph
    Console.Error.WriteLine($"{Clr.Dim}binlog:{Clr.Reset} {binlogPath}");
    var build = BinaryLog.ReadBuild(binlogPath);
    var graph = BuildGraph.FromBinlog(build);

    // Save cache
    try
    {
        Directory.CreateDirectory(cacheDir);
        GraphCache.Save(cachePath, graph, binlogStamp);
        Console.Error.WriteLine($"{Clr.Dim}cached:{Clr.Reset} {cachePath}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"{Clr.Dim}cache write failed: {ex.Message}{Clr.Reset}"); }

    return graph;
}

// ============================================================
// Data model
// ============================================================

enum FileKind { Source, Intermediate, Output }

static class FileKinds
{
    static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
        { ".cpp", ".cc", ".cxx", ".c", ".cs", ".idl", ".xaml", ".rc", ".res", ".appxmanifest" };
    static readonly HashSet<string> HeaderExts = new(StringComparer.OrdinalIgnoreCase)
        { ".h", ".hpp", ".hxx" };
    static readonly HashSet<string> OutputExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".lib", ".winmd", ".xbf", ".obj", ".pri", ".appxrecipe" };
    static readonly HashSet<string> IntermediateExts = new(StringComparer.OrdinalIgnoreCase)
        { ".pch", ".res" };

    public static FileKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (OutputExts.Contains(ext)) return FileKind.Output;
        if (IntermediateExts.Contains(ext)) return FileKind.Intermediate;
        if (SourceExts.Contains(ext) || HeaderExts.Contains(ext)) return FileKind.Source;
        return FileKind.Intermediate; // unknown → intermediate (hidden from dev graph)
    }

    public static bool IsDevVisible(FileKind kind) => kind != FileKind.Intermediate;
}

record FileNode(string Path, FileKind Kind);

record CommandNode(
    string Id,
    string Tool,         // "CL", "Link", "Lib"
    string Project,
    string Target,
    List<string> Inputs,
    List<string> Outputs);

class BuildGraph
{
    /// Root directory all stored paths are relative to (typically solution dir).
    public required string RootDir { get; init; }

    public Dictionary<string, FileNode> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CommandNode> Commands { get; } = [];

    // file → commands that consume it
    public Dictionary<string, List<string>> FileToConsumers { get; } = new(StringComparer.OrdinalIgnoreCase);
    // file → command that produces it
    public Dictionary<string, string> FileToProducer { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// Given a source file, find all final outputs reachable through the graph.
    public HashSet<string> GetOutputsOf(string sourcePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkForward(sourcePath, visited, result);
        result.Remove(sourcePath); // exclude self-edges
        return result;
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

    /// Given an output file, find all source files that feed into it.
    public HashSet<string> GetSourcesOf(string outputPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkBackward(outputPath, visited, result);
        return result;
    }

    void WalkForward(string filePath, HashSet<string> visited, HashSet<string> outputs)
    {
        if (!visited.Add(filePath)) return;

        if (!FileToConsumers.TryGetValue(filePath, out var consumerIds))
        {
            // Leaf: no command consumes this file → it's a final output.
            // Generated sources (.g.h, .g.cpp) have Source kind but ARE outputs
            // of a command, so include them too.
            if (Files.TryGetValue(filePath, out var f)
                && (f.Kind != FileKind.Source || FileToProducer.ContainsKey(filePath)))
                outputs.Add(filePath);
            return;
        }

        foreach (var cmdId in consumerIds)
        {
            if (!Commands.TryGetValue(cmdId, out var cmd)) continue;
            foreach (var output in cmd.Outputs)
                WalkForward(output, visited, outputs);
        }
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

    void WalkBackward(string filePath, HashSet<string> visited, HashSet<string> sources)
    {
        if (!visited.Add(filePath)) return;

        if (!FileToProducer.TryGetValue(filePath, out var producerId))
        {
            // Root: no command produces this file → it's a source
            sources.Add(filePath);
            return;
        }

        if (!Commands.TryGetValue(producerId, out var cmd)) return;
        foreach (var input in cmd.Inputs)
            WalkBackward(input, visited, sources);
    }

    /// Get all files reachable from the given file, both forward and backward.
    /// Unlike GetOutputsOf/GetSourcesOf, this returns EVERY node on the path,
    /// not just endpoints. Used for graph filtering.
    public HashSet<string> GetReachable(string filePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectForward(filePath, result);
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

    // --- Factory ---

    public static BuildGraph FromBinlog(Build build)
    {
        // Discover solution root as common ancestor of all project directories
        var projectDirs = build.FindChildrenRecursive<Project>()
            .Where(p => p.ProjectFile != null)
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p.ProjectFile))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootDir = GetCommonAncestor(projectDirs);

        var graph = new BuildGraph { RootDir = rootDir };
        int cmdIndex = 0;

        // Track CL command IDs per project so we can wire headers to them later.
        var clCmdsByProject = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Track CL command by absolute source path (for tlog matching — tlogs use absolute uppercase paths).
        var clCmdByAbsSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Track intermediate output dirs per project (for discovering tlog directories).
        var objDirsByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name is "CL" or "Link" or "Lib" or "MIDL"))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";
            var target = task.GetNearestParent<Target>()?.Name ?? "unknown";
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf == null) continue;

            var toolName = task.Name;
            var sources = pf.Children.OfType<Parameter>()
                .FirstOrDefault(p => p.Name == "Sources")
                ?.Children.OfType<Item>()
                .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)))
                .ToList() ?? [];

            // MIDL uses a Source property (singular), not Sources items
            if (sources.Count == 0 && toolName != "MIDL") continue;

            if (toolName == "CL")
            {
                // CL batches N sources but the relationship is 1:1 (each .cpp → its .obj).
                // Split into individual commands to keep the graph accurate.
                var objDir = pf.FindChildrenRecursive<Property>(p => p.Name == "ObjectFileName")
                    .FirstOrDefault()?.Value ?? "";
                var absObjDir = ResolveAbsolute(projDir, objDir);
                objDirsByProject.TryAdd(proj, absObjDir);

                foreach (var src in sources)
                {
                    var obj = graph.ToRelative(Path.Combine(absObjDir,
                        Path.GetFileNameWithoutExtension(src) + ".obj"));
                    var cmdId = $"CL#{cmdIndex++}:{proj}/{target}";
                    var cmd = new CommandNode(cmdId, "CL", proj, target, [src], [obj]);
                    graph.Commands[cmdId] = cmd;
                    graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                    graph.Files.TryAdd(obj, new FileNode(obj, FileKinds.Classify(obj)));
                    graph.AddConsumer(src, cmdId);
                    graph.FileToProducer[obj] = cmdId;

                    // Record absolute source path for tlog matching
                    var absSrc = Path.GetFullPath(Path.Combine(graph.RootDir, src));
                    clCmdByAbsSource.TryAdd(absSrc, cmdId);

                    if (!clCmdsByProject.TryGetValue(proj, out var projCmds))
                    {
                        projCmds = [];
                        clCmdsByProject[proj] = projCmds;
                    }
                    projCmds.Add(cmdId);
                }
            }
            else if (toolName == "MIDL")
            {
                // MIDL compiles one .idl at a time; Source is a property, not items.
                var srcProp = pf.FindChildrenRecursive<Property>(p => p.Name == "Source")
                    .FirstOrDefault()?.Value ?? "";
                if (string.IsNullOrEmpty(srcProp)) continue;
                var src = graph.ToRelative(ResolveAbsolute(projDir, srcProp));

                var metaProp = pf.FindChildrenRecursive<Property>(p => p.Name == "MetadataFileName")
                    .FirstOrDefault()?.Value ?? "";
                if (string.IsNullOrEmpty(metaProp)) continue;
                var meta = graph.ToRelative(ResolveAbsolute(projDir, metaProp));

                var cmdId = $"MIDL#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "MIDL", proj, target, [src], [meta]);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                graph.Files.TryAdd(meta, new FileNode(meta, FileKinds.Classify(meta)));
                graph.AddConsumer(src, cmdId);
                graph.FileToProducer[meta] = cmdId;
            }
            else // Link or Lib — N inputs → 1 output
            {
                var outFile = pf.FindChildrenRecursive<Property>(p => p.Name == "OutputFile")
                    .FirstOrDefault()?.Value ?? "";
                outFile = string.IsNullOrEmpty(outFile) ? ""
                    : graph.ToRelative(ResolveAbsolute(projDir, outFile));
                if (string.IsNullOrEmpty(outFile)) continue;

                var cmdId = $"{toolName}#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, toolName, proj, target, sources, [outFile]);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(outFile, new FileNode(outFile, FileKinds.Classify(outFile)));
                graph.FileToProducer[outFile] = cmdId;

                foreach (var input in sources)
                {
                    graph.Files.TryAdd(input, new FileNode(input, FileKinds.Classify(input)));
                    graph.AddConsumer(input, cmdId);
                }
            }
        }

        // Wire header dependencies: prefer precise tlog data, fall back to
        // conservative ClInclude if tlogs are missing.
        // Seed case cache: graph.Files (binlog-derived) + ClInclude items (correct casing).
        // This avoids filesystem lookups for tlog ALLCAPS paths.
        graph.SeedCaseCache();
        foreach (var addItem in build.FindChildrenRecursive<AddItem>(ai => ai.Name == "ClInclude"))
        {
            var eval = addItem.GetNearestParent<ProjectEvaluation>();
            var pd = eval?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(eval.ProjectFile)) ?? "" : "";
            foreach (var item in addItem.Children.OfType<Item>())
            {
                var abs = ResolveAbsolute(pd, item.Text);
                var rel = Path.GetRelativePath(graph.RootDir, abs);
                graph.PrimeCaseCacheEntry(abs, rel);
            }
        }
        var tlogWired = WireTlogHeaders(graph, objDirsByProject, clCmdByAbsSource);
        if (!tlogWired)
        {
            Console.Error.WriteLine($"{Clr.Yellow}warning:{Clr.Reset} tlog files not found; using conservative ClInclude header tracking");
            // Conservative fallback: each ClInclude header feeds every CL
            // command in its project.
            foreach (var addItem in build.FindChildrenRecursive<AddItem>(ai => ai.Name == "ClInclude"))
            {
                var eval = addItem.GetNearestParent<ProjectEvaluation>();
                var proj = eval?.Name ?? "unknown";
                var projDir2 = eval?.ProjectFile != null
                    ? Path.GetDirectoryName(Path.GetFullPath(eval.ProjectFile)) ?? ""
                    : "";

                if (!clCmdsByProject.TryGetValue(proj, out var projCmds)) continue;

                foreach (var item in addItem.Children.OfType<Item>())
                {
                    var headerPath = graph.ToRelative(ResolveAbsolute(projDir2, item.Text));
                    graph.Files.TryAdd(headerPath, new FileNode(headerPath, FileKinds.Classify(headerPath)));

                    foreach (var cmdId in projCmds)
                    {
                        graph.AddConsumer(headerPath, cmdId);
                        if (graph.Commands.TryGetValue(cmdId, out var cmd) && !cmd.Inputs.Contains(headerPath, StringComparer.OrdinalIgnoreCase))
                            cmd.Inputs.Add(headerPath);
                    }
                }
            }
        }

        // CompileXaml: .xaml → generated .xaml.g.h, .g.cpp, .xbf
        // Structured in the binlog: XamlPages/XamlApplications as inputs,
        // _GeneratedCodeFiles/_GeneratedXbfFiles as outputs.
        // Split into 1:1 commands per .xaml file (like CL) to avoid N×N edges.
        // Shared outputs (XamlTypeInfo.g.cpp etc.) go in a separate command.
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "CompileXaml"))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";
            var target = task.GetNearestParent<Target>()?.Name ?? "unknown";
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf == null) continue;

            // Gather input .xaml files
            var xamlInputs = new List<string>();
            foreach (var paramName in new[] { "XamlPages", "XamlApplications" })
            {
                var items = pf.Children.OfType<Parameter>()
                    .FirstOrDefault(p => p.Name == paramName)
                    ?.Children.OfType<Item>()
                    .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)));
                if (items != null) xamlInputs.AddRange(items);
            }
            if (xamlInputs.Count == 0) continue;

            // Gather output files from OutputItems
            var of = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "OutputItems");
            var allOutputs = new List<string>();
            if (of != null)
            {
                foreach (var paramName in new[] { "_GeneratedCodeFiles", "_GeneratedXbfFiles" })
                {
                    var items = of.Children.OfType<NamedNode>()
                        .Where(n => n.Name == paramName)
                        .SelectMany(n => n.Children.OfType<Item>())
                        .Select(i => graph.ToRelative(ResolveAbsolute(projDir, i.Text)));
                    allOutputs.AddRange(items);
                }
            }
            if (allOutputs.Count == 0) continue;

            // Match each .xaml to its outputs by stem (e.g. MainWindow.xaml → MainWindow.xaml.g.h + MainWindow.xbf)
            var sharedOutputs = new List<string>(allOutputs);
            foreach (var xaml in xamlInputs)
            {
                var stem = Path.GetFileNameWithoutExtension(xaml); // "MainWindow.xaml" → "MainWindow"
                var fullStem = Path.GetFileName(xaml);              // "MainWindow.xaml"
                var matched = allOutputs
                    .Where(o => {
                        var fn = Path.GetFileName(o);
                        return fn.StartsWith(fullStem + ".", StringComparison.OrdinalIgnoreCase)
                            || fn.StartsWith(stem + ".xbf", StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                if (matched.Count == 0) continue;

                foreach (var m in matched) sharedOutputs.Remove(m);

                var cmdId = $"CompileXaml#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "CompileXaml", proj, target, [xaml], matched);
                graph.Commands[cmdId] = cmd;
                graph.Files.TryAdd(xaml, new FileNode(xaml, FileKinds.Classify(xaml)));
                graph.AddConsumer(xaml, cmdId);
                foreach (var output in matched)
                {
                    graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                    graph.FileToProducer.TryAdd(output, cmdId);
                }
            }

            // Shared outputs (XamlTypeInfo.g.cpp, XamlLibMetadataProvider.g.cpp, etc.)
            // are produced from all .xaml inputs collectively.
            if (sharedOutputs.Count > 0)
            {
                var cmdId = $"CompileXaml#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "CompileXaml", proj, target, xamlInputs, sharedOutputs);
                graph.Commands[cmdId] = cmd;
                foreach (var input in xamlInputs)
                {
                    graph.Files.TryAdd(input, new FileNode(input, FileKinds.Classify(input)));
                    graph.AddConsumer(input, cmdId);
                }
                foreach (var output in sharedOutputs)
                {
                    graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                    graph.FileToProducer.TryAdd(output, cmdId);
                }
            }
        }

        // Convention: cppwinrt generates .g.h/.g.cpp from .winmd produced by MIDL.
        // The cppwinrt step is an unstructured Exec task, so we infer outputs by
        // naming convention: {name}.idl → MIDL → {name}.winmd, and cppwinrt
        // produces Generated Files\{name}.g.h + .g.cpp in the same project dir.
        foreach (var cmd in graph.Commands.Values.Where(c => c.Tool == "MIDL").ToList())
        {
            var projDir2 = build.FindChildrenRecursive<Project>(p => p.Name == cmd.Project)
                .FirstOrDefault()?.ProjectFile;
            if (projDir2 == null) continue;
            var absDir = Path.GetDirectoryName(Path.GetFullPath(projDir2)) ?? "";

            foreach (var idlPath in cmd.Inputs)
            {
                var stem = Path.GetFileNameWithoutExtension(idlPath);
                foreach (var ext in new[] { ".g.h", ".g.cpp" })
                {
                    var genPath = Path.Combine(absDir, "Generated Files", stem + ext);
                    if (!File.Exists(genPath)) continue;
                    var rel = graph.ToRelative(genPath);
                    graph.Files.TryAdd(rel, new FileNode(rel, FileKinds.Classify(rel)));
                    cmd.Outputs.Add(rel);
                    graph.FileToProducer.TryAdd(rel, cmd.Id);
                }
            }
        }

        // mdmerge: Exec tasks in CppWinRTMergeProjectWinMDInputs target.
        // Messages tell us input and output .winmd files.
        // "Processing input metadata file ARM64\Release\Unmerged\Foo.winmd."
        // "Validating metadata file ARM64\Release\Merged\Bar.winmd."
        foreach (var exec in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "Exec"
                 && (t.GetNearestParent<Target>()?.Name == "CppWinRTMergeProjectWinMDInputs")))
        {
            var projNode = exec.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir3 = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";

            var inputs = new List<string>();
            string? output = null;

            foreach (var msg in exec.FindChildrenRecursive<Message>())
            {
                var text = msg.Text ?? "";
                if (text.StartsWith("Processing input metadata file "))
                {
                    var rel = text["Processing input metadata file ".Length..].TrimEnd('.');
                    inputs.Add(graph.ToRelative(ResolveAbsolute(projDir3, rel)));
                }
                else if (text.StartsWith("Validating metadata file "))
                {
                    var rel = text["Validating metadata file ".Length..].TrimEnd('.');
                    output = graph.ToRelative(ResolveAbsolute(projDir3, rel));
                }
            }

            if (output != null && inputs.Count > 0)
            {
                var target = exec.GetNearestParent<Target>()?.Name ?? "unknown";
                var cmdId = $"mdmerge#{cmdIndex++}:{proj}/{target}";
                var cmd = new CommandNode(cmdId, "mdmerge", proj, target, inputs, [output]);
                graph.Commands[cmdId] = cmd;
                foreach (var inp in inputs) graph.AddConsumer(inp, cmdId);
                graph.Files.TryAdd(output, new FileNode(output, FileKinds.Classify(output)));
                graph.FileToProducer[output] = cmdId;
            }
        }

        // makepri: WinAppSdkGenerateProjectPriFile → resources.pri
        // The task indexes the whole project directory; we model it as a no-input
        // command producing .pri so it appears as a terminal node in the graph.
        foreach (var priTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateProjectPriFile"))
        {
            var projNode4 = priTask.GetNearestParent<Project>();
            var proj4 = projNode4?.Name ?? "unknown";
            var projDir4 = projNode4?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode4.ProjectFile)) ?? ""
                : "";
            var pf4 = priTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            var outFile = pf4?.FindChildrenRecursive<Property>(p => p.Name == "OutputFileName")
                .FirstOrDefault()?.Value;
            if (outFile == null) continue;

            var priRel = graph.ToRelative(ResolveAbsolute(projDir4, outFile));
            var target4 = priTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"makepri#{cmdIndex++}:{proj4}/{target4}";
            var cmd = new CommandNode(cmdId, "makepri", proj4, target4, [], [priRel]);
            graph.Commands[cmdId] = cmd;
            graph.Files.TryAdd(priRel, new FileNode(priRel, FileKinds.Classify(priRel)));
            graph.FileToProducer[priRel] = cmdId;
        }

        // AppxManifest: WinAppSdkGenerateAppxManifest
        // Package.appxmanifest → AppxManifest.xml
        foreach (var manTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateAppxManifest"))
        {
            var projNode5 = manTask.GetNearestParent<Project>();
            var proj5 = projNode5?.Name ?? "unknown";
            var projDir5 = projNode5?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode5.ProjectFile)) ?? ""
                : "";
            var pf5 = manTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf5 == null) continue;

            var inputItems = pf5.FindChildrenRecursive<Parameter>(p => p.Name == "AppxManifestInput")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => graph.ToRelative(ResolveAbsolute(projDir5, i.Text)))
                .ToList() ?? [];
            var outFile = pf5.FindChildrenRecursive<Property>(p => p.Name == "AppxManifestOutput")
                .FirstOrDefault()?.Value;
            if (outFile == null) continue;

            var outRel = graph.ToRelative(ResolveAbsolute(projDir5, outFile));
            var target5 = manTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"AppxManifest#{cmdIndex++}:{proj5}/{target5}";
            var cmd = new CommandNode(cmdId, "AppxManifest", proj5, target5, inputItems, [outRel]);
            graph.Commands[cmdId] = cmd;
            foreach (var inp in inputItems)
            {
                graph.Files.TryAdd(inp, new FileNode(inp, FileKinds.Classify(inp)));
                graph.AddConsumer(inp, cmdId);
            }
            graph.Files.TryAdd(outRel, new FileNode(outRel, FileKind.Output));
            graph.FileToProducer[outRel] = cmdId;
        }

        // Copy tasks: SourceFiles → DestinationFiles (1:1 parallel lists or scalar).
        // Only wire copies where the source is already tracked in the graph.
        foreach (var copyTask in build.FindChildrenRecursive<MSTask>(t => t.Name == "Copy"))
        {
            var projNode7 = copyTask.GetNearestParent<Project>();
            var proj7 = projNode7?.Name ?? "unknown";
            var projDir7 = projNode7?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode7.ProjectFile)) ?? ""
                : "";
            var pf7 = copyTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf7 == null) continue;

            // Collect source and dest lists (may be Parameter with Items, or scalar Property)
            var srcItems = pf7.FindChildrenRecursive<Parameter>(p => p.Name == "SourceFiles")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => i.Text).ToList();
            var dstItems = pf7.FindChildrenRecursive<Parameter>(p => p.Name == "DestinationFiles")
                .FirstOrDefault()?.Children.OfType<Item>()
                .Select(i => i.Text).ToList();

            // Scalar form: single Property instead of Parameter
            if (srcItems == null || srcItems.Count == 0)
            {
                var srcProp = pf7.FindChildrenRecursive<Property>(p => p.Name == "SourceFiles")
                    .FirstOrDefault()?.Value;
                var dstProp = pf7.FindChildrenRecursive<Property>(p => p.Name == "DestinationFiles")
                    .FirstOrDefault()?.Value;
                if (srcProp != null && dstProp != null)
                {
                    srcItems = [srcProp];
                    dstItems = [dstProp];
                }
                else continue;
            }
            if (dstItems == null || srcItems.Count != dstItems.Count) continue;

            var target7 = copyTask.GetNearestParent<Target>()?.Name ?? "unknown";

            for (int i = 0; i < srcItems.Count; i++)
            {
                var srcRel = graph.ToRelative(ResolveAbsolute(projDir7, srcItems[i]));
                var dstRel = graph.ToRelative(ResolveAbsolute(projDir7, dstItems[i]));
                // Only wire if source is already in the graph
                if (!graph.Files.ContainsKey(srcRel)) continue;
                // Skip identity copies
                if (string.Equals(srcRel, dstRel, StringComparison.OrdinalIgnoreCase)) continue;

                var cmdId = $"Copy#{cmdIndex++}:{proj7}/{target7}";
                var cmd = new CommandNode(cmdId, "Copy", proj7, target7, [srcRel], [dstRel]);
                graph.Commands[cmdId] = cmd;
                graph.AddConsumer(srcRel, cmdId);
                graph.Files.TryAdd(dstRel, new FileNode(dstRel, FileKinds.Classify(dstRel)));
                graph.FileToProducer.TryAdd(dstRel, cmdId);
            }
        }
        // AppxPackageRecipe: WinAppSdkGenerateAppxPackageRecipe
        // Gathers payload (.exe, .winmd, .pri, AppxManifest, assets) → .build.appxrecipe
        // We connect only payload items already tracked in the graph as inputs.
        foreach (var recipeTask in build.FindChildrenRecursive<MSTask>(
            t => t.Name == "WinAppSdkGenerateAppxPackageRecipe"))
        {
            var projNode6 = recipeTask.GetNearestParent<Project>();
            var proj6 = projNode6?.Name ?? "unknown";
            var projDir6 = projNode6?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode6.ProjectFile)) ?? ""
                : "";
            var pf6 = recipeTask.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf6 == null) continue;

            var recipeFile = pf6.FindChildrenRecursive<Property>(p => p.Name == "RecipeFile")
                .FirstOrDefault()?.Value;
            if (recipeFile == null) continue;

            var recipeRel = graph.ToRelative(ResolveAbsolute(projDir6, recipeFile));

            // Gather inputs: AppxManifestXml + PayloadFiles that are already in the graph
            var inputs = new List<string>();
            var manifest = pf6.FindChildrenRecursive<Property>(p => p.Name == "AppxManifestXml")
                .FirstOrDefault()?.Value;
            if (manifest != null)
            {
                var mRel = graph.ToRelative(ResolveAbsolute(projDir6, manifest));
                if (graph.Files.ContainsKey(mRel)) inputs.Add(mRel);
            }
            var payload = pf6.FindChildrenRecursive<Parameter>(p => p.Name == "PayloadFiles")
                .FirstOrDefault()?.Children.OfType<Item>().ToList() ?? [];
            foreach (var item in payload)
            {
                var rel = graph.ToRelative(ResolveAbsolute(projDir6, item.Text));
                if (graph.Files.ContainsKey(rel)) inputs.Add(rel);
            }

            var target6 = recipeTask.GetNearestParent<Target>()?.Name ?? "unknown";
            var cmdId = $"AppxRecipe#{cmdIndex++}:{proj6}/{target6}";
            var cmd = new CommandNode(cmdId, "AppxRecipe", proj6, target6, inputs, [recipeRel]);
            graph.Commands[cmdId] = cmd;
            foreach (var inp in inputs) graph.AddConsumer(inp, cmdId);
            graph.Files.TryAdd(recipeRel, new FileNode(recipeRel, FileKinds.Classify(recipeRel)));
            graph.FileToProducer[recipeRel] = cmdId;
        }


        return graph;
    }

    /// Parse CL.read.1.tlog files to wire precise header → source dependencies.
    /// Creates synthetic #include commands: .h → [#include] → .cpp
    /// so the graph reads .h → .cpp → .obj (not .h → .obj directly).
    /// Returns true if at least one tlog was found and parsed.
    static bool WireTlogHeaders(BuildGraph graph,
        Dictionary<string, string> objDirsByProject,
        Dictionary<string, string> clCmdByAbsSource)
    {
        bool foundAny = false;
        int inclIndex = 0;

        // Build reverse map: absSource → relative source path
        var absToRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abs, _) in clCmdByAbsSource)
        {
            var rel = graph.ToRelative(abs);
            absToRel[abs] = rel;
        }

        // Track which (header, source) pairs we've already wired to avoid duplication
        var wired = new HashSet<(string header, string source)>();

        foreach (var (proj, absObjDir) in objDirsByProject)
        {
            // Find *.tlog subdirectories under the obj dir
            if (!Directory.Exists(absObjDir)) continue;
            var tlogDirs = Directory.GetDirectories(absObjDir, "*.tlog");
            if (tlogDirs.Length == 0)
            {
                // tlog might be one level up (e.g., XaBench\ARM64\Debug\XaBench.tlog)
                var parent = Path.GetDirectoryName(absObjDir);
                if (parent != null) tlogDirs = Directory.GetDirectories(parent, "*.tlog");
            }

            foreach (var tlogDir in tlogDirs)
            {
                var readTlog = Path.Combine(tlogDir, "CL.read.1.tlog");
                if (!File.Exists(readTlog)) continue;
                foundAny = true;

                string? currentSourceRel = null;
                foreach (var line in File.ReadLines(readTlog))
                {
                    if (line.StartsWith('^'))
                    {
                        var absSource = line[1..];
                        currentSourceRel = absToRel.GetValueOrDefault(absSource);
                        continue;
                    }

                    if (currentSourceRel == null) continue;

                    // Only track headers under the solution root
                    if (!line.StartsWith(graph.RootDir, StringComparison.OrdinalIgnoreCase)) continue;

                    var headerRel = graph.ToRelative(line);
                    var ext = Path.GetExtension(headerRel);
                    if (!IsHeader(ext) && !IsGeneratedSource(ext)) continue;

                    // Deduplicate: same header→source pair from multiple tlog entries
                    if (!wired.Add((headerRel, currentSourceRel))) continue;

                    graph.Files.TryAdd(headerRel, new FileNode(headerRel, FileKinds.Classify(headerRel)));

                    // Create synthetic #include command: header → source
                    var cmdId = $"#include#{inclIndex++}";
                    var cmd = new CommandNode(cmdId, "#include", "", "", [headerRel], [currentSourceRel]);
                    graph.Commands[cmdId] = cmd;
                    graph.AddConsumer(headerRel, cmdId);
                    // Don't set FileToProducer for the source — it's a real source file,
                    // not generated. Multiple headers can feed the same source.
                }
            }
        }

        return foundAny;

        static bool IsHeader(string ext) =>
            ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase);

        // .g.h, .g.cpp files produced by cppwinrt are #included by some sources
        static bool IsGeneratedSource(string ext) =>
            ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase);
    }

    /// Resolve a potentially relative path to absolute using a base directory.
    static string ResolveAbsolute(string baseDir, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        if (string.IsNullOrEmpty(baseDir)) return path;
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    /// Find the longest common ancestor directory of a set of paths.
    static string GetCommonAncestor(List<string> dirs)
    {
        if (dirs.Count == 0) return "";
        if (dirs.Count == 1) return dirs[0];

        var parts = dirs[0].Split(Path.DirectorySeparatorChar);
        int commonLen = parts.Length;

        for (int i = 1; i < dirs.Count; i++)
        {
            var other = dirs[i].Split(Path.DirectorySeparatorChar);
            commonLen = Math.Min(commonLen, other.Length);
            for (int j = 0; j < commonLen; j++)
            {
                if (!string.Equals(parts[j], other[j], StringComparison.OrdinalIgnoreCase))
                {
                    commonLen = j;
                    break;
                }
            }
        }

        return string.Join(Path.DirectorySeparatorChar, parts[..commonLen]);
    }
}

static class Dot
{
    static int _nextId;
    static readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public static string Id(string key)
    {
        if (!_ids.TryGetValue(key, out var id))
        {
            id = $"n{_nextId++}";
            _ids[key] = id;
        }
        return id;
    }

    public static string Escape(string label) =>
        label.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// ANSI colour helpers — respects --color auto|always|never.
static class Clr
{
    static bool _enabled = !Console.IsOutputRedirected;

    public static void SetMode(string mode) => _enabled = mode switch
    {
        "always" => true,
        "never"  => false,
        _        => !Console.IsOutputRedirected   // "auto"
    };

    public static string Reset   => _enabled ? "\x1b[0m"  : "";
    public static string Bold    => _enabled ? "\x1b[1m"  : "";
    public static string Red     => _enabled ? "\x1b[31m" : "";
    public static string Green   => _enabled ? "\x1b[32m" : "";
    public static string Yellow  => _enabled ? "\x1b[33m" : "";
    public static string Blue    => _enabled ? "\x1b[34m" : "";
    public static string Magenta => _enabled ? "\x1b[35m" : "";
    public static string Cyan    => _enabled ? "\x1b[36m" : "";
    public static string Dim     => _enabled ? "\x1b[2m"  : "";
}

// ============================================================
// Graph cache — JSON serialization with staleness detection
// ============================================================

/// Serializable representation of the build graph.
class GraphCache
{
    public long BinlogTimestamp { get; set; }
    public string RootDir { get; set; } = "";
    public List<CachedFile> Files { get; set; } = [];
    public List<CachedCommand> Commands { get; set; } = [];

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(string path, BuildGraph graph, DateTime binlogStamp)
    {
        var cache = new GraphCache
        {
            BinlogTimestamp = binlogStamp.Ticks,
            RootDir = graph.RootDir,
            Files = graph.Files.Values.Select(f => new CachedFile { Path = f.Path, Kind = (int)f.Kind }).ToList(),
            Commands = graph.Commands.Values.Select(c => new CachedCommand
            {
                Id = c.Id, Tool = c.Tool, Project = c.Project, Target = c.Target,
                Inputs = c.Inputs, Outputs = c.Outputs
            }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOpts));
    }

    public static GraphCache? Load(string path)
        => JsonSerializer.Deserialize<GraphCache>(File.ReadAllText(path), JsonOpts);

    public BuildGraph ToGraph()
    {
        var graph = new BuildGraph { RootDir = RootDir };
        foreach (var f in Files)
            graph.Files.TryAdd(f.Path, new FileNode(f.Path, (FileKind)f.Kind));
        foreach (var c in Commands)
        {
            var cmd = new CommandNode(c.Id, c.Tool, c.Project, c.Target, c.Inputs, c.Outputs);
            graph.Commands[cmd.Id] = cmd;
            foreach (var input in cmd.Inputs)
                graph.AddConsumer(input, cmd.Id);
            foreach (var output in cmd.Outputs)
                graph.FileToProducer[output] = cmd.Id;
        }
        return graph;
    }
}

class CachedFile
{
    public string Path { get; set; } = "";
    public int Kind { get; set; }
}

class CachedCommand
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Project { get; set; } = "";
    public string Target { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
}
