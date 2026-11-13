using System.Text.Json;

namespace BetterPinyin.Core.Updates;

/// <summary>一次发布里的附件（发布包）。</summary>
/// <param name="Name">文件名，例如 <c>BetterMicrosoftPinyin-v0.2.0-win-x64.zip</c>。</param>
/// <param name="DownloadUrl">直链地址。</param>
/// <param name="Size">字节数；GitHub 没给时为 0。</param>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

/// <summary>
/// GitHub 上的一条 Release。
/// </summary>
/// <param name="Tag">原始 tag，例如 <c>v0.2.0</c>。</param>
/// <param name="Version">从 tag 解析出来的版本号。</param>
/// <param name="PageUrl">发布页地址。</param>
/// <param name="Notes">发布说明正文（Markdown）。</param>
/// <param name="PublishedAt">发布时间；解析失败为 null。</param>
/// <param name="IsPrerelease">是否预发布。</param>
/// <param name="Assets">附件列表。</param>
public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string PageUrl,
    string Notes,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>找到 Windows 用的 zip 包；没有返回 null。</summary>
    public UpdateAsset? FindWindowsPackage() =>
        Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 这条发布是否比当前版本更新。
    /// </summary>
    /// <param name="current">当前程序版本。</param>
    /// <returns>更新返回 true。</returns>
    /// <remarks>
    /// 预发布一律不算：用户装的是稳定版，不该被 beta 追着升级。
    /// </remarks>
    public bool IsNewerThan(Version current) => !IsPrerelease && Normalize(Version).CompareTo(Normalize(current)) > 0;

    /// <summary>补齐成四段再比较，避免 <c>0.2</c> 与 <c>0.2.0.0</c> 被判成不同。</summary>
    internal static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));
}

/// <summary>
/// 解析 GitHub 的 Release JSON。
/// </summary>
/// <remarks>
/// 只读需要的字段，其余一律不碰：接口字段很多且会变，绑成 DTO 反而更脆。
/// 任何异常都退化成 null，让调用方按「检查失败」处理，不要把网络问题变成崩溃。
/// </remarks>
public static class ReleaseParser
{
    /// <summary>解析一条 Release。</summary>
    /// <param name="json"><c>releases/latest</c> 的响应正文。</param>
    /// <returns>解析结果；不是合法 Release 时返回 null。</returns>
    public static ReleaseInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tag = GetString(root, "tag_name");
            var version = ParseTag(tag);
            if (tag is null || version is null)
            {
                return null;
            }

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var name = GetString(item, "name");
                    var url = GetString(item, "browser_download_url");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    var size = item.TryGetProperty("size", out var raw) && raw.TryGetInt64(out var value) ? value : 0;
                    assets.Add(new UpdateAsset(name, url, size));
                }
            }

            // tag 里带 -beta / -rc 的也算预发布，不能只看 JSON 的 prerelease 字段：
            // 有时候 tag 是预发布但发布页勾成了正式版。
            var prerelease = tag.Contains('-') ||
                (root.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True);

            return new ReleaseInfo(
                tag,
                version,
                GetString(root, "html_url") ?? string.Empty,
                GetString(root, "body") ?? string.Empty,
                GetPublishedAt(root),
                prerelease,
                assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 tag 解析版本号。
    /// </summary>
    /// <param name="tag">形如 <c>v0.2.0</c>、<c>0.2.0</c>、<c>v0.2.0-beta.1</c>。</param>
    /// <returns>版本号；认不出返回 null。</returns>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        if (!Version.TryParse(text, out var version))
        {
            return null;
        }

        // 补齐成四段：Version.TryParse("1.2") 的 Build 是 -1，直接 ToString(3) 会抛异常。
        return version.Build >= 0
            ? version
            : new Version(version.Major, version.Minor, 0, 0);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetPublishedAt(JsonElement root)
    {
        var text = GetString(root, "published_at");
        return DateTimeOffset.TryParse(text, out var published) ? published : null;
    }
}
