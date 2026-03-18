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
