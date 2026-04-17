using System.Text.Json.Serialization;

namespace Cortana.Plugins.GoogleSearch.Models;

/// <summary>
/// Google Custom Search JSON API 的搜索响应。
/// 仅包含本插件实际使用的字段。
/// </summary>
public sealed class SearchResponse
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public SearchUrl? Url { get; set; }

    [JsonPropertyName("queries")]
    public SearchQueries? Queries { get; set; }

    [JsonPropertyName("searchInformation")]
    public SearchInformation? SearchInformation { get; set; }

    [JsonPropertyName("items")]
    public List<SearchItem>? Items { get; set; }
}

/// <summary>
/// 请求相关的 URL 模板信息。
/// </summary>
public sealed class SearchUrl
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("template")]
    public string Template { get; set; } = string.Empty;
}

/// <summary>
/// 分页查询信息。
/// </summary>
public sealed class SearchQueries
{
    [JsonPropertyName("request")]
    public List<QueryInfo>? Request { get; set; }

    [JsonPropertyName("nextPage")]
    public List<QueryInfo>? NextPage { get; set; }

    [JsonPropertyName("previousPage")]
    public List<QueryInfo>? PreviousPage { get; set; }
}

/// <summary>
/// 单个查询的元数据。
/// </summary>
public sealed class QueryInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("totalResults")]
    public string TotalResults { get; set; } = string.Empty;

    [JsonPropertyName("searchType")]
    public string SearchType { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("startIndex")]
    public int StartIndex { get; set; }

    [JsonPropertyName("startPage")]
    public int StartPage { get; set; }
}

/// <summary>
/// 搜索统计信息。
/// </summary>
public sealed class SearchInformation
{
    [JsonPropertyName("searchTime")]
    public double SearchTime { get; set; }

    [JsonPropertyName("formattedSearchTime")]
    public string FormattedSearchTime { get; set; } = string.Empty;

    [JsonPropertyName("totalResults")]
    public string TotalResults { get; set; } = string.Empty;

    [JsonPropertyName("formattedTotalResults")]
    public string FormattedTotalResults { get; set; } = string.Empty;
}

/// <summary>
/// 单条搜索结果。
/// </summary>
public sealed class SearchItem
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("htmlTitle")]
    public string HtmlTitle { get; set; } = string.Empty;

    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("displayLink")]
    public string DisplayLink { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;

    [JsonPropertyName("htmlSnippet")]
    public string HtmlSnippet { get; set; } = string.Empty;

    [JsonPropertyName("cacheId")]
    public string? CacheId { get; set; }

    [JsonPropertyName("thumbnail")]
    public SearchThumbnail? Thumbnail { get; set; }

    [JsonPropertyName("image")]
    public SearchImage? Image { get; set; }
}

/// <summary>
/// 图片搜索结果的缩略图信息。
/// </summary>
public sealed class SearchThumbnail
{
    [JsonPropertyName("width")]
    public string Width { get; set; } = string.Empty;

    [JsonPropertyName("height")]
    public string Height { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public SearchThumbnailSource? Source { get; set; }
}

/// <summary>
/// 缩略图来源信息。
/// </summary>
public sealed class SearchThumbnailSource
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

/// <summary>
/// 图片搜索结果的原图信息。
/// </summary>
public sealed class SearchImage
{
    [JsonPropertyName("contextLink")]
    public string ContextLink { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("byteSize")]
    public int ByteSize { get; set; }

    [JsonPropertyName("thumbnailLink")]
    public string ThumbnailLink { get; set; } = string.Empty;

    [JsonPropertyName("thumbnail")]
    public SearchThumbnailSource? Thumbnail { get; set; }
}