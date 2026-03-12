using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

var binlogPath = args.Length > 0 ? args[0] : @"..\..\msbuild.binlog";

// --- Parse binlog into BuildGraph ---
var build = BinaryLog.ReadBuild(binlogPath);
var graph = BuildGraph.FromBinlog(build);

// --- Demo queries ---
Console.WriteLine($"Graph: {graph.Files.Count} files, {graph.Commands.Count} commands\n");

// 1. Source → targets: given a source, what outputs get built?
Console.WriteLine("=== Source → Targets ===");
foreach (var src in graph.Files.Values.Where(f => f.Kind == FileKind.Source).Take(5))
{
    var targets = graph.GetOutputsOf(src.Path);
    Console.WriteLine($"  {src.Path} → {string.Join(", ", targets.Select(Path.GetFileName))}");
}

// 2. Target → sources: given an output, what sources feed it?
Console.WriteLine("\n=== Target → Sources ===");
foreach (var output in graph.Files.Values.Where(f => f.Kind == FileKind.Output))
{
    var sources = graph.GetSourcesOf(output.Path);
    Console.WriteLine($"  {Path.GetFileName(output.Path)} ← {sources.Count} sources");
    foreach (var s in sources.Take(5))
        Console.WriteLine($"    {s}");
    if (sources.Count > 5)
        Console.WriteLine($"    ... and {sources.Count - 5} more");
}

// 3. Show all commands
Console.WriteLine("\n=== Commands ===");
foreach (var cmd in graph.Commands.Values)
    Console.WriteLine($"  {cmd.Id}: {cmd.Inputs.Count} inputs → {cmd.Outputs.Count} outputs");

// ============================================================
// Data model
// ============================================================

enum FileKind { Source, Intermediate, Output }

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
    public Dictionary<string, FileNode> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CommandNode> Commands { get; } = [];

    // file → commands that consume it
    public Dictionary<string, List<string>> FileToConsumers { get; } = new(StringComparer.OrdinalIgnoreCase);
    // file → command that produces it
    public Dictionary<string, string> FileToProducer { get; } = new(StringComparer.OrdinalIgnoreCase);

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
            if (Files.TryGetValue(filePath, out var f) && f.Kind == FileKind.Source)
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
        var graph = new BuildGraph();
        int cmdIndex = 0;

        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name is "CL" or "Link" or "Lib"))
        {
            var projNode = task.GetNearestParent<Project>();
            var proj = projNode?.Name ?? "unknown";
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(projNode.ProjectFile) ?? ""
                : "";
            var target = task.GetNearestParent<Target>()?.Name ?? "unknown";
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            if (pf == null) continue;

            var toolName = task.Name;
            // Resolve all source paths relative to project directory
            var sources = pf.Children.OfType<Parameter>()
                .FirstOrDefault(p => p.Name == "Sources")
                ?.Children.OfType<Item>()
                .Select(i => ResolvePath(projDir, i.Text))
                .ToList() ?? [];

            if (sources.Count == 0) continue;

            List<string> outputs;
            FileKind outputKind;

            if (toolName == "CL")
            {
                var objDir = pf.FindChildrenRecursive<Property>(p => p.Name == "ObjectFileName")
                    .FirstOrDefault()?.Value ?? "";
                var absObjDir = ResolvePath(projDir, objDir);
                outputs = sources.Select(s =>
                    Path.Combine(absObjDir, Path.GetFileNameWithoutExtension(s) + ".obj")).ToList();
                outputKind = FileKind.Intermediate;
            }
            else // Link or Lib
            {
                var outFile = pf.FindChildrenRecursive<Property>(p => p.Name == "OutputFile")
                    .FirstOrDefault()?.Value ?? "";
                outFile = string.IsNullOrEmpty(outFile) ? "" : ResolvePath(projDir, outFile);
                outputs = string.IsNullOrEmpty(outFile) ? [] : [outFile];
                outputKind = FileKind.Output;
            }

            var cmdId = $"{toolName}#{cmdIndex++}:{proj}/{target}";
            var cmd = new CommandNode(cmdId, toolName, proj, target, sources, outputs);
            graph.Commands[cmdId] = cmd;

            // Register files
            var inputKind = toolName == "CL" ? FileKind.Source : FileKind.Intermediate;
            foreach (var input in sources)
            {
                graph.Files.TryAdd(input, new FileNode(input, inputKind));
                if (!graph.FileToConsumers.TryGetValue(input, out var list))
                {
                    list = [];
                    graph.FileToConsumers[input] = list;
                }
                list.Add(cmdId);
            }
            foreach (var output in outputs)
            {
                graph.Files.TryAdd(output, new FileNode(output, outputKind));
                graph.FileToProducer[output] = cmdId;
            }
        }

        return graph;
    }

    static string ResolvePath(string baseDir, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        if (string.IsNullOrEmpty(baseDir)) return path;
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }
}
