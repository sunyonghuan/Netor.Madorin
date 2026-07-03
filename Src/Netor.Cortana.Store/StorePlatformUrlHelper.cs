namespace Netor.Cortana.Store;

public static class StorePlatformUrlHelper
{
    public static string NormalizeApiBaseUrl(string? baseUrl)
    {
        var trimmed = baseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !IsLocalHost(uri))
        {
            var httpsBuilder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = uri.IsDefaultPort || uri.Port == 80 ? -1 : uri.Port,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return httpsBuilder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string ResolveAccountPortalBaseUrl(string? apiBaseUrl)
    {
        var normalizedApiBaseUrl = NormalizeApiBaseUrl(apiBaseUrl);
        if (!Uri.TryCreate(normalizedApiBaseUrl, UriKind.Absolute, out var uri))
        {
            return normalizedApiBaseUrl;
        }

        if (IsLocalHost(uri) && uri.Port == 5190)
        {
            var localBuilder = new UriBuilder(uri)
            {
                Port = 5094,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return localBuilder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        var host = uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        var builder = new UriBuilder(uri)
        {
            Host = host,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static bool ShouldShowLocalTestAccount(string? apiBaseUrl)
    {
        if (!Uri.TryCreate(NormalizeApiBaseUrl(apiBaseUrl), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsLocalHost(uri);
    }

    public static bool IsLocalHost(Uri uri)
        => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
