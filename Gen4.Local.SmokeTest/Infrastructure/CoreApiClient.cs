using System.Net.Http.Json;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class CoreApiClient
{
    public static async Task<string> GetTokenAsync(HttpClient client, string path, string login, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { login, password }),
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }
}