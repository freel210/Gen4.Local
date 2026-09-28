using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public sealed record TokenResult(string AccessToken, string RefreshToken, long? CookieMaxAgeSeconds = null);

public static class CoreApiClient
{
    // Gen4.HP.Auth.AuthDefines.CookieNames.PWALongLifeRefreshTokenCookieName - the constant's value
    // is the cookie name itself, not a name built from the client type.
    private const string PwaRefreshTokenCookieName = "PWALongLifeRefreshTokenCookieName";

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

    public static async Task<HttpStatusCode> LogoutAsync(HttpClient client, string clientType, string refreshToken, string? password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/core/api/tokens/{clientType}/logout")
        {
            Content = JsonContent.Create(new { password }),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"{GetCookieName(clientType)}={refreshToken}");

        using var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return response.StatusCode;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"{clientType} logout failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public static async Task<HttpStatusCode> TryRefreshAsync(HttpClient client, string clientType, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/core/api/tokens/{clientType}/refresh");
        request.Headers.TryAddWithoutValidation("Cookie", $"{GetCookieName(clientType)}={refreshToken}");

        using var response = await client.SendAsync(request);
        return response.StatusCode;
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

        var refreshToken = ReadCookie(response, GetCookieName(clientType), what);
        return new TokenResult(accessToken, refreshToken, ReadCookieMaxAge(response, GetCookieName(clientType)));
    }

    private static string GetCookieName(string clientType)
    {
        if (string.Equals(clientType, "pwa", StringComparison.OrdinalIgnoreCase))
        {
            return PwaRefreshTokenCookieName;
        }

        var normalized = char.ToUpperInvariant(clientType[0]) + clientType[1..];
        return $"{normalized}RefreshToken";
    }

    public static string GetRefreshTokenCookieName(string clientType) => GetCookieName(clientType);

    private static string ReadCookie(HttpResponseMessage response, string cookieName, string what)
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

        throw new InvalidOperationException($"{what}: response does not set the {cookieName} cookie.");
    }

    private static long? ReadCookieMaxAge(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }

        foreach (var header in headers)
        {
            var segments = header.Split(';');
            var name = segments[0].Split('=')[0].Trim();
            if (!string.Equals(name, cookieName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var attribute in segments.Skip(1))
            {
                var pair = attribute.Split('=', 2);
                if (pair.Length == 2 &&
                    string.Equals(pair[0].Trim(), "max-age", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(pair[1].Trim(), out var maxAge))
                {
                    return maxAge;
                }
            }
        }

        return null;
    }
}
