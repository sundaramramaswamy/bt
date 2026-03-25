record CommandNode(
    string Id,
    string Tool,         // "CL", "LINK", "LIB", "MIDL"
    string Project,
    string Target,
    List<string> Inputs,
    List<string> Outputs,
    string CommandLine = "",   // full tool invocation from binlog
    string WorkingDir = "")   // project directory
{
    // Mutable so SourceInference can append inferred .obj files to LINK/LIB commands.
    public string CommandLine { get; set; } = CommandLine;
}
