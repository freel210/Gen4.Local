namespace Gen4.Local.SmokeTest;

public sealed record SmokeTestConfig(
    string StyxUrl,
    string CertsRoot,
    string HisDbConnection,
    string CoreDbConnection,
    string Lyra3CoreUrl,
    string Lyra3MongoConnection)
{
    public static SmokeTestConfig FromEnvironment()
    {
        return new SmokeTestConfig(
            Environment.GetEnvironmentVariable("STYX_URL") ?? "https://localhost:5050",
            Environment.GetEnvironmentVariable("CERTS_ROOT") ?? @"C:\Users\user2\Documents\Projects\Gen4.Local\Certs",
            Environment.GetEnvironmentVariable("HIS_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=his-db",
            Environment.GetEnvironmentVariable("CORE_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=core-db",
            Environment.GetEnvironmentVariable("LYRA3_CORE_URL") ?? "http://localhost:3302",
            Environment.GetEnvironmentVariable("LYRA3_MONGO_CONNECTION") ?? "mongodb://root:Passw0rd123@localhost:27018");
    }
}

public static class TestData
{
    public const string AdminLogin = "smoketest.admin";
    public const string UserLogin = "smoketest.user";
    public const string Password = "smoketest";

    /// <summary>Matches Gen4.HP.Admins.AdminsDefines.AdminPasswordHashWorkFactor.</summary>
    public const int AdminPasswordHashWorkFactor = 12;

    /// <summary>The only role a Gen4-issued admin token may carry.</summary>
    public static readonly string[] ExpectedAdminRoles = ["Administrator"];

    public static readonly string[] ExpectedUserRoles =
    [
        "EmployeesView",
        "EmployeesCreate",
        "EmployeesDelete",
        "EmployeesEdit",
        "PatientsView",
        "PatientsCreate",
        "PatientsDelete",
        "PatientsEdit",
        "DirectoriesView",
        "SurgeryTypesCreate",
        "SurgeryTypesDelete",
        "SurgeryTypesEdit",
        "SurgeryRolesCreate",
        "SurgeryRolesDelete",
        "SurgeryRolesEdit",
        "SpecialitiesCreate",
        "SpecialitiesDelete",
        "SpecialitiesEdit",
        "PlannedSurgeriesView",
        "PlannedSurgeriesCreate",
        "PlannedSurgeriesEdit",
        "PlannedSurgeriesDelete",
    ];

    public const string ConstitutionMarker = "smoketest";
    public const int ExpectedPasswordHashWorkFactor = 12;

    /// <summary>
    /// Mirrors Gen4.HP.Auth.ILyra3TokenProcessor.PwaRoles - the User role plus the read-only
    /// ClientRole values a PWA access token may carry.
    /// </summary>
    public static readonly string[] ExpectedPwaRoles =
    [
        "User",
        "EmployeesView",
        "PatientsView",
        "DirectoriesView",
        "PlannedSurgeriesView",
    ];
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

    public const string ChecklistTemplateName = "smoketest-template";
    public const string ChecklistTemplateDescription = "Smoke test checklist template";
    public const string ChecklistStageName = "smoketest-stage";
    public const string ChecklistBlockName = "smoketest-block";
    public const string ChecklistBlockShortName = "smkt";
    public const string ChecklistQuestionText = "Smoke test question";
}