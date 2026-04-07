using System.CommandLine;
using System.CommandLine.Help;
using Microsoft.Build.Logging.StructuredLogger;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Clean up leftover from a previous update
UpdateCommand.CleanupOldBinary();

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
var graphHeadersOption = new Option<bool>("--headers") { Description = "Include tlog-recorded #include headers in -f subgraph" };
graphCmd.Add(graphFileOption);
graphCmd.Add(graphProjectOption);
graphCmd.Add(graphHeadersOption);

var outputsFilesArg = new Argument<string[]>("files") { Description = "Source files to query", Arity = ArgumentArity.OneOrMore };
var outputsOfCmd = new Command("bins", "Downstream dependency tree from <file>");
outputsOfCmd.Add(outputsFilesArg);

var sourcesFilesArg = new Argument<string[]>("files") { Description = "Output files to query", Arity = ArgumentArity.OneOrMore };
var sourcesOfCmd = new Command("srcs", "List all upstream files that feed into <file>");
var srcsHeadersOption = new Option<bool>("--headers") { Description = "Include tlog-recorded #include headers" };
sourcesOfCmd.Add(sourcesFilesArg);
sourcesOfCmd.Add(srcsHeadersOption);

var affectedFilesArg = new Argument<string[]>("files") { Description = "Changed files (default: git diff)", Arity = ArgumentArity.ZeroOrMore };
var affectedCmd = new Command("dirty", "Build plan for changed files");
affectedCmd.Add(affectedFilesArg);

var buildFilesArg = new Argument<string[]>("files") { Description = "Changed files (default: git diff)", Arity = ArgumentArity.ZeroOrMore };
var buildJobsOption = new Option<int>("-j") { Description = "Max parallel jobs (default: CPU cores)" };
buildJobsOption.DefaultValueFactory = _ => Environment.ProcessorCount;
var buildDryRunOption = new Option<bool>("--dry-run") { Description = "Print commands without executing" };
buildDryRunOption.Aliases.Add("-n");
var buildCompileOnlyOption = new Option<bool>("--compile-only") { Description = "Compile only — skip link/lib" };
buildCompileOnlyOption.Aliases.Add("-c");
var buildCmd = new Command("build", "Build only what's dirty");
buildCmd.Add(buildFilesArg);
buildCmd.Add(buildJobsOption);
buildCmd.Add(buildDryRunOption);
buildCmd.Add(buildCompileOnlyOption);

var compileCommandsOutputOption = new Option<string>("-o") { Description = "Output file (default: compile_commands.json in repo root)" };
var compileCommandsCmd = new Command("compiledb", "Generate compile_commands.json for clangd/clang-tidy");
compileCommandsCmd.Add(compileCommandsOutputOption);

var cacheCmd = new Command("cache", "Parse binlog and cache dependency graph");

var watchDebounceOption = new Option<int>("--debounce") { Description = "Debounce delay in ms before triggering build (default: 300)" };
watchDebounceOption.DefaultValueFactory = _ => 300;
var watchRunOption = new Option<string>("--run") { Description = "Command to run after successful build" };
var watchCmd = new Command("watch", "Watch sources and rebuild on change");
watchCmd.Add(watchDebounceOption);
watchCmd.Add(watchRunOption);

var updateCheckOption = new Option<bool>("--check") { Description = "Check for updates without installing" };
updateCheckOption.Aliases.Add("-c");
var updateCmd = new Command("update", "Check for and install updates from GitHub");
updateCmd.Add(updateCheckOption);

// -- Wire up --
var root = new RootCommand("bt — MSBuild/C++ incremental build tool");
root.Add(binlogOption);
root.Add(colorOption);
root.Add(graphCmd);
root.Add(outputsOfCmd);
root.Add(sourcesOfCmd);
root.Add(affectedCmd);
root.Add(buildCmd);
root.Add(compileCommandsCmd);
root.Add(cacheCmd);
root.Add(watchCmd);
root.Add(updateCmd);

// Resolve version string for help banner
var btVersion = "unknown";
var attrs = System.Reflection.Assembly.GetExecutingAssembly()
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
foreach (var attr in attrs)
{
    if (attr is System.Reflection.AssemblyInformationalVersionAttribute infoAttr)
    {
        btVersion = infoAttr.InformationalVersion;
        break;
    }
}
if (btVersion == "unknown")
    btVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
// Short version for help banner:
// Published: 1.0.0-ci.62.1c145d8+fullhash → strip +hash (already in suffix)
// Dev build: 1.0.0+fullhash → trim hash to 7 chars
var plusIdx = btVersion.IndexOf('+');
var btVersionShort = plusIdx < 0 ? btVersion
    : btVersion.Contains("-ci.") ? btVersion[..plusIdx]
    : btVersion[..Math.Min(btVersion.Length, plusIdx + 8)];

Telemetry.Version = btVersionShort;

if (args.Length == 1 && args[0] is "--version")
{
    Console.WriteLine(btVersionShort);
    return 0;
}

// Replace System.CommandLine's built-in help with our coloured version.
// HelpOption only exists on RootCommand — subcommands inherit it.
var helpOpt = root.Options.OfType<HelpOption>().FirstOrDefault();
if (helpOpt is not null)
    helpOpt.Action = new ColoredHelpAction(btVersionShort, colorOption);

// Show coloured root help when invoked with no args or bare "help".
root.SetAction(result =>
{
    Clr.SetMode(result.GetValue(colorOption) ?? "auto");
    Console.Error.WriteLine(new ColoredHelpAction(btVersionShort).GetRootHelp());
});
// Rewrite bare "help" to "--help" so System.CommandLine routes it through HelpOption.
if (Array.Exists(args, a => a == "help"))
    args = args.Select(a => a == "help" ? "--help" : a).ToArray();

graphCmd.SetAction(result =>
{
    var g = Setup(result);
    var filterFiles = result.GetValue(graphFileOption);
    var filterProjects = result.GetValue(graphProjectOption);
    var includeHeaders = result.GetValue(graphHeadersOption);
    return GraphCommand.ShowGraph(g, filterFiles, filterProjects, includeHeaders);
});

outputsOfCmd.SetAction(result =>
{
    var g = Setup(result);
    var files = result.GetValue(outputsFilesArg)!;
    return QueryCommands.OutputsOf(g, files);
});

sourcesOfCmd.SetAction(result =>
{
    var g = Setup(result);
    var files = result.GetValue(sourcesFilesArg)!;
    var includeHeaders = result.GetValue(srcsHeadersOption);
    return QueryCommands.SourcesOf(g, files, includeHeaders);
});

affectedCmd.SetAction(result =>
{
    var g = Setup(result);
    var explicitFiles = result.GetValue(affectedFilesArg) ?? [];
    return DirtyCommand.Affected(g, explicitFiles);
});

buildCmd.SetAction(result =>
{
    var g = Setup(result);
    var explicitFiles = result.GetValue(buildFilesArg) ?? [];
    var maxJobs = result.GetValue(buildJobsOption);
    var dryRun = result.GetValue(buildDryRunOption);
    var compileOnly = result.GetValue(buildCompileOnlyOption);
    return BuildCommand.RunBuild(g, explicitFiles, maxJobs, dryRun, compileOnly) == BuildCommand.BuildResult.Failed ? 1 : 0;
});

compileCommandsCmd.SetAction(result =>
{
    var g = Setup(result);
    var outPath = result.GetValue(compileCommandsOutputOption);
    return CompileDbCommand.CompileCommands(g, outPath);
});

cacheCmd.SetAction(result =>
{
    Setup(result);
    return 0;
});

watchCmd.SetAction(result =>
{
    var g = Setup(result);
    var binlog = result.GetValue(binlogOption)!;
    if (!File.Exists(binlog) && binlog == "msbuild.binlog")
        foreach (var alt in new[] { "msbuild_debug.binlog", "msbuild_release.binlog" })
            if (File.Exists(alt)) { binlog = alt; break; }
    var debounceMs = result.GetValue(watchDebounceOption);
    var runCmd = result.GetValue(watchRunOption);
    return WatchCommand.RunWatch(g, Path.GetFullPath(binlog), debounceMs, LoadGraph, runCmd);
});

updateCmd.SetAction(result =>
{
    Clr.SetMode(result.GetValue(colorOption) ?? "auto");
    var checkOnly = result.GetValue(updateCheckOption);
    return UpdateCommand.RunUpdate(btVersionShort, checkOnly);
});

var parseResult = root.Parse(args);
var exitCode = parseResult.Invoke();

// Fire-and-forget telemetry for real commands (not help/version/no-args)
var cmdName = parseResult.CommandResult.Command.Name;
var isHelp = args.Any(a => a is "-?" or "-h" or "--help" or "/?" or "help");
if (cmdName != root.Name && !isHelp && cmdName != "watch")
{
    // Extract flag names only (strip values) for feature-usage insight
    var flags = string.Join(" ", args.Where(a => a.StartsWith('-') && !a.StartsWith("--binlog") && !a.StartsWith("--color")));
    Telemetry.LogCommand(cmdName, exitCode == 0, flags);
}

return exitCode;

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
    var cacheName = Path.GetFileNameWithoutExtension(binlogPath) + ".fb";
    var cachePath = Path.Combine(cacheDir, cacheName);
    var binlogStamp = File.GetLastWriteTimeUtc(binlogPath);

    // Try loading from cache
    BuildGraph graph;
    BuildGraph? staleGraph = null;
    if (File.Exists(cachePath))
    {
        try
        {
            var cached = GraphCache.Load(cachePath);
            if (cached is { } c)
            {
                if (c.BinlogTimestamp == binlogStamp.Ticks)
                {
                    Console.Error.WriteLine($"{Clr.Dim}cache:{Clr.Reset} {cachePath}");
                    graph = c.Graph;
                    goto infer;
                }
                staleGraph = c.Graph;
                Console.Error.WriteLine($"{Clr.Dim}cache stale, rebuilding{Clr.Reset}");
            }
        }
        catch { Console.Error.WriteLine($"{Clr.Dim}cache corrupt, rebuilding{Clr.Reset}"); }
    }

    // Parse binlog and build graph
    Console.Error.WriteLine($"{Clr.Dim}binlog:{Clr.Reset} {binlogPath}");
    {
        var build = BinaryLog.ReadBuild(binlogPath);
        if (!build.Succeeded)
        {
            if (staleGraph != null)
            {
                Console.Error.WriteLine(
                    $"{Clr.Yellow}warning:{Clr.Reset} binlog is from a failed build; using last good cache");
                graph = staleGraph;
                goto infer;
            }
            Console.Error.WriteLine($"{Clr.Red}error:{Clr.Reset} binlog is from a failed build");
            if (build.FirstError is { } err)
                Console.Error.WriteLine($"  {Clr.Red}{err.File}({err.LineNumber}): {err.Text}{Clr.Reset}");
            Console.Error.WriteLine($"Fix the build errors and re-run: {Clr.Dim}msbuild /bl{Clr.Reset}");
            Environment.Exit(1);
        }
        graph = BuildGraphFactory.FromBinlog(build);
    }

    // Save cache
    try
    {
        Directory.CreateDirectory(cacheDir);
        GraphCache.Save(cachePath, graph, binlogStamp);
        Console.Error.WriteLine($"{Clr.Dim}cached:{Clr.Reset} {cachePath}");
    }
    catch (Exception ex) { Console.Error.WriteLine($"{Clr.Dim}cache write failed: {ex.Message}{Clr.Reset}"); }

    infer:
    var inf = SourceInference.InferNewSources(graph, binlogPath);
    if (inf.InferredCount > 0)
        Console.Error.WriteLine(
            $"{Clr.Yellow}inferred:{Clr.Reset} {inf.InferredCount} new source(s) not in binlog");
    foreach (var w in inf.Warnings)
        Console.Error.WriteLine($"{Clr.Yellow}warning:{Clr.Reset} {w}");

    return graph;
}
