using Microsoft.Extensions.DependencyInjection;

using System.Runtime.InteropServices;

namespace Netor.Cortana.Pages;

/// <summary>
/// 暴露给 JavaScript 的主窗口桥接对象，提供数据查询和选择回调。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public partial class MainBridgeHostObject
{
    private AiProviderService ProviderService => App.Services.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => App.Services.GetRequiredService<AiModelService>();
    private AiModelFetcherService ModelFetcher => App.Services.GetRequiredService<AiModelFetcherService>();
    private AgentService AgentService => App.Services.GetRequiredService<AgentService>();
    private ChatMessageService MessageService => App.Services.GetRequiredService<ChatMessageService>();

    // ──────── 数据查询 ────────

    /// <summary>
    /// 获取所有已启用的厂商列表（JSON）。
    /// </summary>
    public string GetProviders()
    {
        var list = ProviderService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 获取指定厂商下已启用的模型列表（JSON）。
    /// 若数据库中无该厂商的模型，则自动从远程 API 拉取并保存后返回。
    /// </summary>
    public string GetModels(string providerId)
    {
        var list = ModelService.GetByProviderId(providerId);

        if (list.Count == 0)
        {
            var provider = ProviderService.GetById(providerId);
            if (provider is not null)
            {
                try
                {
                    _ = ModelFetcher.FetchAndSaveModelsAsync(provider).ConfigureAwait(false);
                }
                catch
                {
                    // 网络拉取失败时返回空列表，避免阻塞前端
                }
            }
        }

        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 获取所有已启用的智能体列表（JSON）。
    /// </summary>
    public string GetAgents()
    {
        var list = AgentService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 获取会话历史列表（JSON），按最后活跃时间倒序。
    /// </summary>
    public string GetSessions()
    {
        var db = App.Services.GetRequiredService<CortanaDbContext>();
        var list = db.Query(
            "SELECT * FROM ChatSessions WHERE IsArchived = 0 ORDER BY IsPinned DESC, LastActiveTimestamp DESC",
            ReadSessionEntity);
        return JsonSerializer.Serialize(list);
    }

    private static ChatSessionEntity ReadSessionEntity(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new ChatSessionEntity
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Categorize = r.GetString(r.GetOrdinal("Categorize")),
            Title = r.GetString(r.GetOrdinal("Title")),
            Summary = r.GetString(r.GetOrdinal("Summary")),
            RawDiscription = r.GetString(r.GetOrdinal("RawDiscription")),
            AgentId = r.GetString(r.GetOrdinal("AgentId")),
            IsArchived = r.GetInt64(r.GetOrdinal("IsArchived")) != 0,
            IsPinned = r.GetInt64(r.GetOrdinal("IsPinned")) != 0,
            LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveTimestamp")),
            TotalTokenCount = r.GetInt32(r.GetOrdinal("TotalTokenCount"))
        };
    }

    /// <summary>
    /// 获取指定会话下的所有消息（JSON），按创建时间正序。
    /// </summary>
    public string GetMessages(string sessionId)
    {
        var list = MessageService.GetBySessionId(sessionId);
        return JsonSerializer.Serialize(list);
    }

    // ──────── 选择回调（AI 交互预留） ────────

    /// <summary>
    /// 前端切换智能体时的回调，后续接入 AI 交互时实现。
    /// </summary>
    public void OnAgentChanged(string agentId)
    {
        App.Services.GetRequiredService<AiChatHostedService>().ChangeAgent(agentId);
    }

    /// <summary>
    /// 前端切换厂商时的回调，后续接入 AI 交互时实现。
    /// </summary>
    public void OnProviderChanged(string providerId)
    {
        App.Services.GetRequiredService<AiChatHostedService>().ChangeProvider(providerId);
    }

    /// <summary>
    /// 前端切换模型时的回调，后续接入 AI 交互时实现。
    /// </summary>
    public void OnModelChanged(string modelId)
    {
        App.Services.GetRequiredService<AiChatHostedService>().ChangeModel(modelId);
    }

    /// <summary>
    /// 前端切换会话时的回调，后续接入 AI 交互时实现。
    /// </summary>
    public void OnSessionChanged(string sessionId)
    {
        _ = App.Services.GetRequiredService<AiChatHostedService>().ResumeSessionAsync(sessionId);
    }

    // ──────── 窗口操作 ────────

    /// <summary>
    /// 打开设置界面。
    /// </summary>
    public void OpenSettings()
    {
        App.MainWindow?.Invoke(() =>
        {
            var settings = App.Services.GetRequiredService<SettingsWindow>();
            settings.Show();
            settings.Activate();
        });
    }

    private AiChatHostedService ChatService => App.Services.GetRequiredService<AiChatHostedService>();

    /// <summary>
    /// 获取 WebSocket 服务器端口，供前端建立 WS 连接。
    /// </summary>
    public int GetWsPort()
    {
        return App.Services.GetRequiredService<IChatTransport>().Port;
    }

    /// <summary>
    /// 创建新会话。
    /// </summary>
    public void NewSession()
    {
        _ = ChatService.NewSessionAsync();
    }

    /// <summary>
    /// 恢复指定的历史会话。
    /// </summary>
    public void ResumeSession(string sessionId)
    {
        _ = ChatService.ResumeSessionAsync(sessionId);
    }

    /// <summary>
    /// 中止当前正在进行的 AI 流式响应。
    /// </summary>
    public void StopGeneration()
    {
        ChatService.Stop();
    }

    // ──────── 文件选择 ────────

    /// <summary>
    /// 异步打开文件选择对话框。
    /// 在 UI 线程上弹出对话框，选择完成后通过 JS 回调 <c>onFilesSelected(json)</c> 返回结果。
    /// 避免在 COM 同步调用中执行模态对话框导致死锁/闪退。
    /// </summary>
    public void OpenFileDialog()
    {
        _ = Task.Run(() =>
        {
            App.MainWindow?.Invoke(() =>
            {
                using OpenFileDialog dialog = new()
                {
                    Multiselect = true,
                    Title = "选择附件",
                    Filter = "所有文件 (*.*)|*.*|图片文件 (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|文档文件 (*.pdf;*.doc;*.docx;*.txt;*.csv;*.xlsx)|*.pdf;*.doc;*.docx;*.txt;*.csv;*.xlsx"
                };

                string json = "[]";

                if (dialog.ShowDialog(App.MainWindow) == DialogResult.OK)
                {
                    var files = dialog.FileNames.Select(path => new
                    {
                        path,
                        name = Path.GetFileName(path),
                        type = GetMimeType(path)
                    });

                    json = JsonSerializer.Serialize(files);
                }

                // 通过 MainWindow 的 ExecuteJavaScript 回调前端
                if (App.MainWindow is MainWindow mainWindow)
                {
                    var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'");
                    mainWindow.ExecuteJavaScript($"typeof onFilesSelected==='function'&&onFilesSelected('{escaped}')");
                }
            });
        });
    }

    /// <summary>
    /// 根据文件扩展名推断 MIME 类型。
    /// </summary>
    internal static string GetMimeType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            ".rar" => "application/vnd.rar",
            _ => "application/octet-stream"
        };
    }
}