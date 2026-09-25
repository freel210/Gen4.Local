using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace Gen4.Local.SmokeTest.Infrastructure;

public readonly record struct SeededData(Guid DiagnosisId, Guid PatientId);

public static class DbSeeder
{
    public static async Task CleanupAsync(SmokeTestConfig config, Guid checklistTemplateId, Guid? checklistId, ConsoleLogger logger)
    {
        await CleanupCoreAsync(config.CoreDbConnection, checklistTemplateId, checklistId, logger);
        await CleanupHisAsync(config.HisDbConnection, logger);
    }

    public static async Task<SeededData> SeedAsync(SmokeTestConfig config, ConsoleLogger logger)
    {
        logger.Info("Seeding Lyra3 core database (admin + user)...");
        await SeedLyra3CoreAsync(config.Lyra3MongoConnection);

        logger.Info("Seeding his database (diagnosis + patient, cleaning leftovers)...");
        return await SeedHisAsync(config.HisDbConnection);
    }

    private static async Task SeedLyra3CoreAsync(string connectionString)
    {
        var client = new MongoClient(connectionString);
        var db = client.GetDatabase("Lyra3CoreDb");
        var accounts = db.GetCollection<BsonDocument>("accounts");

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(TestData.Password);

        await UpsertAccountAsync(accounts, new BsonDocument
        {
            { "type", "Administrator" },
            { "login", TestData.AdminLogin },
            { "passwordHash", passwordHash },
            { "isEnabled", true },
        });

        await UpsertAccountAsync(accounts, new BsonDocument
        {
            { "type", "User" },
            { "login", TestData.UserLogin },
            { "passwordHash", passwordHash },
            { "isEnabled", true },
            { "roles", new BsonArray { "User", "ArchiveEditor", "ConferenceParticipant", "IcuUser", "IcuUnitEditor", "WorkModeUser", "WorkModeEditor" } },
        });
    }

    private static string AccountId(string login)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(login));
        return new Guid(hash).ToString();
    }

    private static async Task UpsertAccountAsync(IMongoCollection<BsonDocument> accounts, BsonDocument account)
    {
        var login = account["login"].AsString;
        var filter = Builders<BsonDocument>.Filter.Eq("login", login);
        var update = Builders<BsonDocument>.Update.SetOnInsert("_id", AccountId(login));
        foreach (var element in account.Elements)
        {
            update = update.Set(element.Name, element.Value);
        }

        await accounts.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    private static async Task CleanupCoreAsync(string connectionString, Guid checklistTemplateId, Guid? checklistId, ConsoleLogger logger)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var cleanup = new NpgsqlCommand("""
            DELETE FROM checklists
            WHERE id = @checklistId
               OR template_id IN (SELECT id FROM checklist_templates WHERE name = @templateName);
            DELETE FROM checklist_templates
            WHERE id = @checklistTemplateId
               OR name = @templateName;
            """, connection);
        cleanup.Parameters.AddWithValue("@checklistId", (object?)checklistId ?? DBNull.Value);
        cleanup.Parameters.AddWithValue("@checklistTemplateId", checklistTemplateId);
        cleanup.Parameters.AddWithValue("@templateName", TestData.ChecklistTemplateName);
        await cleanup.ExecuteNonQueryAsync();
        logger.Info("Core smoke-test data cleaned.");
    }

    private static async Task CleanupHisAsync(string connectionString, ConsoleLogger logger)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var cleanup = new NpgsqlCommand("""
            DELETE FROM planned_surgeries WHERE note = @marker;
            DELETE FROM employees WHERE comment = @marker;
            DELETE FROM organization_parts WHERE name LIKE @markerPrefix;
            DELETE FROM surgery_types WHERE code = @surgeryTypeCode;
            DELETE FROM patients WHERE comment = @marker;
            DELETE FROM diagnoses WHERE code = @diagnosisCode;
            """, connection);
        cleanup.Parameters.AddWithValue("@marker", TestData.ConstitutionMarker);
        cleanup.Parameters.AddWithValue("@markerPrefix", "smoketest%");
        cleanup.Parameters.AddWithValue("@surgeryTypeCode", TestData.SurgeryTypeCode);
        cleanup.Parameters.AddWithValue("@diagnosisCode", TestData.DiagnosisCode);
        await cleanup.ExecuteNonQueryAsync();
        logger.Info("HIS smoke-test data cleaned.");
    }

    private static async Task<SeededData> SeedHisAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var cleanup = new NpgsqlCommand("""
            DELETE FROM planned_surgeries WHERE note = @marker;
            DELETE FROM employees WHERE comment = @marker;
            DELETE FROM organization_parts WHERE name LIKE @markerPrefix;
            DELETE FROM surgery_types WHERE code = @surgeryTypeCode;
            DELETE FROM patients WHERE comment = @marker;
            DELETE FROM diagnoses WHERE code = @diagnosisCode;
            """, connection))
        {
            cleanup.Parameters.AddWithValue("@marker", TestData.ConstitutionMarker);
            cleanup.Parameters.AddWithValue("@markerPrefix", "smoketest%");
            cleanup.Parameters.AddWithValue("@surgeryTypeCode", TestData.SurgeryTypeCode);
            cleanup.Parameters.AddWithValue("@diagnosisCode", TestData.DiagnosisCode);
            await cleanup.ExecuteNonQueryAsync();
        }

        Guid diagnosisId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO diagnoses (code, name)
            VALUES (@code, @name)
            RETURNING id;
            """, connection))
        {
            cmd.Parameters.AddWithValue("@code", TestData.DiagnosisCode);
            cmd.Parameters.AddWithValue("@name", TestData.DiagnosisName);
            diagnosisId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        Guid patientId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO patients (first_name, last_name, patronymic, full_name_search_string, comment)
            VALUES (@firstName, @lastName, NULL, @searchString, @comment)
            RETURNING id;
            """, connection))
        {
            cmd.Parameters.AddWithValue("@firstName", TestData.PatientFirstName);
            cmd.Parameters.AddWithValue("@lastName", TestData.PatientLastName);
            cmd.Parameters.AddWithValue("@searchString", $"{TestData.PatientLastName.ToLower()} {TestData.PatientFirstName.ToLower()}");
            cmd.Parameters.AddWithValue("@comment", TestData.ConstitutionMarker);
            patientId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        return new SeededData(diagnosisId, patientId);
    }
}