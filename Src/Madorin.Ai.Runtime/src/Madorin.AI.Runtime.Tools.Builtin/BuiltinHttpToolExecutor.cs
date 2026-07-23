using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Performs policy-bound HTTP requests with pinned DNS results and manual redirects.</summary>
public sealed class BuiltinHttpToolExecutor : IToolExecutor
{
    private const int DefaultMaxResponseBytes = 1024 * 1024;
    private const int HardMaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxRequestBodyBytes = 1024 * 1024;
    private const int MaxRequestHeaders = 64;
    private const int MaxResponseHeaders = 128;

    private static readonly HashSet<string> ForbiddenRequestHeaders = new(
        [
            "connection",
            "content-length",
            "expect",
            "host",
            "keep-alive",
            "proxy-authenticate",
            "proxy-authorization",
            "te",
            "trailer",
            "transfer-encoding",
            "upgrade"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SensitiveRedirectHeaders = new(
        ["authorization", "cookie", "proxy-authorization"],
        StringComparer.OrdinalIgnoreCase);

    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.HttpRequestToolId,
        StringComparison.Ordinal);

    public async ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The HTTP executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The tool permission grant belongs to another Run.");
        }

        var policy = permission.NetworkPolicy
            ?? new NetworkPolicy(permission.DenyNetwork, []);
        if (permission.DenyNetwork || policy.DenyAll)
        {
            throw new UnauthorizedAccessException("Network access is denied by the effective Grant.");
        }

        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var arguments = document.RootElement;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments must be a JSON object.");
        }

        var method = ParseMethod(GetRequiredString(arguments, "method"));
        var url = GetRequiredString(arguments, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Tool argument 'url' must be an absolute URI.");
        }

        var headers = ParseHeaders(arguments);
        var body = GetOptionalString(arguments, "body");
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaxRequestBodyBytes)
        {
            throw new InvalidDataException("The HTTP request body exceeds the 1 MB limit.");
        }

        var endpoint = await ValidateAndResolveAsync(uri, policy, ct).ConfigureAwait(false);
        return new PreparedHttpExecution(
            invocation,
            method,
            endpoint,
            headers,
            body,
            policy,
            $"http:{endpoint.Uri.Scheme}://{endpoint.Uri.IdnHost}:{endpoint.Uri.Port}");
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        try
        {
            await using var prepared = await PrepareAsync(invocation, ct).ConfigureAwait(false);
            return await prepared.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidDataException
            or IOException
            or HttpRequestException
            or SocketException
            or UnauthorizedAccessException)
        {
            return new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: ex.Message);
        }
    }

    private static async Task<ToolResult> SendAsync(
        ToolInvocation invocation,
        HttpMethod initialMethod,
        ValidatedEndpoint initialEndpoint,
        IReadOnlyDictionary<string, string> initialHeaders,
        string? initialBody,
        NetworkPolicy policy,
        CancellationToken ct)
    {
        var maxRedirects = policy.MaxRedirects ?? 0;
        if (maxRedirects is < 0 or > 10)
        {
            throw new InvalidDataException("The HTTP redirect limit must be between 0 and 10.");
        }

        var maxResponseBytes = policy.MaxResponseBytes ?? DefaultMaxResponseBytes;
        if (maxResponseBytes is < 1 or > HardMaxResponseBytes)
        {
            throw new InvalidDataException("The HTTP response limit must be between 1 byte and 4 MB.");
        }

        var method = initialMethod;
        var endpoint = initialEndpoint;
        var headers = new Dictionary<string, string>(initialHeaders, StringComparer.OrdinalIgnoreCase);
        var body = initialBody;
        for (var redirect = 0; ; redirect++)
        {
            using var handler = CreateHandler(endpoint);
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var request = CreateRequest(method, endpoint.Uri, headers, body);
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct)
                .ConfigureAwait(false);
            if (TryGetRedirect(response, endpoint.Uri, out var redirectUri))
            {
                if (redirect >= maxRedirects)
                {
                    throw new HttpRequestException("The HTTP redirect limit was exceeded.");
                }

                var previousAuthority = endpoint.Uri.GetComponents(
                    UriComponents.SchemeAndServer,
                    UriFormat.UriEscaped);
                endpoint = await ValidateAndResolveAsync(redirectUri, policy, ct)
                    .ConfigureAwait(false);
                var nextAuthority = endpoint.Uri.GetComponents(
                    UriComponents.SchemeAndServer,
                    UriFormat.UriEscaped);
                if (!string.Equals(previousAuthority, nextAuthority, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sensitive in SensitiveRedirectHeaders)
                    {
                        headers.Remove(sensitive);
                    }
                }

                if (response.StatusCode is HttpStatusCode.SeeOther
                    || response.StatusCode is HttpStatusCode.MovedPermanently
                        or HttpStatusCode.Found
                        && method == HttpMethod.Post)
                {
                    method = HttpMethod.Get;
                    body = null;
                }

                continue;
            }

            var bytes = await ReadResponseBodyAsync(
                response.Content,
                checked((int)maxResponseBytes),
                ct).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var isText = IsTextMediaType(mediaType);
            var responseBody = isText
                ? DecodeResponseText(bytes, response.Content.Headers.ContentType?.CharSet)
                : Convert.ToBase64String(bytes);
            var responseHeaders = CollectResponseHeaders(response, isText);
            var output = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("statusCode", (int)response.StatusCode);
                writer.WriteStartObject("headers");
                foreach (var header in responseHeaders.OrderBy(
                             static item => item.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteString(header.Key, header.Value);
                }

                writer.WriteEndObject();
                writer.WriteString("body", responseBody);
                if (mediaType is null)
                {
                    writer.WriteNull("mediaType");
                }
                else
                {
                    writer.WriteString("mediaType", mediaType);
                }

                writer.WriteEndObject();
            });
            return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
        }
    }

    private static SocketsHttpHandler CreateHandler(ValidatedEndpoint endpoint) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            if (!string.Equals(
                    context.DnsEndPoint.Host,
                    endpoint.Uri.IdnHost,
                    StringComparison.OrdinalIgnoreCase)
                || context.DnsEndPoint.Port != endpoint.Uri.Port)
            {
                throw new UnauthorizedAccessException(
                    "The HTTP connection target differs from the validated endpoint.");
            }

            Exception? lastError = null;
            foreach (var address in endpoint.Addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(address, endpoint.Uri.Port),
                        ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    lastError = ex;
                    socket.Dispose();
                }
            }

            throw new HttpRequestException(
                "No authorized IP address accepted the HTTP connection.",
                lastError);
        }
    };

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? body)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
        }

        foreach (var header in headers)
        {
            var added = request.Headers.TryAddWithoutValidation(header.Key, header.Value)
                || request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value) == true;
            if (!added)
            {
                request.Dispose();
                throw new ArgumentException($"HTTP header '{header.Key}' is not valid.");
            }
        }

        return request;
    }

    private static bool TryGetRedirect(
        HttpResponseMessage response,
        Uri requestUri,
        out Uri redirectUri)
    {
        redirectUri = null!;
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect))
        {
            return false;
        }

        var location = response.Headers.Location
            ?? throw new HttpRequestException("The redirect response has no Location header.");
        redirectUri = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        return true;
    }

    private static async Task<ValidatedEndpoint> ValidateAndResolveAsync(
        Uri uri,
        NetworkPolicy policy,
        CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new UnauthorizedAccessException("The HTTP URI is not allowed.");
        }

        var allowedSchemes = policy.AllowedSchemes ?? ["https"];
        if (!allowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"HTTP scheme '{uri.Scheme}' is not allowed by the effective Grant.");
        }

        if (!(policy.AllowedHosts ?? []).Any(pattern => HostMatches(pattern, uri.IdnHost)))
        {
            throw new UnauthorizedAccessException(
                $"HTTP host '{uri.IdnHost}' is not allowed by the effective Grant.");
        }

        if (policy.AllowedPorts is { } ports)
        {
            if (!ports.Contains(uri.Port))
            {
                throw new UnauthorizedAccessException(
                    $"HTTP port {uri.Port} is not allowed by the effective Grant.");
            }
        }
        else if (!uri.IsDefaultPort)
        {
            throw new UnauthorizedAccessException(
                "A non-default HTTP port requires an explicit port allowlist.");
        }

        IPAddress[] resolved;
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            resolved = [literal];
        }
        else
        {
            resolved = await Dns.GetHostAddressesAsync(uri.IdnHost, ct).ConfigureAwait(false);
        }

        var ranges = ParseRanges(policy.AllowedAddressRanges);
        var authorized = resolved
            .Distinct()
            .Where(address => ranges.Length > 0
                ? ranges.Any(range => range.Contains(address))
                : IsPublicAddress(address))
            .ToArray();
        if (authorized.Length == 0)
        {
            throw new UnauthorizedAccessException(
                $"HTTP host '{uri.IdnHost}' resolved only to unauthorized address ranges.");
        }

        return new ValidatedEndpoint(uri, authorized);
    }

    private static bool HostMatches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        pattern = pattern.TrimEnd('.');
        host = host.TrimEnd('.');
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }

        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }

    private static CidrRange[] ParseRanges(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }

        return values.Select(CidrRange.Parse).ToArray();
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        var ipv6 = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !IPAddress.IsLoopback(address)
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.Equals(IPAddress.IPv6Any)
            && (ipv6[0] & 0xFE) != 0xFC
            && !(ipv6[0] == 0x20
                && ipv6[1] == 0x01
                && ipv6[2] == 0x0D
                && ipv6[3] == 0xB8);
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("headers", out var property))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool argument 'headers' must be an object.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in property.EnumerateObject())
        {
            if (result.Count >= MaxRequestHeaders
                || ForbiddenRequestHeaders.Contains(header.Name)
                || !IsValidHeaderValue(header.Value))
            {
                throw new ArgumentException($"HTTP header '{header.Name}' is not allowed.");
            }

            result.Add(header.Name, header.Value.GetString()!);
        }

        return result;
    }

    private static bool IsValidHeaderValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        return text is not null
            && text.Length <= 8192
            && !text.Contains('\r', StringComparison.Ordinal)
            && !text.Contains('\n', StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CollectResponseHeaders(
        HttpResponseMessage response,
        bool isText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (result.Count >= MaxResponseHeaders)
            {
                throw new InvalidDataException("The HTTP response has too many headers.");
            }

            var value = string.Join(", ", header.Value);
            if (value.Length > 8192)
            {
                throw new InvalidDataException(
                    $"HTTP response header '{header.Key}' exceeds the limit.");
            }

            result[header.Key] = value;
        }

        if (!isText)
        {
            result["x-madorin-body-encoding"] = "base64";
        }

        return result;
    }

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("The HTTP response exceeds the configured limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("The HTTP response exceeds the configured limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static bool IsTextMediaType(string? mediaType) => mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase));

    private static string DecodeResponseText(byte[] bytes, string? charset)
    {
        var encoding = charset?.Trim(' ', '"').ToLowerInvariant() switch
        {
            null or "" or "utf-8" or "utf8" => Encoding.UTF8,
            "us-ascii" or "ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin1" => Encoding.Latin1,
            "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => throw new InvalidDataException(
                $"HTTP response charset '{charset}' is not supported.")
        };
        return encoding.GetString(bytes);
    }

    private static HttpMethod ParseMethod(string value) => value switch
    {
        "GET" => HttpMethod.Get,
        "HEAD" => HttpMethod.Head,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        _ => throw new ArgumentException($"HTTP method '{value}' is not supported.")
    };

    private static string GetRequiredString(JsonElement arguments, string propertyName) =>
        GetOptionalString(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");

    private static string? GetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private sealed class PreparedHttpExecution(
        ToolInvocation invocation,
        HttpMethod method,
        ValidatedEndpoint endpoint,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        NetworkPolicy policy,
        string targetSummary) : IPreparedToolExecution
    {
        private int _disposed;

        public string TargetSummary { get; } = targetSummary;

        public Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return SendAsync(invocation, method, endpoint, headers, body, policy, ct);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ValidatedEndpoint(Uri Uri, IPAddress[] Addresses);

    private sealed record CidrRange(byte[] Network, int PrefixLength)
    {
        public static CidrRange Parse(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            if (!IPAddress.TryParse(parts[0], out var address))
            {
                throw new ArgumentException($"Network range '{value}' has an invalid address.");
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var bytes = address.GetAddressBytes();
            var maxPrefix = bytes.Length * 8;
            var prefix = parts.Length == 1
                ? maxPrefix
                : int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : -1;
            if (prefix < 0 || prefix > maxPrefix)
            {
                throw new ArgumentException($"Network range '{value}' has an invalid prefix.");
            }

            return new CidrRange(bytes, prefix);
        }

        public bool Contains(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var candidate = address.GetAddressBytes();
            if (candidate.Length != Network.Length)
            {
                return false;
            }

            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            if (!candidate.AsSpan(0, wholeBytes).SequenceEqual(Network.AsSpan(0, wholeBytes)))
            {
                return false;
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (candidate[wholeBytes] & mask) == (Network[wholeBytes] & mask);
        }
    }
}
