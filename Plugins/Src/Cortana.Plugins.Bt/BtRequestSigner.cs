using System.Security.Cryptography;
using System.Text;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 负责生成宝塔 API 所需的 request_time 和 request_token。
/// </summary>
public sealed class BtRequestSigner
{
    /// <summary>
    /// 根据 apiSk 生成本次请求使用的鉴权字段。
    /// </summary>
    public Dictionary<string, string> CreateAuthFields(string apiSk)
    {
        var requestTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var token = ComputeMd5(requestTime + ComputeMd5(apiSk));
        return new Dictionary<string, string>
        {
            ["request_time"] = requestTime,
            ["request_token"] = token
        };
    }

    /// <summary>
    /// 计算 32 位小写 MD5 值。
    /// </summary>
    private static string ComputeMd5(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
