namespace TreeSizeBetter.Core.Util;

public static class PathUtil
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Setzt das \\?\-Praefix davor. Damit umgeht die Enumeration das alte
    /// MAX_PATH-Limit von 260 Zeichen - ohne das bricht jeder Scan ab, sobald
    /// irgendwo ein tief verschachtelter node_modules-Ordner liegt.
    /// </summary>
    public static string ToExtended(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return ExtendedUncPrefix + path[2..];

        return ExtendedPrefix + path;
    }

    public static string StripExtended(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
            return @"\\" + path[ExtendedUncPrefix.Length..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path[ExtendedPrefix.Length..];
        return path;
    }

    /// <summary>Haengt einen Namen an einen Pfad an, ohne doppelten Trenner bei C:\.</summary>
    public static string Combine(string directory, string name)
    {
        if (directory.Length == 0) return name;
        char last = directory[^1];
        return last is '\\' or '/'
            ? directory + name
            : directory + '\\' + name;
    }

    /// <summary>Suchmuster fuer FindFirstFileEx: Pfad plus \* in erweiterter Form.</summary>
    public static string ToSearchPattern(string directory)
        => Combine(ToExtended(directory), "*");

    /// <summary>Wurzel eines Pfades in der Form C:\ - fuer GetDiskFreeSpace und Volume-Abfragen.</summary>
    public static string? GetVolumeRoot(string path)
    {
        string plain = StripExtended(path);
        if (plain.Length >= 2 && plain[1] == ':')
            return string.Concat(plain.AsSpan(0, 2), @"\");

        if (plain.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share -> \\server\share\
            int slash = plain.IndexOf('\\', 2);
            if (slash < 0) return null;
            int second = plain.IndexOf('\\', slash + 1);
            string share = second < 0 ? plain : plain[..second];
            return share + '\\';
        }
        return null;
    }

    /// <summary>true, wenn <paramref name="candidate"/> gleich <paramref name="root"/> ist oder darunter liegt.</summary>
    public static bool IsSameOrBelow(string candidate, string root)
    {
        string c = StripExtended(candidate).TrimEnd('\\');
        string r = StripExtended(root).TrimEnd('\\');
        if (c.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        return c.Length > r.Length
               && c.StartsWith(r, StringComparison.OrdinalIgnoreCase)
               && c[r.Length] == '\\';
    }
}
