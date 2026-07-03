using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 将会议附件导入工作区资源目录，并返回可持久化的相对路径快照。
/// </summary>
public sealed class MeetingAttachmentImporter(IAppPaths appPaths)
{
    private readonly IAppPaths _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));

    public ImportedMeetingAttachment Import(string meetingId, AttachmentInfo attachment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(attachment);

        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(attachment.Name)
            ? Path.GetFileName(attachment.Path)
            : attachment.Name);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"attachment-{Guid.NewGuid():N}";
        }

        var relativeDirectory = Path.Combine("meetings", meetingId, "attachments");
        var targetDirectory = Path.Combine(_appPaths.WorkspaceResourcesDirectory, relativeDirectory);
        Directory.CreateDirectory(targetDirectory);

        var targetName = EnsureUniqueName(targetDirectory, fileName);
        var relativePath = Path.Combine(relativeDirectory, targetName);
        var absolutePath = Path.Combine(_appPaths.WorkspaceResourcesDirectory, relativePath);

        if (Directory.Exists(attachment.Path))
        {
            CopyDirectory(attachment.Path, absolutePath);
            return new ImportedMeetingAttachment(
                targetName,
                relativePath,
                attachment.TotalBytes > 0 ? attachment.TotalBytes : GetDirectorySize(absolutePath));
        }

        if (File.Exists(attachment.Path))
        {
            File.Copy(attachment.Path, absolutePath, overwrite: false);
            return new ImportedMeetingAttachment(
                targetName,
                relativePath,
                new FileInfo(absolutePath).Length);
        }

        return new ImportedMeetingAttachment(targetName, relativePath, attachment.TotalBytes);
    }

    private static string EnsureUniqueName(string directory, string fileName)
    {
        var candidate = fileName;
        var nameOnly = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;
        while (File.Exists(Path.Combine(directory, candidate)) ||
               Directory.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{nameOnly}-{index++}{extension}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }

    private static long GetDirectorySize(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }
}

public sealed record ImportedMeetingAttachment(
    string FileName,
    string RelativePath,
    long SizeBytes);
