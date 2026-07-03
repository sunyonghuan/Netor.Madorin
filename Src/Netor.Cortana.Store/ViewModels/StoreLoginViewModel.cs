using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Netor.Cortana.Store.ViewModels;

public sealed class StoreLoginViewModel(string baseUrl) : INotifyPropertyChanged
{
    private const int CaptchaLength = 4;
    private const string CaptchaAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private string _userName = string.Empty;
    private string _password = string.Empty;
    private string _captchaCode = CreateCaptchaCode();
    private string _captchaInput = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BaseUrl { get; } = StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl);

    public string AccountPortalBaseUrl => StorePlatformUrlHelper.ResolveAccountPortalBaseUrl(BaseUrl);

    public bool IsLocalTestAccountVisible => StorePlatformUrlHelper.ShouldShowLocalTestAccount(BaseUrl);

    public string LocalTestAccountText => "本地联调账号：demo@netor.me / 123456";

    public string UserName
    {
        get => _userName;
        set => SetField(ref _userName, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public string CaptchaCode
    {
        get => _captchaCode;
        private set => SetField(ref _captchaCode, value);
    }

    public string CaptchaInput
    {
        get => _captchaInput;
        set => SetField(ref _captchaInput, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public void UseLocalTestAccount()
    {
        UserName = "demo@netor.me";
        Password = "123456";
        StatusText = "已填入本地联调账号，请输入验证码后登录测试安装流程。";
    }

    public void RefreshCaptcha()
    {
        CaptchaCode = CreateCaptchaCode();
        CaptchaInput = string.Empty;
    }

    public bool ValidateCaptcha()
    {
        if (string.IsNullOrWhiteSpace(CaptchaInput))
        {
            StatusText = "请输入验证码。";
            return false;
        }

        if (string.Equals(CaptchaInput.Trim(), CaptchaCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        StatusText = "验证码不正确，请重新输入。";
        RefreshCaptcha();
        return false;
    }

    private static string CreateCaptchaCode()
    {
        Span<char> code = stackalloc char[CaptchaLength];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = CaptchaAlphabet[RandomNumberGenerator.GetInt32(CaptchaAlphabet.Length)];
        }

        return new string(code);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
