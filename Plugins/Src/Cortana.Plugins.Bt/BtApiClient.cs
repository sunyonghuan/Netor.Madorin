using System.Net.Http;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 宝塔 HTTP API 客户端。
/// 该类只负责构造请求、发送表单并返回统一结果，不承担业务判断。
/// </summary>
public sealed class BtApiClient(HttpClient httpClient, BtRequestSigner signer)
{
    /// <summary>
    /// 以表单方式调用宝塔 API。
    /// </summary>
    public async Task<BtApiResult> PostFormAsync(
        string panelUrl,
        string apiSk,
        string relativePathAndQuery,
        IReadOnlyDictionary<string, string?>? fields,
        CancellationToken ct = default)
    {
        if (!BtToolSupport.TryParsePanelUrl(panelUrl, out var baseUri) || baseUri is null)
        {
            return new BtApiResult { Success = false, StatusCode = 0, ErrorMessage = "panelUrl 无效。" };
        }

        // 宝塔接口的鉴权字段由签名器统一生成，业务参数再按需合并进去。
        var requestUri = new Uri(baseUri, relativePathAndQuery);
        var payload = signer.CreateAuthFields(apiSk);
        if (fields is not null)
        {
            foreach (var pair in fields)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    payload[pair.Key] = pair.Value!;
            }
        }

        using var content = new FormUrlEncodedContent(payload);
        using var response = await httpClient.PostAsync(requestUri, content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return new BtApiResult
        {
            Success = response.IsSuccessStatusCode,
            StatusCode = (int)response.StatusCode,
            RequestUrl = requestUri.ToString(),
            ResponseJson = responseJson,
            ErrorMessage = response.IsSuccessStatusCode ? null : response.ReasonPhrase
        };
    }
}
