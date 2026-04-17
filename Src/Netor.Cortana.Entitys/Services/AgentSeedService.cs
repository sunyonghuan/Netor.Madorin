using Microsoft.Data.Sqlite;

using System.Text.Json;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 负责默认智能体种子数据的初始化。
/// 仅在空表时写入默认智能体；对已有数据只修复默认标记，不覆盖用户内容。
/// </summary>
public sealed class AgentSeedService(CortanaDbContext db)
{
    private const string DefaultAgentId = "agent.default.xiaoyue";

    /// <summary>
    /// 确保数据库中存在可用的默认智能体。
    /// </summary>
    public void EnsureSeedData()
    {
        var agentCount = db.ExecuteScalar<long>("SELECT COUNT(1) FROM Agents");
        if (agentCount == 0)
        {
            InsertDefaultAgent();
            return;
        }

        var defaultEnabledCount = db.ExecuteScalar<long>("SELECT COUNT(1) FROM Agents WHERE IsEnabled = 1 AND IsDefault = 1");
        if (defaultEnabledCount > 0)
        {
            return;
        }

        var firstEnabledAgentId = db.ExecuteScalar<string>("SELECT Id FROM Agents WHERE IsEnabled = 1 ORDER BY SortOrder, CreatedTimestamp DESC LIMIT 1");
        if (string.IsNullOrWhiteSpace(firstEnabledAgentId))
        {
            return;
        }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        db.ExecuteInTransaction(connection =>
        {
            using var clearDefault = connection.CreateCommand();
            clearDefault.CommandText = "UPDATE Agents SET IsDefault = 0, UpdatedTimestamp = @Now WHERE IsDefault = 1";
            clearDefault.Parameters.AddWithValue("@Now", now);
            clearDefault.ExecuteNonQuery();

            using var setDefault = connection.CreateCommand();
            setDefault.CommandText = "UPDATE Agents SET IsDefault = 1, UpdatedTimestamp = @Now WHERE Id = @Id";
            setDefault.Parameters.AddWithValue("@Now", now);
            setDefault.Parameters.AddWithValue("@Id", firstEnabledAgentId);
            setDefault.ExecuteNonQuery();
        });
    }

    private void InsertDefaultAgent()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var entity = new AgentEntity
        {
            Id = DefaultAgentId,
            CreatedTimestamp = now,
            UpdatedTimestamp = now,
            Name = "小月",
            Instructions = DefaultInstructions,
            Description = "默认配置型智能体，负责软件配置、工具接入、软件操作与执行方案说明。",
            Image = string.Empty,
            Temperature = 0.2,
            MaxTokens = 0,
            TopP = 1.0,
            FrequencyPenalty = 0,
            PresencePenalty = 0,
            MaxHistoryMessages = 20,
            IsDefault = true,
            IsEnabled = true,
            SortOrder = 0,
            EnabledPluginIds = [],
            EnabledMcpServerIds = []
        };

        db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
    }

    private const string InsertSql = """
        INSERT INTO Agents (Id, CreatedTimestamp, UpdatedTimestamp, Name, Instructions, Description, Image,
            Temperature, MaxTokens, TopP, FrequencyPenalty, PresencePenalty, MaxHistoryMessages,
            IsDefault, IsEnabled, SortOrder, EnabledPluginIds, EnabledMcpServerIds)
        VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Name, @Instructions, @Description, @Image,
            @Temperature, @MaxTokens, @TopP, @FrequencyPenalty, @PresencePenalty, @MaxHistoryMessages,
            @IsDefault, @IsEnabled, @SortOrder, @EnabledPluginIds, @EnabledMcpServerIds)
        """;

    private const string DefaultInstructions = """
        # 角色

        你是小月，是 Cortana 的默认配置型智能体。

        你的职责是帮助用户完成当前软件中的配置、切换、接入、查看和操作工作，并在需要时给出清晰可执行的方案。

        # 工作目标

        1. 优先帮助用户完成当前软件中的配置与操作任务，而不是只停留在建议层面。
        2. 对于可以通过现有工具完成的配置、切换、查看和接入操作，优先直接执行。
        3. 对于不能直接完成的操作，要明确说明限制，并给出下一步最可行的方案。
        4. 始终基于真实状态回答，不编造已经执行过的结果，不伪造文件、配置状态、工具状态或软件状态。

        # 工作方式

        1. 在动手前，先理解用户目标和上下文。
        2. 如果信息不足，先查看当前状态，再决定下一步动作。
        3. 优先使用系统已经提供的工具和能力完成任务。
        4. 完成操作后，要把实际结果告诉用户，说明做了什么、结果如何、还有什么限制。

        # 你的主要职责

        你可以协助用户完成以下类型的工作：

        1. 查看和切换 AI 厂商、模型和智能体。
        2. 查看、修改和解释智能体提示词。
        3. 配置 MCP 服务、测试连通性，并将 MCP 服务启用给智能体。
        4. 管理当前软件中的插件、工具、窗口、设置和工作区。
        5. 回答关于当前软件、当前配置和当前工具能力的问题。
        6. 在用户需要时，提供与当前软件配置和使用相关的执行方案、说明文档或操作建议。

        # 职责边界

        1. 你负责的是当前软件中的配置和操作，不负责外部服务体系本身的开通、采购、授权或部署。
        2. 如果用户尚未准备好外部服务所需的账号、密钥、地址或权限，你要明确说明这是外部前置条件，而不是假装软件内部可以替代完成。
        3. 你不虚构不存在的工具能力，也不把外部服务接入问题伪装成软件内部问题。

        # 工具使用原则

        1. 只要当前工具可以安全完成配置或操作任务，就优先使用工具，而不是只给口头建议。
        2. 使用工具前，先确认目标是否清楚，尤其是目标对象、序号、名称和影响范围。
        3. 使用工具后，基于真实结果反馈用户。
        4. 如果工具不足以完成任务，要明确告诉用户缺少什么条件，而不是假装已经完成。

        # 与用户沟通的要求

        1. 默认使用中文交流。
        2. 表达清楚、直接、专业，不说空话。
        3. 用户要方案时，给出结构清晰、可执行的方案。
        4. 用户要你执行时，优先执行，不拖成纯讨论。
        5. 对不确定的信息要明确说明，不要猜测后当成事实输出。

        # 关键操作提醒

        遇到以下情况时，先提醒用户影响，再继续执行：

        1. 删除、覆盖、移动重要文件或目录。
        2. 批量修改内容。
        3. 关闭、卸载、替换、重载插件或服务。
        4. 改动可能影响当前工作区、当前配置或已有数据的操作。
        5. 其他具有明显风险、不可逆或高影响的动作。

        # 行为约束

        1. 不伪造执行结果。
        2. 不虚构不存在的工具能力。
        3. 不把未执行的操作说成已执行。
        4. 不在信息不足时擅自做高影响操作。
        5. 始终以帮助用户完成实际任务为优先目标。
        """;

    private static void BindEntity(SqliteCommand cmd, AgentEntity entity)
    {
        cmd.Parameters.AddWithValue("@Id", entity.Id);
        cmd.Parameters.AddWithValue("@CreatedTimestamp", entity.CreatedTimestamp);
        cmd.Parameters.AddWithValue("@UpdatedTimestamp", entity.UpdatedTimestamp);
        cmd.Parameters.AddWithValue("@Name", entity.Name);
        cmd.Parameters.AddWithValue("@Instructions", entity.Instructions);
        cmd.Parameters.AddWithValue("@Description", entity.Description);
        cmd.Parameters.AddWithValue("@Image", entity.Image);
        cmd.Parameters.AddWithValue("@Temperature", entity.Temperature);
        cmd.Parameters.AddWithValue("@MaxTokens", entity.MaxTokens);
        cmd.Parameters.AddWithValue("@TopP", entity.TopP);
        cmd.Parameters.AddWithValue("@FrequencyPenalty", entity.FrequencyPenalty);
        cmd.Parameters.AddWithValue("@PresencePenalty", entity.PresencePenalty);
        cmd.Parameters.AddWithValue("@MaxHistoryMessages", entity.MaxHistoryMessages);
        cmd.Parameters.AddWithValue("@IsDefault", entity.IsDefault);
        cmd.Parameters.AddWithValue("@IsEnabled", entity.IsEnabled);
        cmd.Parameters.AddWithValue("@SortOrder", entity.SortOrder);
        cmd.Parameters.AddWithValue("@EnabledPluginIds", JsonSerializer.Serialize(entity.EnabledPluginIds, EntityJsonContext.Default.ListString));
        cmd.Parameters.AddWithValue("@EnabledMcpServerIds", JsonSerializer.Serialize(entity.EnabledMcpServerIds, EntityJsonContext.Default.ListString));
    }
}