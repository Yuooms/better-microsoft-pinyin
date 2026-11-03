using System.Text.Json;

namespace BetterPinyin.Core.Cloud.Bing;

/// <summary>
/// 解析 Bing 搜索建议的两种响应格式。
/// </summary>
/// <remarks>
/// 解析必须极端容错：接口是公开但非契约的，随时可能返回 JSONP 包装、
/// 尾部注释、空结果或结构变化。任何解析失败都只返回空列表，绝不抛异常，
/// 否则一次接口变动就会打断用户输入。
/// </remarks>
public static class BingResponseParser
{
    /// <summary>
    /// 解析响应体。
    /// </summary>
    /// <param name="body">HTTP 响应体。</param>
    /// <param name="format">响应格式。</param>
    /// <returns>候选文本列表（保序、未清洗）；解析失败返回空列表。</returns>
    public static IReadOnlyList<string> Parse(string body, BingResponseFormat format)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<string>();
        }

        var json = ExtractJson(body);
        if (json is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return format == BingResponseFormat.OpenSearchJson
                ? ParseOpenSearch(document.RootElement)
                : ParseSearchBox(document.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 从可能带 JSONP 包装或尾部注释的响应中截出纯 JSON 片段。
    /// </summary>
    /// <param name="body">原始响应体。</param>
    /// <returns>JSON 片段；找不到时返回 null。</returns>
    public static string? ExtractJson(string body)
    {
        var startBracket = body.IndexOf('[');
        var startBrace = body.IndexOf('{');

        int start;
        char close;
        if (startBrace < 0 && startBracket < 0)
        {
            return null;
        }

        if (startBrace < 0)
        {
            start = startBracket;
            close = ']';
        }
        else if (startBracket < 0)
        {
            start = startBrace;
            close = '}';
        }
        else if (startBracket < startBrace)
        {
            start = startBracket;
            close = ']';
        }
        else
        {
            start = startBrace;
            close = '}';
        }

        var end = body.LastIndexOf(close);
        if (end <= start)
        {
            return null;
        }

        return body.Substring(start, end - start + 1);
    }

    private static IReadOnlyList<string> ParseOpenSearch(JsonElement root)
    {
        // 形如 ["查询词", ["候选1", "候选2", ...]]
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
        {
            return Array.Empty<string>();
        }

        var items = root[1];
        if (items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ParseSearchBox(JsonElement root)
    {
        // 形如 {"AS":{"Results":[{"Suggests":[{"Txt":"候选"}]}]}}
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("AS", out var asNode) ||
            asNode.ValueKind != JsonValueKind.Object ||
            !asNode.TryGetProperty("Results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(16);
        foreach (var group in results.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty("Suggests", out var suggests) ||
                suggests.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var suggest in suggests.EnumerateArray())
            {
                if (suggest.ValueKind != JsonValueKind.Object ||
                    !suggest.TryGetProperty("Txt", out var textNode) ||
                    textNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = textNode.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }
}
