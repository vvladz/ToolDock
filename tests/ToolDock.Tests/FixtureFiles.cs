internal static class FixtureFiles
{
    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string[] ReadLines(string path)
    {
        var text = ReadText(path);
        var complete = text.LastIndexOf('\n');
        return complete < 0 ? [] : text[..complete].Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    internal static void WriteOnce(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path);
    }
}
