using System.ComponentModel.DataAnnotations;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 允许操作的文件类型
/// </summary>
public enum AllowedFileType
{
    /// <summary>
    /// 文本文件
    /// </summary>
    [Display(Name = "文本文件")]
    Text,

    /// <summary>
    /// 代码文件
    /// </summary>
    [Display(Name = "代码文件")]
    Code,

    /// <summary>
    /// 图片文件
    /// </summary>
    [Display(Name = "图片文件")]
    Image,

    /// <summary>
    /// 视频文件
    /// </summary>
    [Display(Name = "视频文件")]
    Video,

    /// <summary>
    /// 音频文件
    /// </summary>
    [Display(Name = "音频文件")]
    Audio,

    /// <summary>
    /// 压缩文件
    /// </summary>
    [Display(Name = "压缩文件")]
    Compressed,

    /// <summary>
    /// 其他允许的文件
    /// </summary>
    [Display(Name = "其他文件")]
    Other
}

/// <summary>
/// 文件信息模型
/// </summary>
public sealed class FileItemInfo
{
    /// <summary>
    /// 文件名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 完整路径
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// 类型（file / folder）
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 格式化的文件大小
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// 文件扩展名
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public DateTime Modified { get; set; }

    /// <summary>
    /// 访问时间
    /// </summary>
    public DateTime Accessed { get; set; }

    /// <summary>
    /// 是否只读
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// 是否隐藏
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// 文件夹中的子项数量（仅文件夹）
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// 文件类型分类
    /// </summary>
    public AllowedFileType? FileTypeCategory { get; set; }

    /// <summary>
    /// 是否允许操作
    /// </summary>
    public bool IsAllowedToOperate => FileTypeCategory.HasValue;
}

/// <summary>
/// 目录浏览结果
/// </summary>
public sealed class DirectoryBrowseResult
{
    /// <summary>
    /// 目录路径
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 总文件数
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// 总文件夹数
    /// </summary>
    public int TotalFolders { get; set; }

    /// <summary>
    /// 目录中的项
    /// </summary>
    public List<FileItemInfo> Items { get; set; } = [];

    /// <summary>
    /// 是否有更多项（超过限制）
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// 限制数量
    /// </summary>
    public int LimitCount { get; set; }
}

/// <summary>
/// 文件搜索结果
/// </summary>
public sealed class FileSearchResult
{
    /// <summary>
    /// 搜索根路径
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// 搜索模式
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// 找到的文件
    /// </summary>
    public List<FileItemInfo> Files { get; set; } = [];

    /// <summary>
    /// 搜索是否完成
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// 搜索用时（毫秒）
    /// </summary>
    public long ElapsedMs { get; set; }
}

internal sealed record TextFileLine(int LineNumber, string Content);

internal sealed class TextFileReadResult
{
    private TextFileReadResult(
        bool isSuccess,
        string path,
        int totalLines,
        int startLine,
        int endLine,
        bool withLineNumbers,
        string encoding,
        string newLine,
        string hash,
        List<TextFileLine> lines,
        string? errorMessage)
    {
        IsSuccess = isSuccess;
        Path = path;
        TotalLines = totalLines;
        StartLine = startLine;
        EndLine = endLine;
        WithLineNumbers = withLineNumbers;
        Encoding = encoding;
        NewLine = newLine;
        Hash = hash;
        Lines = lines;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public string Path { get; }

    public int TotalLines { get; }

    public int StartLine { get; }

    public int EndLine { get; }

    public bool WithLineNumbers { get; }

    public string Encoding { get; }

    public string NewLine { get; }

    public string Hash { get; }

    public List<TextFileLine> Lines { get; }

    public string? ErrorMessage { get; }

    public static TextFileReadResult CreateSuccess(
        string path,
        int totalLines,
        int startLine,
        int endLine,
        bool withLineNumbers,
        string encoding,
        string newLine,
        string hash,
        List<TextFileLine> lines)
        => new(true, path, totalLines, startLine, endLine, withLineNumbers, encoding, newLine, hash, lines, null);

    public static TextFileReadResult CreateError(string path, string errorMessage)
        => new(false, path, 0, 0, 0, false, string.Empty, string.Empty, string.Empty, [], errorMessage);
}
