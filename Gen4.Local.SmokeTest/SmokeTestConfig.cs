namespace Gen4.Local.SmokeTest;

public sealed record SmokeTestConfig(
    string StyxUrl,
    string CertsRoot,
    string HisDbConnection,
    string CoreDbConnection)
{
    public static SmokeTestConfig FromEnvironment()
    {
        return new SmokeTestConfig(
            Environment.GetEnvironmentVariable("STYX_URL") ?? "https://localhost:5050",
            Environment.GetEnvironmentVariable("CERTS_ROOT") ?? @"C:\Users\user2\Documents\Projects\Gen4.Local\Certs",
            Environment.GetEnvironmentVariable("HIS_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=his-db",
            Environment.GetEnvironmentVariable("CORE_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=core-db");
    }
}

public static class TestData
{
    public const string AdminLogin = "smoketest.admin";
    public const string UserLogin = "smoketest.user";
    public const string Password = "smoketest";

    public const string ConstitutionMarker = "smoketest";
    public const string DiagnosisCode = "SMKT";
    public const string DiagnosisName = "Smoke test diagnosis";
    public const string PatientFirstName = "Smoke";
    public const string PatientLastName = "TestPatient";

    public const string SurgeryTypeCode = "SMKT";
    public const string SurgeryTypeName = "Smoke test surgery type";

    public const string RootName = "smoketest-org";
    public const string PartName = "smoketest-dept";
    public const string PartNameUpdated = "smoketest-dept-updated";

    public const string EmployeeComment = "smoketest";
    public const string EmployeeFirstName = "Smoke";
    public const string EmployeeLastName = "TestEmployee";
}