using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class ChecklistApiClient
{
    private const string ApiPrefix = "/core";

    public static async Task CreateChecklistTemplateAsync(HttpClient client, Guid templateId)
    {
        await SendAsync(client, HttpMethod.Post, "/api/checklist-templates/", TemplateBody(templateId), null);
    }

    public static async Task CreateChecklistAsync(HttpClient client, string userToken, Guid surgeryTypeId, Guid checklistTemplateId)
    {
        await SendAsync(client, HttpMethod.Post, "/api/checklists/", ChecklistBody(surgeryTypeId, checklistTemplateId), userToken);
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
}
