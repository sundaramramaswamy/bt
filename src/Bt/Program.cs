using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

// Optional --binlog <path> before the subcommand
var binlogPath = Path.GetFullPath("msbuild.binlog");
int cmdStart = 0;
if (args[0] == "--binlog" && args.Length >= 2)
{
    binlogPath = Path.GetFullPath(args[1]);
    cmdStart = 2;
}
else if (args[0].StartsWith("--binlog="))
{
    binlogPath = Path.GetFullPath(args[0]["--binlog=".Length..]);
    cmdStart = 1;
}

if (cmdStart >= args.Length)
{
    PrintUsage();
    return 1;
}

var graph = LoadGraph(binlogPath);

return args[cmdStart] switch
{
    "graph" => ShowGraph(graph),
    "outputs-of" => OutputsOf(graph, args[(cmdStart + 1)..]),
    "sources-of" => SourcesOf(graph, args[(cmdStart + 1)..]),
    _ => Error($"Unknown command: {args[cmdStart]}")
};

// ============================================================
// Commands
// ============================================================

static int ShowGraph(BuildGraph g)
{
    // Developer-centric graph: sources and outputs are visible.
    // Intermediates (.obj, .pch) are collapsed — edges skip through them.
    var visible = g.Files.Values.Where(f => FileKinds.IsDevVisible(f.Kind)).ToList();

    // Walk forward from each source to find which visible outputs it reaches,
    // and walk backward from each output to find which visible sources feed it.
    var edges = new HashSet<(string src, string output)>();
    foreach (var src in visible.Where(f => f.Kind == FileKind.Source))
        foreach (var output in g.GetOutputsOf(src.Path))
            edges.Add((src.Path, output));

    Console.WriteLine("digraph build {");
    Console.WriteLine("  rankdir=LR;");
    Console.WriteLine("  node [fontname=\"Consolas\" fontsize=10];");
    Console.WriteLine();

    var nodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (s, o) in edges) { nodeSet.Add(s); nodeSet.Add(o); }

    foreach (var path in nodeSet)
    {
        if (!g.Files.TryGetValue(path, out var f)) continue;
        var shape = f.Kind == FileKind.Source ? "note" : "box3d";
        Console.WriteLine($"  {Dot.Id(path)} [label=\"{Dot.Escape(path)}\" shape={shape}];");
    }
    Console.WriteLine();

    foreach (var (s, o) in edges)
        Console.WriteLine($"  {Dot.Id(s)} -> {Dot.Id(o)};");

    Console.WriteLine("}");
    return 0;
}

static int OutputsOf(BuildGraph g, string[] files)
{
    if (files.Length == 0) return Error("Usage: bt outputs-of <file> [file...]");

    foreach (var file in files)
    {
        var resolved = ResolveFileArg(g, file);
        if (resolved == null) continue;

        var outputs = g.GetOutputsOf(resolved);
        Console.WriteLine($"{file}:");
        foreach (var o in outputs)
            Console.WriteLine($"  → {g.ToAbsolute(o)}");
    }
    return 0;
}

static int SourcesOf(BuildGraph g, string[] files)
{
    if (files.Length == 0) return Error("Usage: bt sources-of <file> [file...]");

    foreach (var file in files)
    {
        var resolved = ResolveFileArg(g, file);
        if (resolved == null) continue;

        var sources = g.GetSourcesOf(resolved);
        Console.WriteLine($"{file}:");
        foreach (var s in sources)
            Console.WriteLine($"  ← {g.ToAbsolute(s)}");
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
        Console.Error.WriteLine($"{arg}: ambiguous — matches {matches.Count} files:");
        foreach (var m in matches) Console.Error.WriteLine($"  {m}");
        return null;
    }
    Console.Error.WriteLine($"{arg}: not found in graph");
    return null;
}

static BuildGraph LoadGraph(string binlogPath)
{
    if (!File.Exists(binlogPath))
    {
        Console.Error.WriteLine($"binlog not found: {binlogPath}");
        Console.Error.WriteLine("Run a full build with: msbuild /bl");
        Environment.Exit(1);
    }
    var build = BinaryLog.ReadBuild(binlogPath);
    return BuildGraph.FromBinlog(build);
}

static int Error(string msg) { Console.Error.WriteLine(msg); return 1; }

static void PrintUsage()
{
    Console.WriteLine("""
    bt — MSBuild dependency graph explorer

    Usage:  bt [--binlog <path>] <command> [args]

    Options:
      --binlog <path>       Path to .binlog file (default: msbuild.binlog)

    Commands:
      graph                 Show graph summary (files, commands, outputs)
      outputs-of <file>     What outputs get built when <file> changes?
      sources-of <file>     What source files feed into <file>?

    Generate a binlog with: msbuild /bl
    """);
}

// ============================================================
// Data model
// ============================================================

enum FileKind { Source, Intermediate, Output }

static class FileKinds
{
    static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
        { ".cpp", ".cc", ".cxx", ".c", ".cs", ".idl", ".xaml", ".rc", ".res" };
    static readonly HashSet<string> HeaderExts = new(StringComparer.OrdinalIgnoreCase)
        { ".h", ".hpp", ".hxx" };
    static readonly HashSet<string> OutputExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".lib", ".winmd" };
    static readonly HashSet<string> IntermediateExts = new(StringComparer.OrdinalIgnoreCase)
        { ".obj", ".pch", ".res" };

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

    /// Convert an absolute path to root-relative.
    public string ToRelative(string absolutePath) =>
        Path.GetRelativePath(RootDir, absolutePath);

    /// Given a source file, find all final outputs reachable through the graph.
    public HashSet<string> GetOutputsOf(string sourcePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkForward(sourcePath, visited, result);
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
            // Leaf: no command consumes this file → it's a final output
            if (Files.TryGetValue(filePath, out var f) && f.Kind != FileKind.Source)
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

        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name is "CL" or "Link" or "Lib"))
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

            if (sources.Count == 0) continue;

            if (toolName == "CL")
            {
                // CL batches N sources but the relationship is 1:1 (each .cpp → its .obj).
                // Split into individual commands to keep the graph accurate.
                var objDir = pf.FindChildrenRecursive<Property>(p => p.Name == "ObjectFileName")
                    .FirstOrDefault()?.Value ?? "";
                var absObjDir = ResolveAbsolute(projDir, objDir);

                foreach (var src in sources)
                {
                    var obj = graph.ToRelative(Path.Combine(absObjDir,
                        Path.GetFileNameWithoutExtension(src) + ".obj"));
                    var cmdId = $"CL#{cmdIndex++}:{proj}/{target}";
                    var cmd = new CommandNode(cmdId, "CL", proj, target, [src], [obj]);
                    graph.Commands[cmdId] = cmd;
                    graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                    graph.Files.TryAdd(obj, new FileNode(obj, FileKind.Intermediate));
                    graph.AddConsumer(src, cmdId);
                    graph.FileToProducer[obj] = cmdId;
                }
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

        return graph;
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
