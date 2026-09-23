using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public sealed record JwtClaims(
    string AccessToken,
    string? Audience,
    string? Subject,
    string? JwtId,
    long? ExpiresAtUnix,
    IReadOnlyList<string> Roles);

public static class JwtInspector
{
    private static readonly string[] RoleClaimKeys =
    [
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        "role",
        "roles",
    ];

    public static JwtClaims Parse(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Access token is not a JWT (got {parts.Length} segments).");
        }

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = payload.RootElement;

        return new JwtClaims(
            accessToken,
            ReadString(root, "aud"),
            ReadString(root, "sub"),
            ReadString(root, "jti"),
            root.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64()
                : null,
            ReadRoles(root));
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadRoles(JsonElement root)
    {
        var roles = new List<string>();
        foreach (var key in RoleClaimKeys)
        {
            if (!root.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single)
            {
                roles.Add(single);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } role)
                    {
                        roles.Add(role);
                    }
                }
            }
        }

        return roles;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            0 => base64,
            _ => throw new InvalidOperationException($"JWT payload has invalid base64url length {base64.Length}."),
        };

        return Convert.FromBase64String(base64);
    }
}
