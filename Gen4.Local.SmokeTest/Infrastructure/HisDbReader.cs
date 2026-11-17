using Npgsql;

namespace Gen4.Local.SmokeTest.Infrastructure;

public readonly record struct ImportedPlannedSurgeryTime(
    string ExternalHisId,
    DateTime Start,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public readonly record struct ImportedPlannedSurgeryTeamMember(
    string ExternalHisId,
    string RoleName,
    int MembersCount);

public readonly record struct FinishedSurgeryExportState(
    string ExportState,
    string? ErrorMessage,
    DateTime Start,
    DateTime Finish,
    string? PlannedSurgeryExternalHisId,
    string? PatientExternalHisId,
    string? OperatingRoomExternalHisId,
    string? OperatorExternalHisId);

public static class HisDbReader
{
    public static async Task<List<ImportedPlannedSurgeryTime>> ReadJeconbolPlannedSurgeryTimesAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT info.external_his_id, surgery.start, surgery.created_at, surgery.updated_at
            FROM planned_surgeries surgery
            JOIN planned_surgery_additional_info info ON info.planned_surgery_id = surgery.id
            WHERE info.external_his_type = 'jeconbol3' AND info.external_his_id IS NOT NULL;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        var result = new List<ImportedPlannedSurgeryTime>();

        while (await reader.ReadAsync())
        {
            result.Add(new ImportedPlannedSurgeryTime(
                reader.GetString(0),
                reader.GetDateTime(1),
                reader.GetDateTime(2),
                reader.GetDateTime(3)));
        }

        return result;
    }

    public static async Task<List<ImportedPlannedSurgeryTeamMember>> ReadJeconbolPlannedSurgeryTeamAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT info.external_his_id, role.name, count(*) OVER (PARTITION BY surgery.id)
            FROM planned_surgeries surgery
            JOIN planned_surgery_additional_info info ON info.planned_surgery_id = surgery.id
            JOIN surgery_team_members member ON member.planed_surgery_id = surgery.id
            JOIN surgery_roles role ON role.id = member.surgery_role_id
            WHERE info.external_his_type = 'jeconbol3' AND info.external_his_id IS NOT NULL;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        var result = new List<ImportedPlannedSurgeryTeamMember>();

        while (await reader.ReadAsync())
        {
            result.Add(new ImportedPlannedSurgeryTeamMember(
                reader.GetString(0),
                reader.GetString(1),
                (int)reader.GetInt64(2)));
        }

        return result;
    }

    public static async Task<FinishedSurgeryExportState?> ReadFinishedSurgeryAsync(
        string connectionString,
        Guid finishedSurgeryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT info.jeconbol3_export_state,
                   info.jeconbol3_export_error_message,
                   surgery.start_time,
                   surgery.finish_time,
                   plan_info.external_his_id,
                   patient_info.external_his_id,
                   room_info.external_his_id,
                   (
                       SELECT operator_info.external_his_id
                       FROM surgery_team_members member
                       JOIN surgery_roles role ON role.id = member.surgery_role_id AND role.name = @operator_role
                       JOIN employee_additional_info operator_info ON operator_info.employee_id = member.employee_id
                       WHERE member.planed_surgery_id = surgery.planned_surgery_id
                       LIMIT 1
                   )
            FROM finished_surgeries surgery
            JOIN finished_surgery_additional_info info ON info.finished_surgery_id = surgery.id
            LEFT JOIN planned_surgery_additional_info plan_info ON plan_info.planned_surgery_id = surgery.planned_surgery_id
            LEFT JOIN patient_additional_info patient_info ON patient_info.patient_id = surgery.patient_id
            LEFT JOIN operating_room_additional_info room_info ON room_info.organization_part_id = surgery.operating_room_id
            WHERE surgery.id = @id;
            """,
            connection);

        command.Parameters.AddWithValue("operator_role", TestData.JeconbolOperatorRoleName);
        command.Parameters.AddWithValue("id", finishedSurgeryId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new FinishedSurgeryExportState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetDateTime(2),
            reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }
}
