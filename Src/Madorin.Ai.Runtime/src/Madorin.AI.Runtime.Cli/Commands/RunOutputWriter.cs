using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed class RunOutputWriter(
    TextWriter output,
    TextWriter error,
    RunOutputFormat format,
    bool noStream,
    bool machineOutputRedirected,
    Func<string, string> redact)
{
    private readonly StringBuilder _text = new();
    private string? _runId;
    private string? _sessionId;

    public async ValueTask<RunOutputResult?> WriteEventAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken ct)
    {
        switch (envelope.MessageType)
        {
            case MessageTypes.RunAccepted:
                var accepted = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunAcceptedEvent);
                _sessionId = accepted?.SessionId ?? _sessionId;
                _runId = accepted?.RunId ?? envelope.RunId;
                break;

            case MessageTypes.TextDelta:
                var delta = envelope.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent);
                if (delta is not null)
                {
                    _text.Append(delta.Delta);
                    if (format is RunOutputFormat.Text && !noStream)
                    {
                        await output.WriteAsync(delta.Delta.AsMemory(), ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                }

                break;

            case MessageTypes.RunCompleted:
                var completed = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunCompletedEvent);
                _sessionId = completed?.SessionId ?? _sessionId;
                _runId = completed?.RunId ?? envelope.RunId;
                await WriteSuccessAsync(envelope, ct).ConfigureAwait(false);
                return new RunOutputResult(ExitCodes.Success, _sessionId, _runId);

            case MessageTypes.RunFailed:
                var failed = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunFailedEvent);
                _sessionId = failed?.SessionId ?? _sessionId;
                _runId = failed?.RunId ?? envelope.RunId;
                var runtimeError = failed?.Error;
                await WriteFailureAsync(
                        envelope,
                        runtimeError?.Code ?? "RunFailed",
                        runtimeError?.Message ?? "unknown error",
                        runtimeError?.IsRetryable ?? false,
                        runtimeError?.DiagnosticId,
                        ct)
                    .ConfigureAwait(false);
                return new RunOutputResult(ExitCodes.GeneralError, _sessionId, _runId);

            case MessageTypes.RunCancelled:
                var cancelled = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunCancelledEvent);
                _sessionId = cancelled?.SessionId ?? _sessionId;
                _runId = cancelled?.RunId ?? envelope.RunId;
                await WriteFailureAsync(
                        envelope,
                        "RunCancelled",
                        "Run cancelled.",
                        isRetryable: false,
                        diagnosticId: null,
                        ct)
                    .ConfigureAwait(false);
                return new RunOutputResult(ExitCodes.UserInterrupted, _sessionId, _runId);
        }

        if (format is RunOutputFormat.JsonLines && !noStream)
        {
            await WriteEventLineAsync(envelope, ct).ConfigureAwait(false);
        }

        return null;
    }

    public ValueTask<RunOutputResult> WriteExceptionAsync(
        Exception exception,
        int exitCode,
        CancellationToken ct) =>
        WriteExternalFailureAsync(
            exception is OperationCanceledException ? "RunCancelled" : "RunFailed",
            exception is OperationCanceledException ? "Run cancelled." : exception.Message,
            exitCode,
            ct);

    public ValueTask<RunOutputResult> WriteUnexpectedEndAsync(CancellationToken ct) =>
        WriteExternalFailureAsync(
            "EventStreamEnded",
            "The Runtime event stream ended before a terminal event.",
            ExitCodes.GeneralError,
            ct);

    public static void WriteCommandFailure(
        TextWriter output,
        TextWriter error,
        RunOutputFormat format,
        bool machineOutputRedirected,
        string code,
        string message)
    {
        var destination = format is RunOutputFormat.Text || machineOutputRedirected
            ? error
            : output;
        if (format is RunOutputFormat.Text)
        {
            destination.WriteLine(message);
            return;
        }

        var diagnosticId = CreateDiagnosticId();
        WriteJsonLine(destination, writer =>
            WriteFailureEnvelope(
                writer,
                code,
                message,
                isRetryable: false,
                diagnosticId,
                envelope: null));
    }

    private async ValueTask WriteSuccessAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken ct)
    {
        if (format is RunOutputFormat.Text)
        {
            if (noStream)
            {
                await output.WriteAsync(_text.ToString().AsMemory(), ct).ConfigureAwait(false);
            }

            if (_text.Length == 0 || _text[^1] != '\n')
            {
                await output.WriteLineAsync().ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        await WriteJsonLineAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            WriteNullableString(writer, "sessionId", _sessionId);
            WriteNullableString(writer, "runId", _runId);
            writer.WriteString("text", _text.ToString());
            if (format is RunOutputFormat.JsonLines)
            {
                writer.WritePropertyName("event");
                WriteEvent(writer, envelope);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("warnings");
            writer.WriteEndArray();
            writer.WriteNull("diagnosticId");
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask WriteFailureAsync(
        RuntimeEventEnvelope? envelope,
        string code,
        string message,
        bool isRetryable,
        string? diagnosticId,
        CancellationToken ct)
    {
        var redactedMessage = redact(message);
        if (format is RunOutputFormat.Text)
        {
            if (!noStream && _text.Length > 0 && _text[^1] != '\n')
            {
                await output.WriteLineAsync().ConfigureAwait(false);
            }

            var humanMessage = code == "RunCancelled"
                ? redactedMessage
                : $"Run failed: {redactedMessage}";
            await error.WriteLineAsync(humanMessage).ConfigureAwait(false);
            await error.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        var destination = machineOutputRedirected ? error : output;
        var resolvedDiagnosticId = string.IsNullOrWhiteSpace(diagnosticId)
            ? CreateDiagnosticId()
            : diagnosticId;
        await WriteJsonLineAsync(destination, writer =>
            WriteFailureEnvelope(
                writer,
                code,
                redactedMessage,
                isRetryable,
                resolvedDiagnosticId,
                format is RunOutputFormat.JsonLines ? envelope : null), ct).ConfigureAwait(false);
    }

    private async ValueTask<RunOutputResult> WriteExternalFailureAsync(
        string code,
        string message,
        int exitCode,
        CancellationToken ct)
    {
        await WriteFailureAsync(
                envelope: null,
                code,
                message,
                isRetryable: false,
                diagnosticId: null,
                ct)
            .ConfigureAwait(false);
        return new RunOutputResult(exitCode, _sessionId, _runId);
    }

    private ValueTask WriteEventLineAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken ct) =>
        WriteJsonLineAsync(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNull("success");
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WritePropertyName("event");
            WriteEvent(writer, envelope);
            writer.WriteEndObject();
            writer.WriteStartArray("warnings");
            writer.WriteEndArray();
            writer.WriteNull("diagnosticId");
            writer.WriteEndObject();
        }, ct);

    private static void WriteFailureEnvelope(
        Utf8JsonWriter writer,
        string code,
        string message,
        bool isRetryable,
        string diagnosticId,
        RuntimeEventEnvelope? envelope)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("success", false);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteBoolean("isRetryable", isRetryable);
        writer.WriteString("diagnosticId", diagnosticId);
        writer.WriteEndObject();
        writer.WriteStartArray("warnings");
        writer.WriteEndArray();
        writer.WriteString("diagnosticId", diagnosticId);
        if (envelope is not null)
        {
            writer.WritePropertyName("event");
            WriteEvent(writer, envelope);
        }

        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, RuntimeEventEnvelope envelope)
    {
        writer.WriteStartObject();
        writer.WriteString("runtimeInstanceId", envelope.RuntimeInstanceId);
        writer.WriteNumber("gsn", envelope.Gsn);
        writer.WriteString("runId", envelope.RunId);
        writer.WriteNumber("runSequence", envelope.RunSequence);
        writer.WriteString("messageType", envelope.MessageType);
        writer.WriteString("timestamp", envelope.Timestamp);
        writer.WritePropertyName("payload");
        envelope.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static async ValueTask WriteJsonLineAsync(
        TextWriter destination,
        Action<Utf8JsonWriter> writeValue,
        CancellationToken ct)
    {
        var json = CreateJson(writeValue);
        await destination.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteJsonLine(
        TextWriter destination,
        Action<Utf8JsonWriter> writeValue) =>
        destination.WriteLine(CreateJson(writeValue));

    private static string CreateJson(Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeValue(writer);
        }

        return Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
    }

    private static string CreateDiagnosticId() => $"diag_{Guid.NewGuid():N}";
}
