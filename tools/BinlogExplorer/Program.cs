using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger;
using MSTask = Microsoft.Build.Logging.StructuredLogger.Task;

// ============================================================
// BinlogExplorer — reusable tool for probing MSBuild binlogs.
//   Usage: BinlogExplorer <binlog> <command> [args...]
//   Commands:
//     tasks                     List all task types (grouped, with counts)
//     tree <TaskName> [N]       Dump tree for first N instances of TaskName
//     props <TaskName> [N]      Show parameters/items for first N instances
//     search <pattern>          Find nodes whose text matches (case-insensitive)
//     targets [project-filter]  List targets per project
// ============================================================

if (args.Length < 2) { Usage(); return 1; }

var binlogPath = args[0];
var command = args[1].ToLowerInvariant();

if (!File.Exists(binlogPath)) { Console.Error.WriteLine($"error: {binlogPath} not found"); return 1; }
var build = BinaryLog.ReadBuild(binlogPath);

return command switch
{
    "tasks"   => Tasks(build),
    "tree"    => Tree(build, args),
    "props"   => Props(build, args),
    "search"  => Search(build, args),
    "targets" => Targets(build, args),
    "envvars" => EnvVars(build),
    _         => Usage()
};

// -----------------------------------------------------------

int Usage()
{
    Console.Error.WriteLine("Usage: BinlogExplorer <binlog> <command> [args...]");
    Console.Error.WriteLine("  tasks                     List task types with counts");
    Console.Error.WriteLine("  tree <TaskName> [N]       Dump tree for first N instances");
    Console.Error.WriteLine("  props <TaskName> [N]      Show parameters/items for instances");
    Console.Error.WriteLine("  search <pattern>          Search node text (case-insensitive)");
    Console.Error.WriteLine("  targets [project-filter]  List targets per project");
    return 1;
}

int Tasks(Build build)
{
    var tasks = build.FindChildrenRecursive<MSTask>().ToList();
    Console.WriteLine($"Total tasks: {tasks.Count}\n");
    foreach (var g in tasks
        .GroupBy(t => $"{Project(t)}|{t.Name}")
        .OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Count(),3}x  {g.Key}");
    return 0;
}

int Tree(Build build, string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("Usage: tree <TaskName> [N]"); return 1; }
    var name = args[2];
    var limit = args.Length > 3 ? int.Parse(args[3]) : 3;
    var tasks = build.FindChildrenRecursive<MSTask>(t => t.Name == name).Take(limit).ToList();
    Console.WriteLine($"Found {tasks.Count} {name} task(s) (showing ≤{limit}):\n");
    foreach (var t in tasks)
    {
        Console.WriteLine($"--- {t.Name} in {Project(t)} / {Target(t)} ---");
        DumpTree(t, 0, 8);
        Console.WriteLine();
    }
    return 0;
}

int Props(Build build, string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("Usage: props <TaskName> [N]"); return 1; }
    var name = args[2];
    var limit = args.Length > 3 ? int.Parse(args[3]) : 5;
    var tasks = build.FindChildrenRecursive<MSTask>(t => t.Name == name).Take(limit).ToList();
    Console.WriteLine($"Found {tasks.Count} {name} task(s) (showing ≤{limit}):\n");
    foreach (var t in tasks)
    {
        Console.WriteLine($"--- {t.Name} in {Project(t)} / {Target(t)} ---");
        if (!string.IsNullOrEmpty(t.CommandLineArguments))
            Console.WriteLine($"  CMD: {Trunc(t.CommandLineArguments, 300)}");

        foreach (var child in t.Children)
        {
            if (child is Property prop)
                Console.WriteLine($"  [Property] {prop.Name} = {Trunc(prop.Value, 200)}");
            else if (child is Parameter param)
            {
                Console.WriteLine($"  [Parameter] {param.Name}:");
                foreach (var item in param.Children.OfType<Item>().Take(20))
                    Console.WriteLine($"    - {item.Text}");
                if (param.Children.Count > 20)
                    Console.WriteLine($"    ... and {param.Children.Count - 20} more");
            }
            else if (child is Folder folder)
            {
                Console.WriteLine($"  [Folder] {folder.Name}:");
                DumpTree(folder, 2, 4);
            }
            else if (child is NamedNode named)
                Console.WriteLine($"  [{child.GetType().Name}] {named.Name}");
        }
        Console.WriteLine();
    }
    return 0;
}

int Search(Build build, string[] args)
{
    if (args.Length < 3) { Console.Error.WriteLine("Usage: search <pattern>"); return 1; }
    var pattern = args[2];
    var matches = new List<(string Type, string Text, string Context)>();
    WalkAll(build, node =>
    {
        var text = node switch
        {
            Message m  => m.Text,
            Property p => $"{p.Name} = {p.Value}",
            Item i     => i.Text,
            NamedNode n => n.Name,
            _          => null
        };
        if (text != null && text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            matches.Add((node.GetType().Name, Trunc(text, 200), ContextPath(node)));
    });
    Console.WriteLine($"Found {matches.Count} matches for '{pattern}':\n");
    foreach (var (type, text, ctx) in matches.Take(100))
        Console.WriteLine($"  [{type}] {ctx}\n    {text}");
    if (matches.Count > 100)
        Console.WriteLine($"\n  ... and {matches.Count - 100} more");
    return 0;
}

int Targets(Build build, string[] args)
{
    var filter = args.Length > 2 ? args[2] : null;
    var projects = build.FindChildrenRecursive<Project>()
        .Where(p => filter == null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .ToList();
    foreach (var proj in projects)
    {
        Console.WriteLine($"\n=== {proj.Name} ===");
        foreach (var tgt in proj.Children.OfType<Target>())
            Console.WriteLine($"  {tgt.Name}");
    }
    return 0;
}

// --- Helpers ---

static string Project(BaseNode node) =>
    node.GetNearestParent<Project>()?.Name ?? "?";

static string Target(BaseNode node) =>
    node.GetNearestParent<Target>()?.Name ?? "?";

static string Trunc(string s, int max) =>
    s.Length <= max ? s : s[..max] + "...";

static string ContextPath(BaseNode node)
{
    var parts = new List<string>();
    var n = node.Parent;
    while (n != null)
    {
        if (n is Project p) { parts.Add(p.Name); break; }
        if (n is Target t) parts.Add(t.Name);
        if (n is MSTask task) parts.Add(task.Name);
        n = n.Parent;
    }
    parts.Reverse();
    return string.Join(" > ", parts);
}

static void WalkAll(BaseNode node, Action<BaseNode> action)
{
    action(node);
    if (node is TreeNode tree)
        foreach (var child in tree.Children)
            WalkAll(child, action);
}

static void DumpTree(BaseNode node, int depth, int maxDepth)
{
    if (depth > maxDepth) return;
    var indent = new string(' ', depth * 2);
    var label = node switch
    {
        Message m  => $"[Message] {Trunc(m.Text ?? "", 150)}",
        Property p => $"[Property] {p.Name} = {Trunc(p.Value ?? "", 150)}",
        Item i     => $"[Item] {Trunc(i.Text ?? "", 150)}",
        MSTask t   => $"[Task] {t.Name}",
        _          => $"[{node.GetType().Name}] {(node is NamedNode nn ? nn.Name : node.ToString() ?? "")}"
    };
    Console.WriteLine($"{indent}{label}");
    if (node is TreeNode tree)
        foreach (var child in tree.Children)
            DumpTree(child, depth + 1, maxDepth);
}

int EnvVars(Build build)
{
    var seen = new HashSet<string>();
    foreach (var t in build.FindChildrenRecursive<MSTask>(t => t.Name == "SetEnv"))
    {
        var pf = t.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Parameters");
        if (pf == null) continue;
        var name = pf.FindChildrenRecursive<Property>(p => p.Name == "Name").FirstOrDefault()?.Value ?? "";
        var val = pf.FindChildrenRecursive<Property>(p => p.Name == "Value").FirstOrDefault()?.Value ?? "";
        var proj = Project(t);
        var target = Target(t);
        var key = $"{proj}/{target}/{name}";
        if (seen.Add(key))
        {
            var display = val.Length > 1000 ? val[..1000] + "..." : val;
            Console.WriteLine($"[{proj}] [{target}] {name} = {display}");
        }
    }
    Console.WriteLine($"\n{seen.Count} unique SetEnv calls");
    return 0;
}
