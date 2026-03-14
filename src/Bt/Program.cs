using System.CommandLine;
using System.IO.Compression;
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
var outputsOfCmd = new Command("bins", "Downstream dependency tree from <file>");
outputsOfCmd.Add(outputsFilesArg);

var sourcesFilesArg = new Argument<string[]>("files") { Description = "Output files to query", Arity = ArgumentArity.OneOrMore };
var sourcesOfCmd = new Command("srcs", "List all upstream files that feed into <file>");
sourcesOfCmd.Add(sourcesFilesArg);

var affectedFilesArg = new Argument<string[]>("files") { Description = "Changed files (default: git diff)", Arity = ArgumentArity.ZeroOrMore };
var affectedCmd = new Command("dirty", "Build plan for changed files");
affectedCmd.Add(affectedFilesArg);

var buildFilesArg = new Argument<string[]>("files") { Description = "Changed files (default: git diff)", Arity = ArgumentArity.ZeroOrMore };
var buildJobsOption = new Option<int>("-j") { Description = "Max parallel jobs (default: CPU cores)" };
buildJobsOption.DefaultValueFactory = _ => Environment.ProcessorCount;
var buildDryRunOption = new Option<bool>("--dry-run") { Description = "Print commands without executing" };
buildDryRunOption.Aliases.Add("-n");
var buildCmd = new Command("build", "Build only what's dirty");
buildCmd.Add(buildFilesArg);
buildCmd.Add(buildJobsOption);
buildCmd.Add(buildDryRunOption);

var compileCommandsOutputOption = new Option<string>("-o") { Description = "Output file (default: compile_commands.json in repo root)" };
var compileCommandsCmd = new Command("compiledb", "Generate compile_commands.json for clangd/clang-tidy");
compileCommandsCmd.Add(compileCommandsOutputOption);

// -- Wire up --
var root = new RootCommand("bt — MSBuild dependency graph explorer");
root.Add(binlogOption);
root.Add(colorOption);
root.Add(graphCmd);
root.Add(outputsOfCmd);
root.Add(sourcesOfCmd);
root.Add(affectedCmd);
root.Add(buildCmd);
root.Add(compileCommandsCmd);

// Custom coloured help — runs before System.CommandLine's default help
var btVersion = System.Reflection.Assembly.GetExecutingAssembly()
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
    ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
// Short version for help banner:
// Published: 1.0.0-ci.62.1c145d8+fullhash → strip +hash (already in suffix)
// Dev build: 1.0.0+fullhash → trim hash to 7 chars
var plusIdx = btVersion.IndexOf('+');
var btVersionShort = plusIdx < 0 ? btVersion
    : btVersion.Contains("-ci.") ? btVersion[..plusIdx]
    : btVersion[..Math.Min(btVersion.Length, plusIdx + 8)];

if (args.Length == 1 && args[0] is "--version")
{
    Console.WriteLine(btVersion);
    return 0;
}
if (args.Length == 0 || args.Any(a => a is "-?" or "-h" or "--help"))
{
    // Only colourize top-level help; let subcommand -? use defaults
    if (args.Length == 0 || !args.Any(a => a is "graph" or "bins" or "srcs" or "dirty" or "build" or "compiledb"))
    {
        Clr.SetMode("auto");
        Console.Error.WriteLine($"""

        {Clr.Bold}bt{Clr.Reset} {Clr.Dim}{btVersionShort}{Clr.Reset} — MSBuild dependency graph explorer

        {Clr.Yellow}Usage:{Clr.Reset}  bt [command] [options]

        {Clr.Yellow}Commands:{Clr.Reset}
          {Clr.Cyan}graph{Clr.Reset}              Emit Graphviz DOT dependency graph
          {Clr.Cyan}bins{Clr.Reset} <files>       Downstream dependency tree
          {Clr.Cyan}srcs{Clr.Reset} <files>       Upstream dependency tree
          {Clr.Cyan}dirty{Clr.Reset} [files]      Build plan (mtime-based, or explicit files)
          {Clr.Cyan}build{Clr.Reset} [files]      Build only what's dirty (-j N, --dry-run)
          {Clr.Cyan}compiledb{Clr.Reset}          Generate compile_commands.json (-o path)

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>    Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>    auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}
          {Clr.Green}--version{Clr.Reset}          Show version
          {Clr.Green}-?, --help{Clr.Reset}         Show this help

        {Clr.Yellow}Graph filters:{Clr.Reset}
          {Clr.Green}-f, --file{Clr.Reset} <path>     Subgraph reachable from/to file
          {Clr.Green}-p, --project{Clr.Reset} <name>  Only nodes from project

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt graph | dot -Tsvg -o build.svg{Clr.Reset}
          {Clr.Dim}bt graph -f TestDataItem.h{Clr.Reset}
          {Clr.Dim}bt bins TestDataItem.h{Clr.Reset}
          {Clr.Dim}bt srcs XaBench.exe{Clr.Reset}
          {Clr.Dim}bt dirty{Clr.Reset}
          {Clr.Dim}bt dirty src/Foo.cpp src/Bar.h{Clr.Reset}
          {Clr.Dim}bt build{Clr.Reset}
          {Clr.Dim}bt build -j 4 src/Foo.cpp{Clr.Reset}
          {Clr.Dim}bt build --dry-run{Clr.Reset}
          {Clr.Dim}bt compiledb{Clr.Reset}
        """);
        return 0;
    }
}

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

affectedCmd.SetAction(result =>
{
    var g = Setup(result);
    var explicitFiles = result.GetValue(affectedFilesArg) ?? [];
    return Affected(g, explicitFiles);
});

buildCmd.SetAction(result =>
{
    var g = Setup(result);
    var explicitFiles = result.GetValue(buildFilesArg) ?? [];
    var maxJobs = result.GetValue(buildJobsOption);
    var dryRun = result.GetValue(buildDryRunOption);
    return RunBuild(g, explicitFiles, maxJobs, dryRun);
});

compileCommandsCmd.SetAction(result =>
{
    var g = Setup(result);
    var outPath = result.GetValue(compileCommandsOutputOption);
    return CompileCommands(g, outPath);
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
        Console.WriteLine($"{Clr.Cyan}{resolved}{Clr.Reset}");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PrintTreeForward(g, resolved, "", true, seen);
    }
    return 0;
}

static int SourcesOf(BuildGraph g, string[] files)
{
    foreach (var file in files)
    {
        var resolved = ResolveFileArg(g, file);
        if (resolved == null) continue;
        Console.WriteLine($"{Clr.Cyan}{resolved}{Clr.Reset}");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PrintTreeBackward(g, resolved, "", true, seen);
    }
    return 0;
}

static string FileClr(BuildGraph g, string path) =>
    g.Files.TryGetValue(path, out var f) ? f.Kind switch
    {
        FileKind.Source => Clr.Green,
        FileKind.Output => Clr.Yellow,
        _ => Clr.Dim
    } : Clr.Dim;

static void PrintTreeForward(BuildGraph g, string filePath, string indent, bool last, HashSet<string> seen, HashSet<string>? dirtyIds = null)
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

static void PrintTreeBackward(BuildGraph g, string filePath, string indent, bool last, HashSet<string> seen)
{
    if (!g.FileToProducer.TryGetValue(filePath, out var producerId)) return;
    if (!g.Commands.TryGetValue(producerId, out var cmd)) return;

    var inputs = cmd.Inputs.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
    for (int i = 0; i < inputs.Count; i++)
    {
        var input = inputs[i];
        var isLast = i == inputs.Count - 1;
        var branch = isLast ? "└── " : "├── ";
        var cont   = isLast ? "    " : "│   ";

        if (!seen.Add(input))
        {
            Console.WriteLine($"{indent}{branch}{Clr.Dim}[{cmd.Tool}] {input} (↑ above){Clr.Reset}");
            continue;
        }
        Console.WriteLine($"{indent}{branch}{Clr.Dim}[{cmd.Tool}]{Clr.Reset} {FileClr(g, input)}{input}{Clr.Reset}");
        PrintTreeBackward(g, input, indent + cont, isLast, seen);
    }
}

static int Affected(BuildGraph g, string[] explicitFiles)
{
    List<CommandNode> plan;

    if (explicitFiles.Length > 0)
    {
        // Explicit files: resolve and walk forward
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in explicitFiles)
        {
            var r = ResolveFileArg(g, arg);
            if (r != null) resolved.Add(r);
        }
        if (resolved.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Dim}No files found in graph.{Clr.Reset}");
            return 0;
        }
        Console.Error.WriteLine($"{Clr.Dim}Explicit files ({resolved.Count}):{Clr.Reset}");
        foreach (var f in resolved.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
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
        plan = mtimePlan.Where(c => !string.IsNullOrEmpty(c.CommandLine)).ToList();

        if (plan.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
            return 0;
        }

        // Print a tree per dirty source, filtered to only dirty commands.
        // Also include synthetic (#include) commands that bridge to dirty commands.
        var dirtyIds = new HashSet<string>(plan.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in g.Commands.Values)
            if (cmd.Tool.StartsWith("#") && cmd.Outputs.Any(o =>
                g.FileToConsumers.TryGetValue(o, out var cids) && cids.Any(dirtyIds.Contains)))
                dirtyIds.Add(cmd.Id);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (src, _) in dirtySources.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{Clr.Red}{src}{Clr.Reset}");
            PrintTreeForward(g, src, "", true, seen, dirtyIds);
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

static int CompileCommands(BuildGraph g, string? outputPath)
{
    var entries = g.Commands.Values
        .Where(c => c.Tool == "CL" && !string.IsNullOrEmpty(c.CommandLine))
        .Select(c =>
        {
            var file = c.Inputs.Count > 0
                ? Path.GetFullPath(Path.Combine(g.RootDir, c.Inputs[0]))
                : "";
            var dir = !string.IsNullOrEmpty(c.WorkingDir) ? c.WorkingDir : g.RootDir;
            return new CompileCommandEntry { Directory = dir, Command = c.CommandLine, File = file };
        })
        .ToList();

    var outFile = outputPath ?? Path.Combine(g.RootDir, "compile_commands.json");
    using var fs = File.Create(outFile);
    using var writer = new System.Text.Json.Utf8JsonWriter(fs, new System.Text.Json.JsonWriterOptions
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    JsonSerializer.Serialize(writer, entries, CompileDbJsonContext.Default.ListCompileCommandEntry);
    Console.Error.WriteLine($"Wrote {entries.Count} entries to {outFile}");
    return 0;
}

static int RunBuild(BuildGraph g, string[] explicitFiles, int maxJobs, bool dryRun)
{
    List<CommandNode> plan;

    if (explicitFiles.Length > 0)
    {
        // Explicit files: resolve and walk forward
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in explicitFiles)
        {
            var r = ResolveFileArg(g, arg);
            if (r != null) resolved.Add(r);
        }
        if (resolved.Count == 0)
        {
            Console.Error.WriteLine($"{Clr.Green}Nothing to build.{Clr.Reset}");
            return 0;
        }
        plan = g.GetAffectedCommands(resolved);
    }
    else
    {
        // Default: mtime-based dirty detection
        Console.Error.WriteLine($"{Clr.Dim}Checking file timestamps...{Clr.Reset}");
        plan = g.GetDirtyCommandsByMtime().Plan;
    }

    if (plan.Count == 0)
    {
        Console.Error.WriteLine($"{Clr.Green}Everything up to date.{Clr.Reset}");
        return 0;
    }

    // Filter to commands that have command lines (skip synthetic)
    plan = plan.Where(c => !string.IsNullOrEmpty(c.CommandLine)).ToList();
    if (plan.Count == 0)
    {
        Console.Error.WriteLine($"{Clr.Yellow}No executable commands in plan.{Clr.Reset}");
        return 0;
    }

    var effectiveJobs = Math.Min(plan.Count, maxJobs);
    Console.Error.WriteLine($"{Clr.Bold}Build plan: {plan.Count} command{(plan.Count == 1 ? "" : "s")}, {effectiveJobs} parallel{Clr.Reset}");
    Console.Error.WriteLine();

    if (dryRun)
    {
        foreach (var cmd in plan)
        {
            Console.Error.WriteLine($"{Clr.Cyan}[{cmd.Tool}]{Clr.Reset} {Clr.Dim}{cmd.Project}{Clr.Reset}");
            Console.WriteLine(cmd.CommandLine);
            Console.Error.WriteLine();
        }
        return 0;
    }

    // Execute in waves: commands whose inputs are all "done" can run in parallel.
    // Track which files have been produced.
    var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // All source files (not produced by any command) are available from the start.
    foreach (var f in g.Files.Values)
        if (!g.FileToProducer.ContainsKey(f.Path))
            produced.Add(f.Path);

    var remaining = new List<CommandNode>(plan);
    int failures = 0;
    int completed = 0;
    int total = plan.Count;
    bool isTty = !Console.IsErrorRedirected;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var statusLock = new object();

    void WriteStatus(string tool, string file, bool done, bool failed = false)
    {
        lock (statusLock)
        {
            if (isTty)
            {
                // Erase current line, write status
                var sym = done ? (failed ? $"{Clr.Red}✗" : $"{Clr.Green}✓") : $"{Clr.Cyan}…";
                var counter = $"[{completed}/{total}]";
                var shortFile = Path.GetFileName(file);
                var line = $"\r{Clr.Bold}{counter}{Clr.Reset} {sym}{Clr.Reset} [{tool}] {shortFile}";
                Console.Error.Write(line + new string(' ', Math.Max(0, Console.WindowWidth - line.Length + 20)));
                if (done && failed)
                    Console.Error.WriteLine(); // preserve failed lines
            }
            else if (done)
            {
                var sym = failed ? "✗" : "✓";
                Console.Error.WriteLine($"  {sym} [{tool}] {file}");
            }
        }
    }

    while (remaining.Count > 0)
    {
        // Find commands whose inputs are all produced
        var wave = remaining.Where(c => c.Inputs.All(i => produced.Contains(i))).ToList();
        if (wave.Count == 0)
        {
            if (isTty) Console.Error.WriteLine(); // clear status line
            Console.Error.WriteLine($"{Clr.Red}Deadlock: {remaining.Count} commands stuck (missing inputs){Clr.Reset}");
            foreach (var c in remaining)
            {
                var missing = c.Inputs.Where(i => !produced.Contains(i)).ToList();
                Console.Error.WriteLine($"  [{c.Tool}] waiting on: {string.Join(", ", missing.Take(3))}");
            }
            return 1;
        }

        foreach (var c in wave) remaining.Remove(c);

        // Run wave in parallel with live progress
        var results = new System.Collections.Concurrent.ConcurrentBag<(CommandNode cmd, int exitCode, string output)>();
        Parallel.ForEach(wave, new ParallelOptions { MaxDegreeOfParallelism = maxJobs }, cmd =>
        {
            WriteStatus(cmd.Tool, cmd.Outputs.FirstOrDefault() ?? cmd.Id, done: false);
            var (exitCode, output) = ExecuteCommand(cmd);
            Interlocked.Increment(ref completed);
            results.Add((cmd, exitCode, output));
            WriteStatus(cmd.Tool, cmd.Outputs.FirstOrDefault() ?? cmd.Id, done: true, failed: exitCode != 0);
        });

        // Process results — mark outputs as produced (or not on failure)
        foreach (var (cmd, exitCode, output) in results)
        {
            if (exitCode == 0)
            {
                foreach (var o in cmd.Outputs) produced.Add(o);
            }
            else
            {
                failures++;
                if (isTty) Console.Error.WriteLine(); // newline after status
                Console.Error.WriteLine($"  {Clr.Red}✗{Clr.Reset} [{cmd.Tool}] {cmd.Outputs.FirstOrDefault() ?? cmd.Id}  (exit {exitCode})");
                if (!string.IsNullOrWhiteSpace(output))
                    Console.Error.WriteLine(output);
            }
        }
    }

    if (isTty)
    {
        // Clear the status line and print summary
        Console.Error.Write($"\r{new string(' ', Math.Max(Console.WindowWidth, 40))}\r");
    }

    sw.Stop();
    if (failures == 0)
        Console.Error.WriteLine($"{Clr.Green}Build succeeded{Clr.Reset} ({plan.Count} commands, {sw.Elapsed.TotalSeconds:F1}s)");
    else
        Console.Error.WriteLine($"{Clr.Red}Build failed{Clr.Reset} ({failures}/{plan.Count} commands failed, {sw.Elapsed.TotalSeconds:F1}s)");

    return failures > 0 ? 1 : 0;
}

static (int exitCode, string output) ExecuteCommand(CommandNode cmd)
{
    // Parse command line: first token is the executable, rest are arguments.
    // The command line from binlog is a full invocation string.
    var cmdLine = cmd.CommandLine;
    string exe, args;

    if (cmdLine.StartsWith('"'))
    {
        var endQuote = cmdLine.IndexOf('"', 1);
        exe = endQuote > 0 ? cmdLine[1..endQuote] : cmdLine;
        args = endQuote > 0 && endQuote + 1 < cmdLine.Length ? cmdLine[(endQuote + 2)..] : "";
    }
    else
    {
        var sp = cmdLine.IndexOf(' ');
        exe = sp > 0 ? cmdLine[..sp] : cmdLine;
        args = sp > 0 ? cmdLine[(sp + 1)..] : "";
    }

    var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
    {
        WorkingDirectory = cmd.WorkingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    try
    {
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return (1, "Failed to start process");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var output = (stdout + stderr).Trim();
        return (proc.ExitCode, output);
    }
    catch (Exception ex)
    {
        return (1, ex.Message);
    }
}

/// Try to match a user-supplied filename/path against the graph's file index.
/// Graph stores root-relative paths; user may pass absolute or partial names.
static string? ResolveFileArg(BuildGraph g, string arg)
{
    // Normalize forward slashes so Unix-style paths work on Windows
    arg = arg.Replace('/', '\\');

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
    var cacheName = Path.GetFileNameWithoutExtension(binlogPath) + ".graph.json.gz";
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
    List<string> Outputs,
    string CommandLine = "",   // full tool invocation from binlog
    string WorkingDir = "");   // project directory

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

    /// External include path prefixes (from CAExcludePath). Files under these
    /// are SDK/generated headers — excluded from mtime dirty checking.
    public HashSet<string> ExternalPrefixes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // file → synthetic (#include) commands that produce it (1:N, unlike FileToProducer)
    public Dictionary<string, List<string>> SyntheticProducers { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                    var absPath = ToAbsolute(input);
                    if (File.Exists(absPath))
                    {
                        var t = File.GetLastWriteTimeUtc(absPath);
                        if (t > maxInputTime) { maxInputTime = t; newestInput = input; }
                    }
                }

                foreach (var output in cmd.Outputs)
                {
                    var absPath = ToAbsolute(output);
                    if (!File.Exists(absPath)) { dirty = true; triggers.Add(output + " (missing)"); break; }
                    var t = File.GetLastWriteTimeUtc(absPath);
                    if (maxInputTime > t) { dirty = true; if (newestInput != null) triggers.Add(newestInput); break; }
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

        // Extract external include prefixes from CAExcludePath (per-project SetEnv task).
        // These are SDK/generated directories whose headers we skip in mtime checks.
        foreach (var task in build.FindChildrenRecursive<MSTask>(t => t.Name == "SetEnv"))
        {
            var pf = task.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
            var name = pf?.FindChildrenRecursive<Property>(p => p.Name == "Name")
                .FirstOrDefault()?.Value;
            if (name != "CAExcludePath") continue;
            var value = pf?.FindChildrenRecursive<Property>(p => p.Name == "Value")
                .FirstOrDefault()?.Value ?? "";
            var projNode = task.GetNearestParent<Project>();
            var projDir = projNode?.ProjectFile != null
                ? Path.GetDirectoryName(Path.GetFullPath(projNode.ProjectFile)) ?? ""
                : "";
            foreach (var dir in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (dir == "PreventSdkUapPropsAssignment") continue; // not a path
                // Absolute paths (SDK, toolchain) → convert to root-relative
                // Relative paths (e.g. "Generated Files\") → prepend project-relative prefix
                var abs = Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(projDir, dir));
                var rel = Path.GetRelativePath(rootDir, abs);
                if (!rel.EndsWith('\\')) rel += '\\';
                graph.ExternalPrefixes.Add(rel);
            }
        }

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
            // Extract the full command line from binlog (available for CL, Link, Lib, MIDL)
            // CommandLineArguments is a child of the task, not inside the Parameters folder.
            var cmdLineRaw = (task.FindChildrenRecursive<Property>(p => p.Name == "CommandLineArguments")
                .FirstOrDefault()?.Value ?? "").ReplaceLineEndings(" ").Trim();
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

                // PCH detection: /Yc = create, /Yu = use
                var pchOutFile = pf.FindChildrenRecursive<Property>(p => p.Name == "PrecompiledHeaderOutputFile")
                    .FirstOrDefault()?.Value ?? "";
                var pchPath = string.IsNullOrEmpty(pchOutFile) ? ""
                    : graph.ToRelative(ResolveAbsolute(projDir, pchOutFile));
                bool createsYc = cmdLineRaw.Contains("/Yc");
                bool usesYu = cmdLineRaw.Contains("/Yu");

                foreach (var src in sources)
                {
                    var obj = graph.ToRelative(Path.Combine(absObjDir,
                        Path.GetFileNameWithoutExtension(src) + ".obj"));
                    var cmdId = $"CL#{cmdIndex++}:{proj}/{target}";
                    // Build per-file command line: strip batched sources, append single source
                    var absSrc = Path.GetFullPath(Path.Combine(graph.RootDir, src));
                    var absObj = Path.GetFullPath(Path.Combine(graph.RootDir, obj));
                    var clCmdLine = BuildClCommandLine(cmdLineRaw, absSrc, absObj, sources.Count);

                    var inputs = new List<string> { src };
                    var outputs = new List<string> { obj };

                    // /Yc command (pch.cpp): also produces pch.pch
                    if (createsYc && !string.IsNullOrEmpty(pchPath))
                        outputs.Add(pchPath);
                    // /Yu command (regular .cpp): depends on pch.pch
                    if (usesYu && !string.IsNullOrEmpty(pchPath))
                        inputs.Add(pchPath);

                    var cmd = new CommandNode(cmdId, "CL", proj, target, inputs, outputs, clCmdLine, projDir);
                    graph.Commands[cmdId] = cmd;
                    graph.Files.TryAdd(src, new FileNode(src, FileKinds.Classify(src)));
                    graph.Files.TryAdd(obj, new FileNode(obj, FileKinds.Classify(obj)));
                    if (!string.IsNullOrEmpty(pchPath))
                        graph.Files.TryAdd(pchPath, new FileNode(pchPath, FileKind.Intermediate));
                    foreach (var i in inputs) graph.AddConsumer(i, cmdId);
                    foreach (var o in outputs) graph.FileToProducer.TryAdd(o, cmdId);

                    // Record absolute source path for tlog matching
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
                var cmd = new CommandNode(cmdId, "MIDL", proj, target, [src], [meta], cmdLineRaw, projDir);
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
                var cmd = new CommandNode(cmdId, toolName, proj, target, sources, [outFile], cmdLineRaw, projDir);
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
            // CommandLineArguments is a child property of the task (like CL/Link)
            var priCmdLine = (priTask.FindChildrenRecursive<Property>(
                p => p.Name == "CommandLineArguments").FirstOrDefault()?.Value ?? "")
                .ReplaceLineEndings(" ").Trim();
            var cmd = new CommandNode(cmdId, "makepri", proj4, target4, [], [priRel])
            {
                CommandLine = priCmdLine,
                WorkingDir = projDir4
            };
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
                var absSrc = ResolveAbsolute(projDir7, srcItems[i]);
                var absDst = ResolveAbsolute(projDir7, dstItems[i]);
                var copyCmd = $"cmd /c copy /Y \"{absSrc}\" \"{absDst}\"";
                var cmd = new CommandNode(cmdId, "Copy", proj7, target7, [srcRel], [dstRel], copyCmd, projDir7);
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
                    // Track in SyntheticProducers (1:N) so mtime walk finds all headers for a source
                    if (!graph.SyntheticProducers.TryGetValue(currentSourceRel, out var spList))
                    {
                        spList = [];
                        graph.SyntheticProducers[currentSourceRel] = spList;
                    }
                    spList.Add(cmdId);
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
    /// Build a per-file CL command line from the batched command line.
    /// The batched cmdline ends with all source files; we strip them and
    /// substitute the single source + explicit /Fo for the output.
    static string BuildClCommandLine(string batchedCmdLine, string absSource, string absObj, int sourceCount)
    {
        if (string.IsNullOrEmpty(batchedCmdLine)) return "";
        // CL cmdline format: cl.exe /flags... source1.cpp source2.cpp ...
        // The source files are the last N tokens (unquoted .cpp paths or quoted).
        // Strategy: find the tool + all flags (everything before the first source),
        // then append our single source + /Fo.
        // Simple heuristic: split, drop last sourceCount tokens, append ours.
        var parts = SplitCommandLine(batchedCmdLine);
        if (parts.Count <= sourceCount) return batchedCmdLine; // can't split
        var flags = parts.Take(parts.Count - sourceCount);
        return string.Join(" ", flags) + $" /Fo\"{absObj}\" \"{absSource}\"";
    }

    /// Crude command-line splitter that respects double quotes.
    static List<string> SplitCommandLine(string cmdLine)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var ch in cmdLine)
        {
            if (ch == '"') { inQuote = !inQuote; sb.Append(ch); }
            else if (ch == ' ' && !inQuote)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

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
    public List<string> ExternalPrefixes { get; set; } = [];

    public static void Save(string path, BuildGraph graph, DateTime binlogStamp)
    {
        var cache = new GraphCache
        {
            BinlogTimestamp = binlogStamp.Ticks,
            RootDir = graph.RootDir,
            ExternalPrefixes = graph.ExternalPrefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            Files = graph.Files.Values.Select(f => new CachedFile { Path = f.Path, Kind = (int)f.Kind }).ToList(),
            Commands = graph.Commands.Values.Select(c => new CachedCommand
            {
                Id = c.Id, Tool = c.Tool, Project = c.Project, Target = c.Target,
                Inputs = c.Inputs, Outputs = c.Outputs,
                CommandLine = c.CommandLine, WorkingDir = c.WorkingDir
            }).ToList()
        };
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, cache, BtJsonContext.Default.GraphCache);
    }

    public static GraphCache? Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gz, BtJsonContext.Default.GraphCache);
    }

    public BuildGraph ToGraph()
    {
        var graph = new BuildGraph { RootDir = RootDir };
        foreach (var p in ExternalPrefixes)
            graph.ExternalPrefixes.Add(p);
        foreach (var f in Files)
            graph.Files.TryAdd(f.Path, new FileNode(f.Path, (FileKind)f.Kind));
        foreach (var c in Commands)
        {
            var cmd = new CommandNode(c.Id, c.Tool, c.Project, c.Target, c.Inputs, c.Outputs, c.CommandLine, c.WorkingDir);
            graph.Commands[cmd.Id] = cmd;
            foreach (var input in cmd.Inputs)
                graph.AddConsumer(input, cmd.Id);
            foreach (var output in cmd.Outputs)
            {
                graph.FileToProducer.TryAdd(output, cmd.Id);
                // Rebuild SyntheticProducers index for #include commands
                if (cmd.Tool.StartsWith("#"))
                {
                    if (!graph.SyntheticProducers.TryGetValue(output, out var spList))
                    {
                        spList = [];
                        graph.SyntheticProducers[output] = spList;
                    }
                    spList.Add(cmd.Id);
                }
            }
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
    public string CommandLine { get; set; } = "";
    public string WorkingDir { get; set; } = "";
}

class CompileCommandEntry
{
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "";
    public string File { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializableAttribute(typeof(GraphCache))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
partial class BtJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

[System.Text.Json.Serialization.JsonSerializableAttribute(typeof(List<CompileCommandEntry>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
partial class CompileDbJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
