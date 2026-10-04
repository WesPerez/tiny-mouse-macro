namespace TinyMouseMacro;

// Records app operations and configured shortcut diagnostics, never general keyboard input.
internal static class ActivityLog
{
    internal const int MaximumBytes = 128 * 1024;
    internal static void Append(string path, string message)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                File.Move(path, path + ".previous", overwrite: true);
            var text = message.Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > 1000) text = text[..1000];
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
