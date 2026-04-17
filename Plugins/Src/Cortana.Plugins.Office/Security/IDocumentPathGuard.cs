namespace Cortana.Plugins.Office.Security;

/// <summary>
/// 文档路径安全校验接口。
/// 负责防止路径穿越和越权访问，确保操作限制在白名单目录内。
/// </summary>
public interface IDocumentPathGuard
{
    /// <summary>
    /// 校验路径是否在允许目录内。合法时返回规范化绝对路径，否则返回 null。
    /// </summary>
    string? ValidatePath(string? path);
}
