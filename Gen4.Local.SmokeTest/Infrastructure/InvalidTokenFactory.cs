using System.Text;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class InvalidTokenFactory
{
    public static string Create()
    {
        var issued = DateTimeOffset.UtcNow.AddHours(-2);

        var header = Base64UrlEncode("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            sub = "smoketest.invalid",
            unique_name = "smoketest.invalid",
            aud = "lyra3",
            iat = issued.ToUnixTimeSeconds(),
            exp = issued.AddHours(1).ToUnixTimeSeconds(),
            role = new[] { "User" },
        }));
        var signature = Base64UrlEncode(new string('A', 256));

        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(string value)
        => Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}