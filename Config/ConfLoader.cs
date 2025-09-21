using System.Collections.Generic;
using System.IO;

namespace Sharpmote.App.Config;

public static class ConfLoader
{
    public static IReadOnlyDictionary<string, string> LoadFrom(string directory, string fileName)
    {
        var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return dict;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#") || line.StartsWith(";")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            dict[key] = value;
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(key)))
                System.Environment.SetEnvironmentVariable(key, value);
        }
        return dict;
    }
}
