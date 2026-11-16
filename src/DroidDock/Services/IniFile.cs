using System.IO;
using System.Text;

namespace DroidDock.Services;

/// <summary>Minimal reader/writer for the key=value files the Android tools use.</summary>
public static class IniFile
{
    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    public static Dictionary<string, string> Read(string path) =>
        File.Exists(path) ? Parse(File.ReadAllLines(path)) : new Dictionary<string, string>();

    public static void Write(string path, IEnumerable<KeyValuePair<string, string>> values)
    {
        var text = string.Join("\n", values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}")) + "\n";
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
