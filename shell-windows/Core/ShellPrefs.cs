using System.Text.Json;
using System.Text.Json.Nodes;

namespace XTrader.Shell.Core;

/// <summary>
/// 壳自身的界面偏好（如模拟交易页上次选中的 tab、常见问题展开态）。
///
/// 与业务数据分开存放在数据根目录下：后端只认 <c>data/</c> 与 <c>runtime/</c>，
/// 壳的偏好不参与后端读写，也不会被全量更新覆盖（更新只替换 runtime/backend）。
/// 发布形态是 unpackaged，WinUI 的 ApplicationData 不可用，故自行落文件。
/// </summary>
internal static class ShellPrefs
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataRoot, "shell-prefs.json");

    /// <summary>读一个字符串偏好；文件缺失或损坏时返回 null（按默认值处理）。</summary>
    internal static string? Get(string key)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            using var stream = File.OpenRead(FilePath);
            var root = JsonNode.Parse(stream) as JsonObject;
            return root?[key]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // 偏好文件损坏只影响 UI 记忆，按未设置处理；不阻断页面加载
            return null;
        }
    }

    /// <summary>写入一个字符串偏好。写失败同样只影响 UI 记忆，不向调用方抛。</summary>
    internal static void Set(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var root = File.Exists(FilePath)
                ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            root[key] = value;
            File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 同上：偏好写不进去不该打断用户操作
        }
    }

    /// <summary>
    /// 删除若干已废弃的偏好键。键不存在时不做任何事（也不会创建文件）；
    /// 清理失败只影响遗留数据的去留，不向调用方抛。
    /// </summary>
    internal static void Remove(params string[] keys)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root)
            {
                return;
            }
            var removed = false;
            foreach (var key in keys)
            {
                removed |= root.Remove(key);
            }
            if (removed)
            {
                File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 同 Get / Set：偏好清理失败不该打断用户操作
        }
    }
}
