using Gen4.Local.SmokeTest.Infrastructure;
using Gen4.Local.SmokeTest.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gen4.Local.SmokeTest;

public static class SmokeTestRunner
{
    public static async Task<int> RunAsync(SmokeTestConfig config, ConsoleLogger logger)
    {
        logger.Info("STYX: {0}", config.StyxUrl);

        using var tlsHandler = Tls.CreateHandler(config.CertsRoot);
        using var styx = new HttpClient(tlsHandler)
        {
            BaseAddress = new Uri(config.StyxUrl),
        };

        if (!await CheckEnvironmentReadyAsync(styx, logger))
        {
            return 1;
        }

        SeededData seeded = default;
        await RetryAsync(
            async () => seeded = await DbSeeder.SeedAsync(config, logger),
            "Database seeding",
            logger);

        TokenResult adminBundle = null!;
        TokenResult userBundle = null!;
        await RetryAsync(async () =>
        {
            adminBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/admin", TestData.AdminLogin, TestData.Password);
            userBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/user", TestData.UserLogin, TestData.Password);
        }, "Core login", logger);

        CheckUserToken(userBundle.AccessToken, "login user token", logger);
        CheckAdminToken(adminBundle.AccessToken, "login admin token", logger);

        await CheckLyra3CoreAsync(config, userBundle.AccessToken, logger);

        logger.Info("Refresh: POST /core/api/tokens/user/refresh");
        userBundle = await CoreApiClient.RefreshAsync(styx, "user", userBundle.RefreshToken);
        logger.Info("Refresh: POST /core/api/tokens/admin/refresh");
        adminBundle = await CoreApiClient.RefreshAsync(styx, "admin", adminBundle.RefreshToken);

        CheckUserToken(userBundle.AccessToken, "refreshed user token", logger);
        CheckAdminToken(adminBundle.AccessToken, "refreshed admin token", logger);

        await CheckLyra3CoreAsync(config, userBundle.AccessToken, logger);

        var adminToken = adminBundle.AccessToken;
        var userToken = userBundle.AccessToken;

        styx.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);

        logger.Info("Connecting to notifications hubs...");
        var tracker = new NotificationTracker();
        await using var hisHub = await RetryAsync(
            () => ConnectHubAsync(config, "/his/hubs/notifications", userToken, connection => RegisterHandlers(connection, tracker)),
            "His hub connection",
            logger);
        await using var coreHub = await RetryAsync(
            () => ConnectHubAsync(config, "/core/hubs/notifications", userToken, connection => RegisterCoreHandlers(connection, tracker, logger)),
            "Core hub connection",
            logger);

        var checklistTemplateId = Guid.NewGuid();
        Guid? checklistId = null;

        try
        {
            await RunScenarioAsync(styx, tracker, seeded, userToken, checklistTemplateId, logger);

            logger.Info("Waiting for notifications...");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!tracker.AllMatched && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            checklistId = tracker.SnapshotReceived()
                .Where(notification => notification.Method == "CheckListCreated")
                .Select(notification => (Guid?)notification.Id)
                .LastOrDefault();

            logger.Info("Received {0} of {1} expected notifications.", tracker.ReceivedCount, tracker.ExpectedCount);
            foreach (var notification in tracker.SnapshotReceived())
            {
                logger.Info("  {0}  {1}", notification.Method, notification.Id);
            }

            if (!tracker.AllMatched)
            {
                logger.Error("Missing notifications:");
                foreach (var missing in tracker.SnapshotUnmatched())
                {
                    logger.Error("  {0}  {1}  start={2}  end={3}", missing.Method, missing.Id, missing.Start, missing.End);
                }

                logger.Error("Received (full):");
                foreach (var notification in tracker.SnapshotReceived())
                {
                    logger.Error("  {0}  {1}  start={2}  end={3}", notification.Method, notification.Id, notification.Start, notification.End);
                }

                return 1;
            }

            return 0;
        }
        finally
        {
            await DbSeeder.CleanupAsync(config, checklistTemplateId, checklistId, logger);
        }
    }

    private static async Task CheckLyra3CoreAsync(SmokeTestConfig config, string userToken, ConsoleLogger logger)
    {
        logger.Info("LYRA3_CORE: {0}", config.Lyra3CoreUrl);

        using var lyra3Core = new HttpClient
        {
            BaseAddress = new Uri(config.Lyra3CoreUrl),
        };
        lyra3Core.DefaultRequestHeaders.Authorization = new("Bearer", userToken);

        await RetryAsync(async () =>
        {
            var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
            using var response = await lyra3Core.GetAsync($"/api/finishedSurgeries?Limit=1&PageNumber=1&From={from}&To={to}");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Lyra3 core responded with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            logger.Info("Lyra3 core FinishedSurgeries endpoint OK.");
        }, "Lyra3 core FinishedSurgeries check", logger);
    }

    private static void CheckUserToken(string accessToken, string what, ConsoleLogger logger)
    {
        var claims = JwtInspector.Parse(accessToken);
        ValidateCommon(claims, what);

        var missing = TestData.ExpectedUserRoles
            .Where(role => !claims.Roles.Contains(role, StringComparer.Ordinal))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{what}: missing ClientRole values: {string.Join(", ", missing)}.");
        }

        logger.Info("{0}: aud=lyra3, sub={1}, all {2} ClientRole values present.", what, claims.Subject, TestData.ExpectedUserRoles.Length);
        logger.Info("  roles: {0}", string.Join(", ", claims.Roles.Distinct().OrderBy(role => role, StringComparer.Ordinal)));
        logger.Info("  access token: {0}", accessToken);
    }

    private static void CheckAdminToken(string accessToken, string what, ConsoleLogger logger)
    {
        var claims = JwtInspector.Parse(accessToken);
        ValidateCommon(claims, what);

        var leaked = claims.Roles
            .Where(role => TestData.ExpectedUserRoles.Contains(role, StringComparer.Ordinal))
            .ToList();
        if (leaked.Count > 0)
        {
            throw new InvalidOperationException($"{what}: admin token must not carry ClientRole values, found: {string.Join(", ", leaked)}.");
        }

        logger.Info("{0}: aud=lyra3, sub={1}, no ClientRole values.", what, claims.Subject);
    }

    private static void ValidateCommon(JwtClaims claims, string what)
    {
        if (!string.Equals(claims.Audience, "lyra3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}: aud is '{claims.Audience}', expected 'lyra3'.");
        }

        if (string.IsNullOrEmpty(claims.Subject))
        {
            throw new InvalidOperationException($"{what}: sub claim is missing.");
        }

        if (claims.ExpiresAtUnix is not { } expiresAt || DateTimeOffset.FromUnixTimeSeconds(expiresAt) <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException($"{what}: exp claim is missing or already expired.");
        }
    }

    private static async Task<bool> CheckEnvironmentReadyAsync(HttpClient styx, ConsoleLogger logger)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var coreResponse = await styx.GetAsync("/core/ok");
                using var hisResponse = await styx.GetAsync("/his/ok");
                if (coreResponse.IsSuccessStatusCode && hisResponse.IsSuccessStatusCode)
                {
                    logger.Info("Environment is ready (Core OK, His OK) on attempt {0}.", attempt);
                    return true;
                }

                logger.Warn("Attempt {0}: Core={1}, His={2}.", attempt, coreResponse.StatusCode, hisResponse.StatusCode);
            }
            catch (Exception ex)
            {
                logger.Warn("Attempt {0} failed: {1}", attempt, ex.Message);
            }

            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        logger.Error("Environment is not ready after {0} attempts.", attempts);
        return false;
    }

    private static async Task RunScenarioAsync(HttpClient styx, NotificationTracker tracker, SeededData seeded, string userToken, Guid checklistTemplateId, ConsoleLogger logger)
    {
        logger.Info("Scenario: SurgeryType create/update/delete...");
        var surgeryTypeId = await HisApiClient.CreateSurgeryTypeAsync(styx, TestData.SurgeryTypeCode, TestData.SurgeryTypeName);
        tracker.Expect("SurgeryTypeCreated", surgeryTypeId);
        logger.Info("  created {0}", surgeryTypeId);

        await HisApiClient.UpdateSurgeryTypeAsync(styx, surgeryTypeId, TestData.SurgeryTypeCode, TestData.SurgeryTypeName);
        tracker.Expect("SurgeryTypeUpdated", surgeryTypeId);
        logger.Info("  updated {0}", surgeryTypeId);

        logger.Info("Scenario: Employee create/update/delete...");
        var employeeId = await HisApiClient.CreateEmployeeAsync(styx);
        tracker.Expect("EmployeeCreated", employeeId);
        logger.Info("  created {0}", employeeId);

        await HisApiClient.UpdateEmployeeAsync(styx, employeeId);
        tracker.Expect("EmployeeUpdated", employeeId);
        logger.Info("  updated {0}", employeeId);

        logger.Info("Scenario: OrganizationPart create (root + department) /update/delete...");
        var rootId = await HisApiClient.AddOrganizationRootAsync(styx, TestData.RootName);
        tracker.Expect("OrganizationPartCreated", rootId);
        logger.Info("  root created {0}", rootId);

        var partId = await HisApiClient.AddOrganizationPartAsync(styx, TestData.PartName, "OperatingRoom", rootId);
        tracker.Expect("OrganizationPartCreated", partId);
        logger.Info("  part created {0}", partId);

        await HisApiClient.UpdateOrganizationPartAsync(styx, partId, TestData.PartNameUpdated, "OperatingRoom", rootId);
        tracker.Expect("OrganizationPartUpdated", partId);
        logger.Info("  part updated {0}", partId);

        logger.Info("Scenario: PlannedSurgery create/update/delete...");
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);
        var surgeryId = await HisApiClient.AddPlannedSurgeryAsync(styx, start, end, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, partId);
        tracker.Expect("PlannedSurgeryCreated", surgeryId, start, end);
        logger.Info("  created {0}", surgeryId);

        var start2 = start.AddDays(1);
        var end2 = end.AddDays(1);
        await HisApiClient.UpdatePlannedSurgeryAsync(styx, surgeryId, start2, end2, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, partId);
        tracker.Expect("PlannedSurgeryUpdated", surgeryId, start2, end2);
        logger.Info("  updated {0}", surgeryId);

        await HisApiClient.DeletePlannedSurgeryAsync(styx, surgeryId);
        tracker.Expect("PlannedSurgeryDeleted", surgeryId, start2, end2);
        logger.Info("  deleted {0}", surgeryId);

        logger.Info("Scenario: ChecklistTemplate + Checklist create (core)...");
        await ChecklistApiClient.CreateChecklistTemplateAsync(styx, checklistTemplateId);
        logger.Info("  template created {0}", checklistTemplateId);

        tracker.Expect("CheckListCreated");
        await ChecklistApiClient.CreateChecklistAsync(styx, userToken, surgeryTypeId, checklistTemplateId);
        logger.Info("  checklist posted; id will arrive with the CheckListCreated event");

        logger.Info("Scenario: deletes...");
        await HisApiClient.DeleteOrganizationPartAsync(styx, partId);
        tracker.Expect("OrganizationPartDeleted", partId);
        logger.Info("  part deleted {0}", partId);

        await HisApiClient.DeleteEmployeeAsync(styx, employeeId);
        tracker.Expect("EmployeeDeleted", employeeId);
        logger.Info("  employee deleted {0}", employeeId);

        await HisApiClient.DeleteSurgeryTypeAsync(styx, surgeryTypeId);
        tracker.Expect("SurgeryTypeDeleted", surgeryTypeId);
        logger.Info("  surgery type deleted {0}", surgeryTypeId);
    }

    private static async Task<HubConnection> ConnectHubAsync(
        SmokeTestConfig config,
        string path,
        string userToken,
        Action<HubConnection> registerHandlers)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(config.StyxUrl), path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult<string?>(userToken);
                options.HttpMessageHandlerFactory = _ => Tls.CreateHandler(config.CertsRoot);
            })
            .Build();

        registerHandlers(connection);

        try
        {
            await connection.StartAsync();
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private static void RegisterHandlers(HubConnection connection, NotificationTracker tracker)
    {
        connection.On<Guid>("SurgeryTypeCreated", id => tracker.Record("SurgeryTypeCreated", id));
        connection.On<Guid>("SurgeryTypeUpdated", id => tracker.Record("SurgeryTypeUpdated", id));
        connection.On<Guid>("SurgeryTypeDeleted", id => tracker.Record("SurgeryTypeDeleted", id));

        connection.On<Guid>("EmployeeCreated", id => tracker.Record("EmployeeCreated", id));
        connection.On<Guid>("EmployeeUpdated", id => tracker.Record("EmployeeUpdated", id));
        connection.On<Guid>("EmployeeDeleted", id => tracker.Record("EmployeeDeleted", id));

        connection.On<Guid>("OrganizationPartCreated", id => tracker.Record("OrganizationPartCreated", id));
        connection.On<Guid>("OrganizationPartUpdated", id => tracker.Record("OrganizationPartUpdated", id));
        connection.On<Guid>("OrganizationPartDeleted", id => tracker.Record("OrganizationPartDeleted", id));

        connection.On<Guid, DateTime, DateTime>("PlannedSurgeryCreated", (id, start, end) => tracker.Record("PlannedSurgeryCreated", id, start, end));
        connection.On<Guid, DateTime, DateTime>("PlannedSurgeryUpdated", (id, start, end) => tracker.Record("PlannedSurgeryUpdated", id, start, end));
        connection.On<Guid, DateTime, DateTime>("PlannedSurgeryDeleted", (id, start, end) => tracker.Record("PlannedSurgeryDeleted", id, start, end));
    }

    private static void RegisterCoreHandlers(HubConnection connection, NotificationTracker tracker, ConsoleLogger logger)
    {
        connection.On<Guid>("CheckListCreated", id =>
        {
            logger.Info(">>> CheckListCreated event received from core hub: checklist id={0}", id);
            tracker.Record("CheckListCreated", id);
        });
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, string what, ConsoleLogger logger, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    logger.Error("'{0}' did not succeed within the timeout: {1}", what, ex.Message);
                    throw;
                }

                logger.Warn("'{0}' not ready yet ({1}); retrying...", what, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task RetryAsync(Func<Task> action, string what, ConsoleLogger logger, TimeSpan? timeout = null)
    {
        await RetryAsync(
            async () =>
            {
                await action();
                return true;
            },
            what,
            logger,
            timeout);
    }
}