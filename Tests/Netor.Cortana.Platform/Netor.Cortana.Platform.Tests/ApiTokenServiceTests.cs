using Netor.Cortana.Platform.Api.Security;

namespace Netor.Cortana.Platform.Tests;

public sealed class ApiTokenServiceTests
{
    [Fact]
    public void TryValidateToken_WithValidToken_ReturnsAccountId()
    {
        var service = CreateService();
        var token = service.CreateToken("account-1", DateTimeOffset.UtcNow.AddMinutes(5));

        var valid = service.TryValidateToken(token, out var accountId);

        Assert.True(valid);
        Assert.Equal("account-1", accountId);
    }

    [Fact]
    public void TryValidateToken_WithExpiredToken_ReturnsFalse()
    {
        var service = CreateService();
        var token = service.CreateToken("account-1", DateTimeOffset.UtcNow.AddMinutes(-1));

        var valid = service.TryValidateToken(token, out var accountId);

        Assert.False(valid);
        Assert.Equal(string.Empty, accountId);
    }

    [Fact]
    public void TryValidateToken_WithMalformedSignature_ReturnsFalse()
    {
        var service = CreateService();
        var token = service.CreateToken("account-1", DateTimeOffset.UtcNow.AddMinutes(5));
        var malformedToken = $"{token.Split('.')[0]}.x";

        var valid = service.TryValidateToken(malformedToken, out var accountId);

        Assert.False(valid);
        Assert.Equal(string.Empty, accountId);
    }

    private static ApiTokenService CreateService()
        => new("test-signing-key");
}
