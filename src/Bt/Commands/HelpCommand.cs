using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

/// Replaces System.CommandLine's default HelpAction so that -?, /?, -h, --help
/// all flow through a single, coloured code-path.
sealed class ColoredHelpAction(string versionShort, Option<string>? colorOption = null) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        // Respect --color if the user supplied it, otherwise default to auto.
        var colorValue = colorOption is not null
            ? parseResult.GetResult(colorOption)?.GetValueOrDefault<string>()
            : null;
        Clr.SetMode(colorValue ?? "auto");

        // Determine which command the user asked help for.
        var cmd = parseResult.CommandResult.Command.Name;
        Console.Error.WriteLine(cmd switch
        {
            "graph" => GraphHelp(),
            "bins" => BinsHelp(),
            "srcs" => SrcsHelp(),
            "dirty" => DirtyHelp(),
            "build" => BuildHelp(),
            "compiledb" => CompileDbHelp(),
            "cache" => CacheHelp(),
            "watch" => WatchHelp(),
            _ => GetRootHelp()
        });
        return 0;
    }

    public string GetRootHelp() => $"""

        {Clr.Bold}bt{Clr.Reset} {Clr.Dim}{versionShort}{Clr.Reset} — MSBuild incremental build tool

        {Clr.Yellow}Usage:{Clr.Reset}  bt [command] [options]

        {Clr.Yellow}Commands:{Clr.Reset}
          {Clr.Cyan}graph{Clr.Reset}              Emit Graphviz DOT dependency graph
          {Clr.Cyan}bins{Clr.Reset} <files>       Downstream dependency tree
          {Clr.Cyan}srcs{Clr.Reset} <files>       Upstream dependency tree
          {Clr.Cyan}dirty{Clr.Reset} [files]      Build plan (mtime-based, or explicit files)
          {Clr.Cyan}build{Clr.Reset} [files]      Build only what's dirty (-j N, -n, -c)
          {Clr.Cyan}compiledb{Clr.Reset}          Generate compile_commands.json (-o path)
          {Clr.Cyan}cache{Clr.Reset}              Parse binlog and cache dependency graph
          {Clr.Cyan}watch{Clr.Reset}              Watch sources and rebuild on change

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>    Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>    auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}
          {Clr.Green}--version{Clr.Reset}          Show version
          {Clr.Green}-?, --help{Clr.Reset}         Show this help

        {Clr.Yellow}Build options:{Clr.Reset}
          {Clr.Green}-j{Clr.Reset} <N>              Max parallel jobs     {Clr.Dim}[default: CPU cores]{Clr.Reset}
          {Clr.Green}-n, --dry-run{Clr.Reset}       Print commands without executing
          {Clr.Green}-c, --compile-only{Clr.Reset}  Compile only — skip link/lib

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
          {Clr.Dim}bt build -c src/Foo.cpp{Clr.Reset}
          {Clr.Dim}bt compiledb{Clr.Reset}
        """;

    static string GraphHelp() => $"""

        {Clr.Bold}bt graph{Clr.Reset} — Emit Graphviz DOT dependency graph

        {Clr.Yellow}Usage:{Clr.Reset}  bt graph [options]

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}-f, --file{Clr.Reset} <path>     Subgraph reachable from/to file
          {Clr.Green}-p, --project{Clr.Reset} <name>  Only nodes from project
          {Clr.Green}--headers{Clr.Reset}              Include #include headers in -f subgraph
          {Clr.Green}--binlog{Clr.Reset} <path>       Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>       auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt graph | dot -Tsvg -o build.svg{Clr.Reset}
          {Clr.Dim}bt graph -f TestDataItem.h{Clr.Reset}
          {Clr.Dim}bt graph -f main.cpp --headers{Clr.Reset}
          {Clr.Dim}bt graph -p XaBench -f main.cpp{Clr.Reset}
        """;

    static string BinsHelp() => $"""

        {Clr.Bold}bt bins{Clr.Reset} — Downstream dependency tree from <file>

        {Clr.Yellow}Usage:{Clr.Reset}  bt bins <files>

        {Clr.Yellow}Arguments:{Clr.Reset}
          {Clr.Cyan}<files>{Clr.Reset}  Source files to query

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Example:{Clr.Reset}
          {Clr.Dim}bt bins TestDataItem.h{Clr.Reset}
        """;

    static string SrcsHelp() => $"""

        {Clr.Bold}bt srcs{Clr.Reset} — Upstream dependency tree for <file>

        {Clr.Yellow}Usage:{Clr.Reset}  bt srcs [options] <files>

        {Clr.Yellow}Arguments:{Clr.Reset}
          {Clr.Cyan}<files>{Clr.Reset}  Output files to query

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--headers{Clr.Reset}          Include tlog-recorded #include headers
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt srcs XaBench.exe{Clr.Reset}
          {Clr.Dim}bt srcs --headers Tracing.cpp{Clr.Reset}
        """;

    static string DirtyHelp() => $"""

        {Clr.Bold}bt dirty{Clr.Reset} — Build plan for changed files

        {Clr.Yellow}Usage:{Clr.Reset}  bt dirty [files]

        {Clr.Yellow}Arguments:{Clr.Reset}
          {Clr.Cyan}[files]{Clr.Reset}  Changed files {Clr.Dim}(default: all mtime-dirty){Clr.Reset}

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt dirty{Clr.Reset}
          {Clr.Dim}bt dirty src/Foo.cpp src/Bar.h{Clr.Reset}
        """;

    static string BuildHelp() => $"""

        {Clr.Bold}bt build{Clr.Reset} — Build only what's dirty

        {Clr.Yellow}Usage:{Clr.Reset}  bt build [files] [options]

        {Clr.Yellow}Arguments:{Clr.Reset}
          {Clr.Cyan}[files]{Clr.Reset}  Files to build {Clr.Dim}(default: all mtime-dirty){Clr.Reset}

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}-j{Clr.Reset} <N>              Max parallel jobs     {Clr.Dim}[default: CPU cores]{Clr.Reset}
          {Clr.Green}-n, --dry-run{Clr.Reset}       Print commands without executing
          {Clr.Green}-c, --compile-only{Clr.Reset}  Compile only — skip link/lib
          {Clr.Green}--binlog{Clr.Reset} <path>     Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>     auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt build{Clr.Reset}
          {Clr.Dim}bt build -j 4 src/Foo.cpp{Clr.Reset}
          {Clr.Dim}bt build --dry-run{Clr.Reset}
          {Clr.Dim}bt build -c src/Foo.cpp{Clr.Reset}
        """;

    static string CompileDbHelp() => $"""

        {Clr.Bold}bt compiledb{Clr.Reset} — Generate compile_commands.json for clangd/clang-tidy

        {Clr.Yellow}Usage:{Clr.Reset}  bt compiledb [options]

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}-o{Clr.Reset} <path>        Output file  {Clr.Dim}[default: compile_commands.json]{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Example:{Clr.Reset}
          {Clr.Dim}bt compiledb{Clr.Reset}
        """;

    static string CacheHelp() => $"""

        {Clr.Bold}bt cache{Clr.Reset} — Parse binlog and cache dependency graph

        {Clr.Yellow}Usage:{Clr.Reset}  bt cache [options]

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}
        """;

    static string WatchHelp() => $"""

        {Clr.Bold}bt watch{Clr.Reset} — Watch sources and rebuild on change

        {Clr.Yellow}Usage:{Clr.Reset}  bt watch [options]

        {Clr.Yellow}Options:{Clr.Reset}
          {Clr.Green}--debounce{Clr.Reset} <ms>  Debounce delay before rebuild  {Clr.Dim}[default: 300]{Clr.Reset}
          {Clr.Green}--run{Clr.Reset} <cmd>      Command to run after successful build
          {Clr.Green}--binlog{Clr.Reset} <path>  Path to .binlog file  {Clr.Dim}[default: msbuild.binlog]{Clr.Reset}
          {Clr.Green}--color{Clr.Reset}  <mode>  auto | always | never {Clr.Dim}[default: auto]{Clr.Reset}

        {Clr.Yellow}Examples:{Clr.Reset}
          {Clr.Dim}bt watch{Clr.Reset}
          {Clr.Dim}bt watch --run "test.exe"{Clr.Reset}
          {Clr.Dim}bt watch --debounce 500{Clr.Reset}
        """;
}
