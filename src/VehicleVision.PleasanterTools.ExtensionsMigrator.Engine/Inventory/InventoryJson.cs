using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.ExtensionsMigrator.Engine.Inventory;

/// <summary>棚卸し結果の JSON の読み書きと、原子的な保存。</summary>
public static class InventoryJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(InventoryReport report) => JsonSerializer.Serialize(report, Options);

    /// <summary>読む。形が違えば <see cref="FormatException"/>。</summary>
    public static InventoryReport Deserialize(string json)
    {
        try
        {
            var report = JsonSerializer.Deserialize<InventoryReport>(json, Options);
            if (report is null || report.FormatVersion != InventoryReport.CurrentFormatVersion)
            {
                throw new FormatException("Unsupported inventory format version.");
            }

            return report;
        }
        catch (JsonException)
        {
            throw new FormatException("The inventory file is not valid.");
        }
    }

    /// <summary>一時ファイルへ書いてから置き換える。途中で失敗しても、前の内容を壊さない。</summary>
    public static void WriteAtomic(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
