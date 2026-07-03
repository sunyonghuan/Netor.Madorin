using Netor.Cortana.Store.ViewModels;

namespace Netor.Cortana.Store.Tests;

public sealed class StorePlatformUrlHelperTests
{
    [Theory]
    [InlineData("http://api.madorin.netor.me", "https://api.madorin.netor.me")]
    [InlineData("http://api.madorin.netor.me/", "https://api.madorin.netor.me")]
    [InlineData("https://api.madorin.netor.me", "https://api.madorin.netor.me")]
    [InlineData("http://localhost:5190", "http://localhost:5190")]
    public void NormalizeApiBaseUrl_ReturnsExpectedUrl(string input, string expected)
    {
        var actual = StorePlatformUrlHelper.NormalizeApiBaseUrl(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("http://api.madorin.netor.me", "https://madorin.netor.me")]
    [InlineData("https://api.madorin.netor.me", "https://madorin.netor.me")]
    [InlineData("http://madorin.netor.me", "https://madorin.netor.me")]
    [InlineData("http://api.xxx.com", "https://xxx.com")]
    [InlineData("https://api.xxx.com", "https://xxx.com")]
    [InlineData("http://localhost:5190", "http://localhost:5094")]
    public void ResolveAccountPortalBaseUrl_ReturnsExpectedPortal(string input, string expected)
    {
        var actual = StorePlatformUrlHelper.ResolveAccountPortalBaseUrl(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("http://localhost:5190", true)]
    [InlineData("http://127.0.0.1:5190", true)]
    [InlineData("https://api.madorin.netor.me", false)]
    public void StoreLoginViewModel_SetsLocalTestAccountVisibility(string input, bool expected)
    {
        var viewModel = new StoreLoginViewModel(input);

        Assert.Equal(expected, viewModel.IsLocalTestAccountVisible);
    }

    [Fact]
    public void StoreLoginViewModel_UsesPortalDomainForRegisterAndResetLinks()
    {
        var viewModel = new StoreLoginViewModel("http://api.madorin.netor.me");

        Assert.Equal("https://api.madorin.netor.me", viewModel.BaseUrl);
        Assert.Equal("https://madorin.netor.me", viewModel.AccountPortalBaseUrl);
    }

    [Fact]
    public void StoreLoginViewModel_RemovesApiSubdomainForCustomDomain()
    {
        var viewModel = new StoreLoginViewModel("http://api.xxx.com");

        Assert.Equal("https://api.xxx.com", viewModel.BaseUrl);
        Assert.Equal("https://xxx.com", viewModel.AccountPortalBaseUrl);
    }
}
