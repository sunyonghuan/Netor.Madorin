using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using System.Security.Cryptography;

namespace Netor.Cortana.AI;

/// <summary>
/// 负责把用户附件展开、复制到资源目录、写入资源索引，并转换为发送给模型的 <see cref="AIContent"/>。
/// 阶段 3.1 先从 <see cref="AiChatHostedService"/> 中抽出附件装载职责，不改变上层发送链路。
/// </summary>
public sealed class ChatAttachmentLoader(
    ChatMessageAssetService assetService,
    IAppPaths appPaths,
    ILogger<ChatAttachmentLoader> logger)
{
    public async Task LoadAsync(
        ICollection<AIContent> contents,
        IReadOnlyList<AttachmentInfo> attachments,
        string sessionId,
        string userMessageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);

        if (attachments.Count == 0)
        {
            return;
        }

        var assetEntities = new List<ChatMessageAssetEntity>();
        var sortOrder = 0;
        var expandedAttachments = ExpandAttachments(attachments);

        foreach (var attachment in expandedAttachments)
        {
            try
            {
                if (!File.Exists(attachment.Path))
                {
                    logger.LogWarning("附件文件不存在：{Path}", attachment.Path);
                    continue;
                }

                var assetGroup = ResolveAssetGroup(attachment.MimeType);
                var relativePath = Path.Combine("histories", sessionId, assetGroup, userMessageId, attachment.Name);
                var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, relativePath);

                var targetDir = Path.GetDirectoryName(absolutePath)!;
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(attachment.Path, absolutePath, overwrite: true);

                var fileInfo = new FileInfo(absolutePath);
                var sha256 = ComputeFileSha256(absolutePath);

                assetEntities.Add(new ChatMessageAssetEntity
                {
                    SessionId = sessionId,
                    MessageId = userMessageId,
                    Role = "user",
                    AssetGroup = assetGroup,
                    AssetKind = "attachment",
                    MimeType = attachment.MimeType,
                    OriginalName = attachment.Name,
                    RelativePath = relativePath,
                    FileSizeBytes = fileInfo.Length,
                    Sha256 = sha256,
                    SortOrder = sortOrder++,
                    SourceType = "local",
                });

                if (IsImageMimeType(attachment.MimeType))
                {
                    var imageData = await DataContent.LoadFromAsync(
                        absolutePath,
                        attachment.MimeType,
                        cancellationToken: cancellationToken);
                    contents.Add(imageData);
                    contents.Add(new TextContent($" ![{attachment.Name}]({absolutePath}) "));

                    logger.LogInformation("已导入图片附件到资源目录：{Name}（{MimeType}）",
                        attachment.Name, attachment.MimeType);
                }
                else
                {
                    contents.Add(new TextContent($" [{attachment.Name}]({attachment.Path}) "));

                    logger.LogInformation("已索引用户附件并引用原始路径：{Name}（{MimeType}）",
                        attachment.Name, attachment.MimeType);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "处理附件失败：{Path}", attachment.Path);
            }
        }

        if (assetEntities.Count == 0)
        {
            return;
        }

        try
        {
            assetService.BatchInsert(assetEntities);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "批量写入资源索引失败");
        }
    }

    private IReadOnlyList<AttachmentInfo> ExpandAttachments(IReadOnlyList<AttachmentInfo> attachments)
    {
        var expandedAttachments = new List<AttachmentInfo>();

        foreach (var attachment in attachments)
        {
            if (attachment.IsFolder)
            {
                var scanner = new FolderAttachmentScanner();
                var scanResult = scanner.Scan(attachment.Path);
                foreach (var filePath in scanResult.IncludedFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var mime = GuessMimeFromExtension(filePath);
                    expandedAttachments.Add(new AttachmentInfo(filePath, fileName, mime));
                }

                logger.LogInformation("文件夹附件已展开：{Name}（{FileCount} 文件，{TotalBytes} 字节）",
                    attachment.Name, scanResult.FileCount, scanResult.TotalBytes);
                continue;
            }

            expandedAttachments.Add(attachment);
        }

        return expandedAttachments;
    }

    private static bool IsImageMimeType(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string GuessMimeFromExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return "application/octet-stream";
        }

        return ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".cs" or ".py" or ".js" or ".ts" or ".java" or ".go" or ".rs" => "text/plain",
            ".css" or ".scss" or ".less" => "text/css",
            ".yaml" or ".yml" => "text/yaml",
            ".csv" => "text/csv",
            _ => "application/octet-stream",
        };
    }

    private static string ResolveAssetGroup(string mimeType)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "images";
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return "files";
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
