using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class ChecklistApiClient
{
    private const string ApiPrefix = "/core";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task CreateChecklistTemplateAsync(HttpClient client, Guid templateId)
    {
        await SendAsync(client, HttpMethod.Post, "/api/checklist-templates/", TemplateBody(templateId), null);
    }

    public static async Task CreateChecklistAsync(HttpClient client, string userToken, Guid surgeryTypeId, Guid checklistTemplateId)
    {
        await SendAsync(client, HttpMethod.Post, "/api/checklists/", ChecklistBody(surgeryTypeId, checklistTemplateId), userToken);
    }

    public static async Task UpdateOperatingRoomAsync(HttpClient client, string userToken, Guid checklistId, Guid? operatingRoomId)
    {
        await SendAsync(
            client,
            HttpMethod.Patch,
            $"/api/checklists/{checklistId}/operating-room",
            new { operatingRoomId },
            userToken);
    }

    public static async Task UpdateOperatingDepartmentAsync(HttpClient client, string userToken, Guid checklistId, Guid? operatingDepartmentId)
    {
        await SendAsync(
            client,
            HttpMethod.Patch,
            $"/api/checklists/{checklistId}/operating-department",
            new { operatingDepartmentId },
            userToken);
    }

    public static async Task UpdateNumberMedicalHistoryAsync(HttpClient client, string userToken, Guid checklistId, string? numberMedicalHistory)
    {
        await SendAsync(
            client,
            HttpMethod.Patch,
            $"/api/checklists/{checklistId}/number-medical-history",
            new { numberMedicalHistory },
            userToken);
    }

    public static async Task<ChecklistView> GetChecklistAsync(HttpClient client, string userToken, Guid checklistId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/api/checklists/{checklistId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }

        return JsonSerializer.Deserialize<ChecklistView>(body, JsonOptions)
            ?? throw new InvalidOperationException("Checklist response body is empty.");
    }

    public static async Task<List<MonitoringChecklist>> GetMonitoringChecklistsAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiPrefix + "/api/checklists/monitoring");
        using var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. Body: {errorBody}");
        }

        return await response.Content.ReadFromJsonAsync<List<MonitoringChecklist>>(JsonOptions) ?? [];
    }

    private static object TemplateBody(Guid templateId)
    {
        return new
        {
            id = templateId,
            name = TestData.ChecklistTemplateName,
            description = new
            {
                description = TestData.ChecklistTemplateDescription,
                isSequential = false,
                surgeryTypeId = (Guid?)null,
                isBase = false,
                stages = new object[]
                {
                    new
                    {
                        name = TestData.ChecklistStageName,
                        blocks = new object[]
                        {
                            new
                            {
                                name = TestData.ChecklistBlockName,
                                shortName = TestData.ChecklistBlockShortName,
                                questions = new object[]
                                {
                                    new
                                    {
                                        text = TestData.ChecklistQuestionText,
                                        answerType = new
                                        {
                                            type = "text",
                                            constraints = new { minLength = 0, maxLength = 100 },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static object ChecklistBody(Guid surgeryTypeId, Guid checklistTemplateId)
    {
        return new
        {
            surgeryTypeId,
            checklistTemplateId,
            patientId = (Guid?)null,
            isPatientManual = true,
            patientFullName = $"{TestData.PatientLastName} {TestData.PatientFirstName}",
            plannedSurgeryId = (Guid?)null,
            numberMedicalHistory = (string?)null,
            operatingRoomId = (Guid?)null,
            operatingDepartmentId = (Guid?)null,
        };
    }

    private static async Task SendAsync(HttpClient client, HttpMethod method, string path, object? body, string? bearerToken)
    {
        using var request = new HttpRequestMessage(method, ApiPrefix + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. Body: {errorBody}");
        }
    }

    public static async Task<string> GetFullChecklistsAsync(HttpClient client, string userToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiPrefix + "/api/checklists/list-full");
        request.Content = JsonContent.Create(new { pageSize = 10, pageNumber = 0 });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }

        return body;
    }
}

public sealed record ChecklistView(
    Guid Id,
    string? NumberMedicalHistory,
    ChecklistOrganizationPartView? OperatingRoom,
    ChecklistOrganizationPartView? OperatingDepartment);

public sealed record ChecklistOrganizationPartView(Guid Id);


