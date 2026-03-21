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

    /// Visible length of a string after stripping ANSI escape sequences and \r.
    public static int VisibleLength(string s)
    {
        int len = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && s[i] != 'm') i++;
                continue;
            }
            if (s[i] != '\r') len++;
        }
        return len;
    }
}
