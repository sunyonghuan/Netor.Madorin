using System.CommandLine;
using System.Text;
using System.Text.Json;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class CliOutput
{
    public static bool IsJson(ParseResult parseResult, Option<bool> jsonOption) =>
        parseResult.GetValue(jsonOption);

    public static void WriteNotImplemented(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string message)
    {
        if (!IsJson(parseResult, jsonOption))
        {
            output.WriteLine(message);
            return;
        }

        WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("status", "NotImplemented");
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }

    public static void WriteJson(TextWriter output, Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeValue(writer);
        }

        output.WriteLine(Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length)));
    }
}
