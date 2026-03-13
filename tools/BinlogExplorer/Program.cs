using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Logging.StructuredLogger;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Binlog Dependency Explorer ===\n");
        
        string binlogPath = "../../msbuild.binlog";
        Console.WriteLine($"Loading binlog from: {binlogPath}");
        
        try
        {
            var build = BinaryLog.ReadBuild(binlogPath);
            Console.WriteLine($"Build loaded successfully. Root: {build.GetType().Name}\n");

            // Search for all CL tasks
            Console.WriteLine("=== SEARCHING FOR CL TASKS ===\n");
            var clTasks = FindAllNodes(build, n => 
                n is Task task && (task.Name == "CL" || task.Name.Contains("CL.exe"))
            ).Cast<Task>().ToList();

            Console.WriteLine($"Found {clTasks.Count} CL tasks\n");

            foreach (var clTask in clTasks.Take(10)) // Limit to first 10
            {
                Console.WriteLine($"\n--- CL Task: {clTask.Name} ---");
                DumpNodeStructure(clTask, depth: 0, maxDepth: 6);
            }

            // Search for any node that might contain header/include information
            Console.WriteLine("\n\n=== SEARCHING FOR DEPENDENCY-RELATED NODES ===\n");
            
            var allNodes = FindAllNodes(build, n => true).ToList();
            Console.WriteLine($"Total nodes in binlog: {allNodes.Count}\n");

            // Look for specific node types and names
            var keywords = new[] { "include", "header", ".h", ".hpp", "tracker", "tlog", 
                                   "read file", "dependency", "CLCommandThatRefersToInputs", 
                                   "TrackedFileAccess", "showIncludes", "input", "output" };

            var interestingNodes = new HashSet<BaseNode>();
            foreach (var keyword in keywords)
            {
                var matches = FindAllNodes(build, n => 
                    n.Name != null && n.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();

                foreach (var match in matches)
                {
                    interestingNodes.Add(match);
                }
            }

            Console.WriteLine($"Found {interestingNodes.Count} nodes matching keywords\n");
            foreach (var node in interestingNodes.OrderBy(n => n.GetType().Name).ThenBy(n => n.Name))
            {
                Console.WriteLine($"  [{node.GetType().Name}] {node.Name}");
            }

            // Look for any nodes with .h or .hpp references in their properties/children
            Console.WriteLine("\n\n=== SEARCHING FOR .h/.hpp FILE REFERENCES ===\n");
            var headerRefNodes = FindAllNodes(build, n => 
            {
                var text = n.ToString() ?? "";
                return (text.Contains(".h\"") || text.Contains(".h'") || 
                        text.Contains(".hpp\"") || text.Contains(".hpp'") ||
                        text.Contains(".h ") || text.Contains(".hpp "));
            }).Take(50).ToList();

            Console.WriteLine($"Found {headerRefNodes.Count} nodes referencing header files (showing first 50):\n");
            foreach (var node in headerRefNodes)
            {
                Console.WriteLine($"[{node.GetType().Name}] {node.Name}");
                Console.WriteLine($"  Text: {node.ToString().Substring(0, Math.Min(200, node.ToString().Length))}");
                Console.WriteLine();
            }

            // Dump all Task nodes to see structure
            Console.WriteLine("\n\n=== ALL TASKS IN BUILD ===\n");
            var allTasks = FindAllNodes(build, n => n is Task).Cast<Task>().ToList();
            Console.WriteLine($"Total tasks: {allTasks.Count}\n");
            
            var taskGroups = allTasks.GroupBy(t => t.Name).OrderByDescending(g => g.Count());
            foreach (var group in taskGroups.Take(20))
            {
                Console.WriteLine($"  {group.Key}: {group.Count()}");
            }

            // For each CL task, dump its FULL tree
            Console.WriteLine("\n\n=== DETAILED CL TASK TREE DUMP ===\n");
            foreach (var clTask in clTasks.Take(3))
            {
                Console.WriteLine($"\n>>> CL Task: {clTask.Name} <<<");
                DumpFullTree(clTask, depth: 0);
            }

            Console.WriteLine("\n\n=== SEARCH FOR .tlog OR FILE TRACKER OUTPUT ===\n");
            var tlogNodes = FindAllNodes(build, n => 
                n.Name != null && (n.Name.Contains(".tlog") || n.Name.Contains("tlog"))
            ).ToList();
            Console.WriteLine($"Found {tlogNodes.Count} tlog-related nodes:\n");
            foreach (var node in tlogNodes)
            {
                Console.WriteLine($"[{node.GetType().Name}] {node.Name}");
            }

            Console.WriteLine("\n\n=== Search for Message nodes with 'reading' or 'tracking' ===\n");
            var msgNodes = FindAllNodes(build, n => n is Message msg)
                .Cast<Message>()
                .Where(m => m.Text != null && (m.Text.Contains("reading", StringComparison.OrdinalIgnoreCase) || 
                                               m.Text.Contains("tracking", StringComparison.OrdinalIgnoreCase) ||
                                               m.Text.Contains("includes", StringComparison.OrdinalIgnoreCase)))
                .Take(20)
                .ToList();

            Console.WriteLine($"Found {msgNodes.Count} relevant messages:\n");
            foreach (var msg in msgNodes)
            {
                Console.WriteLine($"  {msg.Text.Substring(0, Math.Min(150, msg.Text.Length))}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
        }
    }

    static List<BaseNode> FindAllNodes(BaseNode root, Func<BaseNode, bool> predicate, List<BaseNode> results = null)
    {
        results ??= new List<BaseNode>();

        if (predicate(root))
        {
            results.Add(root);
        }

        if (root is IHasChildren hasChildren)
        {
            foreach (var child in hasChildren.Children)
            {
                FindAllNodes(child, predicate, results);
            }
        }

        return results;
    }

    static void DumpNodeStructure(BaseNode node, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        string indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}[{node.GetType().Name}] {node.Name}");

        if (!string.IsNullOrEmpty(node.ToString()) && node.ToString().Length > 0)
        {
            var text = node.ToString();
            if (text.Length > 150)
                text = text.Substring(0, 150) + "...";
            Console.WriteLine($"{indent}  → {text}");
        }

        if (node is IHasChildren hasChildren)
        {
            foreach (var child in hasChildren.Children.Take(10))
            {
                DumpNodeStructure(child, depth + 1, maxDepth);
            }
            if (hasChildren.Children.Count > 10)
            {
                Console.WriteLine($"{indent}  ... and {hasChildren.Children.Count - 10} more children");
            }
        }
    }

    static void DumpFullTree(BaseNode node, int depth)
    {
        if (depth > 8) return; // Hard limit

        string indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}[{node.GetType().Name}] {node.Name}");

        // Try to get useful text info
        if (node is Task task)
        {
            if (!string.IsNullOrEmpty(task.CommandLineArguments))
                Console.WriteLine($"{indent}  CMD: {task.CommandLineArguments.Substring(0, Math.Min(200, task.CommandLineArguments.Length))}");
        }
        else if (node is Message msg)
        {
            if (!string.IsNullOrEmpty(msg.Text))
                Console.WriteLine($"{indent}  MSG: {msg.Text.Substring(0, Math.Min(200, msg.Text.Length))}");
        }

        if (node is IHasChildren hasChildren && hasChildren.Children.Count > 0)
        {
            Console.WriteLine($"{indent}  Children ({hasChildren.Children.Count}):");
            foreach (var child in hasChildren.Children)
            {
                DumpFullTree(child, depth + 1);
            }
        }
    }
}
