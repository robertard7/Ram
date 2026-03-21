using Microsoft.Win32;
using System.IO;
using System.Text;

namespace RAM.Tools;

public sealed class ToolService
{
    public string Echo(string input)
    {
        return $"ECHO:{Environment.NewLine}{input}";
    }

    public string ListFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        var sb = new StringBuilder();
        sb.AppendLine($"Folder: {path}");
        sb.AppendLine();

        var directories = Directory.GetDirectories(path)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var files = Directory.GetFiles(path)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        sb.AppendLine("[Directories]");
        foreach (var dir in directories)
        {
            sb.AppendLine($"  {Path.GetFileName(dir)}");
        }

        sb.AppendLine();
        sb.AppendLine("[Files]");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            sb.AppendLine($"  {info.Name}  ({info.Length} bytes)");
        }

        return sb.ToString();
    }

    public string ReadTextFile(string path, int maxChars = 12000)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        var text = File.ReadAllText(path);

        if (text.Length <= maxChars)
            return text;

        return text[..maxChars] + Environment.NewLine + Environment.NewLine + "[TRUNCATED]";
    }

    public string PickFolderWithDialog()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder"
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : "";
    }

    public string PickTextFileWithDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select text file",
            Filter = "Text files|*.txt;*.md;*.json;*.cs;*.xaml;*.xml;*.log|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : "";
    }
}