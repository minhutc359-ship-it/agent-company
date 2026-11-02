using System.Net.Http.Headers;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record CloudCatalogResult(bool Available, string[] Models, bool ConfiguredModelListed, string Detail);
public sealed class CloudModelCatalog(Func<HttpMessageHandler>? handlerFactory = null)
{
    public async Task<CloudCatalogResult> CheckAsync(CloudProviderConfiguration provider, string secret, CancellationToken ct)
    {
        var endpoint = CloudProviderPolicy.Resolve(provider.ProviderType);
        var path = provider.ProviderType == "gemini" ? "/v1beta/models?pageSize=1000" : "/v1/models";
        using var http = new HttpClient(handlerFactory?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + endpoint.Host + path);
        if (provider.ProviderType is "openai" or "anthropic") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        else request.Headers.TryAddWithoutValidation("x-goog-api-key", secret);
        if (provider.ProviderType == "anthropic") request.Headers.TryAddWithoutValidation("anthropic-version", endpoint.Version);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new(false, [], false, $"HTTP {(int)response.StatusCode}: kiểm tra credential/quyền truy cập của tài khoản.");
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return new(false, [], false, "Danh sách model vượt giới hạn.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream(); var buffer = new byte[8192]; int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0) { if (bytes.Length + n > 2 * 1024 * 1024) return new(false, [], false, "Danh sách model vượt giới hạn."); bytes.Write(buffer, 0, n); }
            using var json = JsonDocument.Parse(bytes.ToArray()); var field = provider.ProviderType == "gemini" ? "models" : "data";
            var names = json.RootElement.GetProperty(field).EnumerateArray().Select(m => m.GetProperty(provider.ProviderType == "gemini" ? "name" : "id").GetString() ?? "")
                .Select(s => s.StartsWith("models/", StringComparison.Ordinal) ? s[7..] : s).Where(s => s.Length is > 0 and <= 160 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')).Distinct().Order().Take(300).ToArray();
            return new(true, names, names.Contains(provider.ModelId), "Credential đọc được danh sách model; chưa gửi nội dung workspace hay gọi sinh văn bản. Khả năng sinh đúng schema vẫn cần nghiệm thu bằng payload đã duyệt.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return new(false, [], false, "Không đọc được danh sách model. Kiểm tra kết nối, API và quyền tài khoản."); }
    }
}
