namespace Netor.Cortana.Store.Models;

public sealed record PlatformAccountSession(
    string BaseUrl,
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    ApiAccountResponse Account,
    bool IsExpired = false);

public sealed record ApiLoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, ApiAccountResponse Account);

public sealed record ApiAccountResponse(string Id, long No, string LoginUserName, string NickName, string Email, string Phone);
