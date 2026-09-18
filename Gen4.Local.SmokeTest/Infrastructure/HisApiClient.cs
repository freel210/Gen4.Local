using System.Net.Http.Json;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class HisApiClient
{
    private const string ApiPrefix = "/his";
    public static async Task<Guid> CreateSurgeryTypeAsync(HttpClient client, string code, string name)
    {
        return await SendForIdAsync(client, HttpMethod.Post, "/api/directories/surgery-types/", new { code, name });
    }

    public static async Task UpdateSurgeryTypeAsync(HttpClient client, Guid id, string code, string name)
    {
        await SendAsync(client, HttpMethod.Put, $"/api/directories/surgery-types/{id}", new { code, name });
    }

    public static async Task DeleteSurgeryTypeAsync(HttpClient client, Guid id)
    {
        await SendAsync(client, HttpMethod.Delete, $"/api/directories/surgery-types/{id}", null);
    }

    public static async Task<Guid> CreateEmployeeAsync(HttpClient client)
    {
        return await SendForIdAsync(client, HttpMethod.Post, "/api/employees/", CreateEmployeeBody());
    }

    public static async Task UpdateEmployeeAsync(HttpClient client, Guid id)
    {
        await SendAsync(client, HttpMethod.Put, $"/api/employees/{id}", CreateEmployeeBody());
    }

    public static async Task DeleteEmployeeAsync(HttpClient client, Guid id)
    {
        await SendAsync(client, HttpMethod.Delete, $"/api/employees/{id}", null);
    }

    public static async Task<Guid> AddOrganizationRootAsync(HttpClient client, string name)
    {
        return await SendForIdAsync(client, HttpMethod.Post, "/api/organization/root", new { name });
    }

    public static async Task<Guid> AddOrganizationPartAsync(HttpClient client, string name, string type, Guid partOf)
    {
        return await SendForIdAsync(client, HttpMethod.Post, "/api/organization/parts", new { name, type, partOf });
    }

    public static async Task UpdateOrganizationPartAsync(HttpClient client, Guid id, string name, string type, Guid partOf)
    {
        await SendAsync(client, HttpMethod.Put, $"/api/organization/parts/{id}", new { name, type, partOf });
    }

    public static async Task DeleteOrganizationPartAsync(HttpClient client, Guid id)
    {
        await SendAsync(client, HttpMethod.Delete, $"/api/organization/parts/{id}", null);
    }

    public static async Task<Guid> AddPlannedSurgeryAsync(
        HttpClient client,
        DateTime start,
        DateTime end,
        Guid patientId,
        Guid diagnosisId,
        Guid surgeryTypeId,
        Guid? operatingRoomId)
    {
        return await SendForIdAsync(
            client,
            HttpMethod.Post,
            "/api/planned-surgeries/add",
            PlannedSurgeryBody(start, end, patientId, diagnosisId, surgeryTypeId, operatingRoomId));
    }

    public static async Task UpdatePlannedSurgeryAsync(
        HttpClient client,
        Guid id,
        DateTime start,
        DateTime end,
        Guid patientId,
        Guid diagnosisId,
        Guid surgeryTypeId,
        Guid? operatingRoomId)
    {
        await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/planned-surgeries/{id}",
            PlannedSurgeryBody(start, end, patientId, diagnosisId, surgeryTypeId, operatingRoomId));
    }

    public static async Task DeletePlannedSurgeryAsync(HttpClient client, Guid id)
    {
        await SendAsync(client, HttpMethod.Delete, $"/api/planned-surgeries/{id}", null);
    }

    private static object CreateEmployeeBody()
    {
        return new
        {
            firstName = TestData.EmployeeFirstName,
            lastName = TestData.EmployeeLastName,
            patronymic = (string?)null,
            organizationPartId = (Guid?)null,
            specialityIds = (Guid[]?)null,
            comment = TestData.EmployeeComment,
            avatarAction = "None",
            avatar = (string?)null,
        };
    }

    private static object PlannedSurgeryBody(
        DateTime start,
        DateTime end,
        Guid patientId,
        Guid diagnosisId,
        Guid surgeryTypeId,
        Guid? operatingRoomId)
    {
        return new
        {
            startDate = start,
            endDate = end,
            patientId,
            diagnosisId,
            surgeryTypeId,
            operatingRoomId,
            numberMedicalHistory = (string?)null,
            note = TestData.ConstitutionMarker,
            surgeryTeamMembers = (object[]?)null,
        };
    }

    private static async Task<Guid> SendForIdAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        using var doc = await SendAsync(client, method, path, body);
        return GetPropertyIgnoreCase(doc.RootElement, "id").GetGuid();
    }

    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, ApiPrefix + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. Body: {errorBody}");
        }
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(json) ? JsonDocument.Parse("{}") : JsonDocument.Parse(json);
    }

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found in response: {element.GetRawText()}");
    }
}