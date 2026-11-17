namespace Gen4.Local.SmokeTest;

public sealed record SmokeTestConfig(
    string StyxUrl,
    string CertsRoot,
    string HisDbConnection,
    string CoreDbConnection,
    string Lyra3CoreUrl,
    string Lyra3AuthUrl,
    string Lyra3JumboUrl,
    string Lyra3MongoConnection,
    string JeconbolUrl,
    string JeconbolOperatingRoomId,
    string JeconbolOperatingRoomExternalId)
{
    public static SmokeTestConfig FromEnvironment()
    {
        return new SmokeTestConfig(
            Environment.GetEnvironmentVariable("STYX_URL") ?? "https://localhost:5050",
            Environment.GetEnvironmentVariable("CERTS_ROOT") ?? @"C:\Users\user2\Documents\Projects\Gen4.Local\Certs",
            Environment.GetEnvironmentVariable("HIS_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=his-db",
            Environment.GetEnvironmentVariable("CORE_DB_CONNECTION") ?? "Host=localhost;Username=postgres;Password=postgres;Database=core-db",
            Environment.GetEnvironmentVariable("LYRA3_CORE_URL") ?? "http://localhost:3302",
            Environment.GetEnvironmentVariable("LYRA3_AUTH_URL") ?? "http://localhost:3304",
            Environment.GetEnvironmentVariable("LYRA3_JUMBO_URL") ?? "http://localhost:3308",
            Environment.GetEnvironmentVariable("LYRA3_MONGO_CONNECTION") ?? "mongodb://root:Passw0rd123@localhost:27018",
            Environment.GetEnvironmentVariable("JECONBOL_URL") ?? "http://localhost:9097",
            Environment.GetEnvironmentVariable("JECONBOL_OPERATING_ROOM_ID") ?? "11111111-1111-1111-1111-111111111111",
            Environment.GetEnvironmentVariable("JECONBOL_OPERATING_ROOM_EXTERNAL_ID") ?? "00000000-1ada-4f78-a560-ceaa55a10170");
    }
}

public static class TestData
{
    public const string AdminLogin = "smoketest.admin";
    public const string UserLogin = "smoketest.user";
    public const string Password = "smoketest";

    public const string Lyra3AdminLogin = "admin";
    public const string Lyra3AdminPassword = "Passw0rd123";

    public const string JeconbolSecondOperatingRoomExternalId = "00000001-1ada-4f78-a560-ceaa55a10170";
    public const string JeconbolOperatingRoomPartName = "smoketest-jeconbol-or";
    public const string JeconbolOperatingRoomPartNameUpdated = "smoketest-jeconbol-or-updated";
    public const string JeconbolSecondRoomPartName = "smoketest-jeconbol-or-2";
    public const string JeconbolDepartmentPartName = "smoketest-jeconbol-dept";
    public const string JeconbolFinishedSurgeryIdExtensionUrl = "http://resheine-soft.ru/mvs-finishedsurgery-id";
    public const string JeconbolOperatorRoleName = "оперирующий хирург";
    public const string JeconbolExportedState = "exported";
    public const string JeconbolTestAuthorization = "smoketest-auth";
    public const int JeconbolImportDaysAhead = 8;

    /// <summary>Matches Gen4.HP.Admins.AdminsDefines.AdminPasswordHashWorkFactor.</summary>
    public const int AdminPasswordHashWorkFactor = 12;

    /// <summary>The only role a Gen4-issued admin token may carry.</summary>
    public static readonly string[] ExpectedAdminRoles = ["Administrator"];

    public const string Gen4UserRole = "Gen4User";

    public const string Lyra3UserRole = "User";

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
    /// Mirrors Gen4.HP.Auth.ILyra3TokenProcessor.PwaRoles - the Gen4User role plus the read-only
    /// ClientRole values a PWA access token may carry.
    /// </summary>
    public static readonly string[] ExpectedPwaRoles =
    [
        Gen4UserRole,
        "EmployeesView",
        "PatientsView",
        "DirectoriesView",
        "PlannedSurgeriesView",
    ];
    public const string DiagnosisCode = "SMKT";
    public const string DiagnosisName = "Smoke test diagnosis";
    public const string PatientFirstName = "Smoke";
    public const string PatientLastName = "TestPatient";
    public const string FhirPatientLastName = "SmokeFhirPatient";

    public const string SurgeryTypeCode = "SMKT2";
    public const string SurgeryTypeName = "Smoke test surgery type";

    public const string RootName = "smoketest-org";
    public const string DepartmentPartName = "smoketest-department";
    public const string OperatingRoomPartName = "smoketest-or";
    public const string OperatingRoomPartNameUpdated = "smoketest-or-updated";
    public const string NumberMedicalHistory = "SMKT-12345";

    public const string EmployeeComment = "smoketest";
    public const string EmployeeFirstName = "Smoke";
    public const string EmployeeLastName = "TestEmployee";

    public const string ChecklistTemplateName = "smoketest-template";
    public const string ChecklistTemplateDescription = "Smoke test checklist template";
    public const string ChecklistStageName = "smoketest-stage";
    public const string ChecklistBlockName = "smoketest-block";
    public const string ChecklistBlockShortName = "smkt";
    public const string ChecklistQuestionText = "Smoke test question";

    public const string AriadnaUrlSetting = "/MVS/Configuration/Integration/HIS/ariadna/Url";
    public const string AriadnaApiKeySetting = "/MVS/Configuration/Integration/HIS/ariadna/ApiKey";
}
