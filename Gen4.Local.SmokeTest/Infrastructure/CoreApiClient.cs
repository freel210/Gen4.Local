using System.Net.Http.Json;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public sealed record TokenResult(string AccessToken, string RefreshToken);

public static class CoreApiClient
{
    public static async Task<TokenResult> GetTokenAsync(HttpClient client, string path, string login, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { login, password }),
        };

        var clientType = path[(path.LastIndexOf('/') + 1)..];
        return await SendAsync(client, request, $"{clientType} login", clientType);
    }

    public static async Task<TokenResult> RefreshAsync(HttpClient client, string clientType, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/core/api/tokens/{clientType}/refresh");
        request.Headers.TryAddWithoutValidation("Cookie", $"{GetCookieName(clientType)}={refreshToken}");
        return await SendAsync(client, request, $"{clientType} refresh", clientType);
    }

    private static async Task<TokenResult> SendAsync(HttpClient client, HttpRequestMessage request, string what, string clientType)
    {
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{what} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var accessToken = doc.RootElement.TryGetProperty("accessToken", out var token)
            ? token.GetString()
            : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException($"{what}: response has no accessToken: {body}");
        }

        var refreshToken = ReadCookie(response, GetCookieName(clientType));
        return new TokenResult(accessToken, refreshToken);
    }

    private static string GetCookieName(string clientType)
    {
        var normalized = char.ToUpperInvariant(clientType[0]) + clientType[1..];
        return $"{normalized}RefreshToken";
    }

    private static string ReadCookie(HttpResponseMessage response, string cookieName)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            foreach (var header in headers)
            {
                var pair = header.Split(';')[0];
                var separator = pair.IndexOf('=');
                if (separator > 0 && string.Equals(pair[..separator].Trim(), cookieName, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[(separator + 1)..].Trim());
                }
            }
        }

        throw new InvalidOperationException($"Response does not set the {cookieName} cookie.");
    }
}
