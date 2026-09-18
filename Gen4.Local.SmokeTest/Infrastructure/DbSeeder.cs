using Npgsql;

namespace Gen4.Local.SmokeTest.Infrastructure;

public readonly record struct SeededData(Guid DiagnosisId, Guid PatientId);

public static class DbSeeder
{
    public static async Task<SeededData> SeedAsync(SmokeTestConfig config, ConsoleLogger logger)
    {
        logger.Info("Seeding core database (admin + user)...");
        await SeedCoreAsync(config.CoreDbConnection);

        logger.Info("Seeding his database (diagnosis + patient, cleaning leftovers)...");
        return await SeedHisAsync(config.HisDbConnection);
    }

    private static async Task SeedCoreAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM users WHERE login IN (@admin, @user);
            DELETE FROM administrators WHERE login = @admin;
            """, connection))
        {
            cmd.Parameters.AddWithValue("@admin", TestData.AdminLogin);
            cmd.Parameters.AddWithValue("@user", TestData.UserLogin);
            await cmd.ExecuteNonQueryAsync();
        }

        var adminHash = BCrypt.Net.BCrypt.HashPassword(TestData.Password, 12);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO administrators (login, password_hash, is_enabled)
            VALUES (@login, @hash, true);
            """, connection))
        {
            cmd.Parameters.AddWithValue("@login", TestData.AdminLogin);
            cmd.Parameters.AddWithValue("@hash", adminHash);
            await cmd.ExecuteNonQueryAsync();
        }

        Guid profileId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO user_profiles (roles)
            VALUES (ARRAY[]::client_role[])
            RETURNING id;
            """, connection))
        {
            profileId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        var userHash = BCrypt.Net.BCrypt.HashPassword(TestData.Password, 12);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO users (login, password_hash, profile_id, is_enabled)
            VALUES (@login, @hash, @profileId, true);
            """, connection))
        {
            cmd.Parameters.AddWithValue("@login", TestData.UserLogin);
            cmd.Parameters.AddWithValue("@hash", userHash);
            cmd.Parameters.AddWithValue("@profileId", profileId);
            await cmd.ExecuteNonQueryAsync();
        }
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