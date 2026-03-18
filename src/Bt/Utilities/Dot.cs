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
