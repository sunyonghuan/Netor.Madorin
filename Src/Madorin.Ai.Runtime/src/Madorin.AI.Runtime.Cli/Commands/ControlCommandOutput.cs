using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ControlCommandOutput
{
    public static void WriteStatus(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        RuntimeStatusResult status)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"实例：{status.RuntimeInstanceId}");
            output.WriteLine($"版本：{status.ProgramVersion} | 协议：{status.ProtocolVersion}");
            output.WriteLine($"工作区：{status.Workspace}");
            output.WriteLine($"PID：{status.ProcessId}");
            output.WriteLine(
                $"活动 Run：{status.ActiveRunCount} | 已连接宿主：{status.ConnectedHostCount}");
            output.WriteLine($"运行时长：{FormatDuration(DateTimeOffset.UtcNow - status.StartedAt)}");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            WriteSuccessHeader(writer, "ctl status");
            writer.WritePropertyName("runtime");
            writer.WriteStartObject();
            writer.WriteString("runtimeInstanceId", status.RuntimeInstanceId);
            writer.WriteString("programVersion", status.ProgramVersion);
            writer.WriteString("protocolVersion", status.ProtocolVersion);
            writer.WriteString("workspace", status.Workspace);
            writer.WriteNumber("processId", status.ProcessId);
            writer.WriteString("startedAt", status.StartedAt);
            writer.WriteNumber("activeRunCount", status.ActiveRunCount);
            writer.WriteNumber("connectedHostCount", status.ConnectedHostCount);
            writer.WriteNumber(
                "connectedEventChannelCount",
                status.ConnectedEventChannelCount);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static void WriteSessions(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        SessionListResult result)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            if (result.Sessions.Length == 0)
            {
                output.WriteLine("没有活动 Session。");
                return;
            }

            foreach (var session in result.Sessions)
            {
                output.WriteLine(
                    $"{session.SessionId}\t{session.Mode}\t{session.Status}\t{session.UpdatedAt:O}\t{session.Title ?? string.Empty}");
            }

            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            WriteSuccessHeader(writer, "ctl sessions");
            writer.WritePropertyName("sessions");
            writer.WriteStartArray();
            foreach (var session in result.Sessions)
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WriteString("mode", session.Mode.ToString());
                writer.WriteString("status", session.Status.ToString());
                writer.WriteString("updatedAt", session.UpdatedAt);
                writer.WriteString("title", session.Title);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("nextCursor", result.NextCursor);
            writer.WriteEndObject();
        });
    }

    public static void WriteRuns(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        RunListResult result)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            if (result.Runs.Length == 0)
            {
                output.WriteLine("没有活动 Run。");
                return;
            }

            foreach (var run in result.Runs)
            {
                output.WriteLine(
                    $"{run.RunId}\t{run.SessionId}\t{run.Status}\t{FormatDuration(DateTimeOffset.UtcNow - run.StartedAt)}");
            }

            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            WriteSuccessHeader(writer, "ctl runs");
            writer.WritePropertyName("runs");
            writer.WriteStartArray();
            foreach (var run in result.Runs)
            {
                writer.WriteStartObject();
                writer.WriteString("runId", run.RunId);
                writer.WriteString("sessionId", run.SessionId);
                writer.WriteString("status", run.Status.ToString());
                writer.WriteString("startedAt", run.StartedAt);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static void WriteCancel(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        TextWriter error,
        string runId,
        bool accepted)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            var target = accepted ? output : error;
            target.WriteLine(accepted
                ? $"取消请求已接受：{runId}；最终状态尚未确认。"
                : $"取消请求被拒绝：{runId}。");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", accepted);
            writer.WriteString("command", "ctl cancel");
            writer.WriteString("runId", runId);
            writer.WriteBoolean("cancelAccepted", accepted);
            writer.WriteBoolean("cancelled", false);
            if (!accepted)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteString("code", "CancelRejected");
                writer.WriteString("message", "The Runtime rejected the cancellation request.");
                writer.WriteBoolean("isRetryable", false);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });
    }

    public static void WriteCredentialUpdate(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string runId,
        string providerId,
        string? profileId,
        DateTimeOffset? expiresAt)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"凭据已更新：Run {runId} | Provider {providerId}");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            WriteSuccessHeader(writer, "ctl credential update");
            writer.WriteString("runId", runId);
            writer.WriteString("providerId", providerId);
            writer.WriteString("profileId", profileId);
            if (expiresAt is { } expiration)
            {
                writer.WriteString("expiresAt", expiration);
            }

            writer.WriteEndObject();
        });
    }

    public static void WriteError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        TextWriter error,
        string code,
        string message,
        bool isRetryable)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            error.WriteLine(message);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteBoolean("isRetryable", isRetryable);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void WriteSuccessHeader(Utf8JsonWriter writer, string command)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("success", true);
        writer.WriteString("command", command);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{duration.Seconds}s";
    }
}
