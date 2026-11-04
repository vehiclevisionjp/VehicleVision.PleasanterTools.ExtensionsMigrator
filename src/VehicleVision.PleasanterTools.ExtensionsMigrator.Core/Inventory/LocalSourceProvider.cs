namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Core.Inventory;

/// <summary>ローカルのフォルダ（Pleasanter の <c>App_Data/Parameters</c> に相当する場所）を読むだけの provider。何も書かない。</summary>
public sealed class LocalSourceProvider : ISourceProvider
{
    /// <summary>読む 1 ファイルの上限。これを超えるものは読まない。</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    private readonly string root;

    public LocalSourceProvider(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException("The source directory does not exist.");
        }

        root = Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>ルートの絶対パス。</summary>
    public string RootPath => root;

    public IReadOnlyList<SourceItem> List()
    {
        var items = new List<SourceItem>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!directory.Name.StartsWith("Extended", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsReparsePoint(directory))
            {
                items.Add(new SourceItem(directory.Name, 0, true));
                continue;
            }

            Walk(directory, items);
        }

        return items.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
    }

    public byte[] Read(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The path is outside the source root.");
        }

        var info = new FileInfo(full);
        if (IsReparsePoint(info))
        {
            throw new InvalidOperationException("Links are not read.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidOperationException("The file is too large.");
        }

        return File.ReadAllBytes(full);
    }

    private static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;

    private void Walk(DirectoryInfo directory, List<SourceItem> items)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (IsReparsePoint(entry))
            {
                items.Add(new SourceItem(relative, 0, true));
            }
            else if (entry is DirectoryInfo child)
            {
                Walk(child, items);
            }
            else if (entry is FileInfo file)
            {
                items.Add(new SourceItem(relative, file.Length, false));
            }
        }
    }
}
