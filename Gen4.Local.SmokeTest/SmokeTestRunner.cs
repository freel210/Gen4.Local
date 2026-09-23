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

        logger.Info("Connecting to notifications hub...");
        var tracker = new NotificationTracker();
        await using var hub = await RetryAsync(
            () => ConnectHubAsync(config, userToken, tracker),
            "Hub connection",
            logger);

        RunScenario(styx, tracker, seeded, logger);

        logger.Info("Waiting for notifications...");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!tracker.AllMatched && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

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

    private static void RunScenario(HttpClient his, NotificationTracker tracker, SeededData seeded, ConsoleLogger logger)
    {
        logger.Info("Scenario: SurgeryType create/update/delete...");
        var surgeryTypeId = HisApiClient.CreateSurgeryTypeAsync(his, TestData.SurgeryTypeCode, TestData.SurgeryTypeName).GetAwaiter().GetResult();
        tracker.Expect("SurgeryTypeCreated", surgeryTypeId);
        logger.Info("  created {0}", surgeryTypeId);

        HisApiClient.UpdateSurgeryTypeAsync(his, surgeryTypeId, TestData.SurgeryTypeCode, TestData.SurgeryTypeName).GetAwaiter().GetResult();
        tracker.Expect("SurgeryTypeUpdated", surgeryTypeId);
        logger.Info("  updated {0}", surgeryTypeId);

        logger.Info("Scenario: Employee create/update/delete...");
        var employeeId = HisApiClient.CreateEmployeeAsync(his).GetAwaiter().GetResult();
        tracker.Expect("EmployeeCreated", employeeId);
        logger.Info("  created {0}", employeeId);

        HisApiClient.UpdateEmployeeAsync(his, employeeId).GetAwaiter().GetResult();
        tracker.Expect("EmployeeUpdated", employeeId);
        logger.Info("  updated {0}", employeeId);

        logger.Info("Scenario: OrganizationPart create (root + department) /update/delete...");
        var rootId = HisApiClient.AddOrganizationRootAsync(his, TestData.RootName).GetAwaiter().GetResult();
        tracker.Expect("OrganizationPartCreated", rootId);
        logger.Info("  root created {0}", rootId);

        var partId = HisApiClient.AddOrganizationPartAsync(his, TestData.PartName, "OperatingRoom", rootId).GetAwaiter().GetResult();
        tracker.Expect("OrganizationPartCreated", partId);
        logger.Info("  part created {0}", partId);

        HisApiClient.UpdateOrganizationPartAsync(his, partId, TestData.PartNameUpdated, "OperatingRoom", rootId).GetAwaiter().GetResult();
        tracker.Expect("OrganizationPartUpdated", partId);
        logger.Info("  part updated {0}", partId);

        logger.Info("Scenario: PlannedSurgery create/update/delete...");
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);
        var surgeryId = HisApiClient.AddPlannedSurgeryAsync(his, start, end, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, partId).GetAwaiter().GetResult();
        tracker.Expect("PlannedSurgeryCreated", surgeryId, start, end);
        logger.Info("  created {0}", surgeryId);

        var start2 = start.AddDays(1);
        var end2 = end.AddDays(1);
        HisApiClient.UpdatePlannedSurgeryAsync(his, surgeryId, start2, end2, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, partId).GetAwaiter().GetResult();
        tracker.Expect("PlannedSurgeryUpdated", surgeryId, start2, end2);
        logger.Info("  updated {0}", surgeryId);

        HisApiClient.DeletePlannedSurgeryAsync(his, surgeryId).GetAwaiter().GetResult();
        tracker.Expect("PlannedSurgeryDeleted", surgeryId, start2, end2);
        logger.Info("  deleted {0}", surgeryId);

        logger.Info("Scenario: deletes...");
        HisApiClient.DeleteOrganizationPartAsync(his, partId).GetAwaiter().GetResult();
        tracker.Expect("OrganizationPartDeleted", partId);
        logger.Info("  part deleted {0}", partId);

        HisApiClient.DeleteEmployeeAsync(his, employeeId).GetAwaiter().GetResult();
        tracker.Expect("EmployeeDeleted", employeeId);
        logger.Info("  employee deleted {0}", employeeId);

        HisApiClient.DeleteSurgeryTypeAsync(his, surgeryTypeId).GetAwaiter().GetResult();
        tracker.Expect("SurgeryTypeDeleted", surgeryTypeId);
        logger.Info("  surgery type deleted {0}", surgeryTypeId);
    }

    private static async Task<HubConnection> ConnectHubAsync(
        SmokeTestConfig config,
        string userToken,
        NotificationTracker tracker)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(config.StyxUrl), "/his/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult<string?>(userToken);
                options.HttpMessageHandlerFactory = _ => Tls.CreateHandler(config.CertsRoot);
            })
            .Build();

        RegisterHandlers(connection, tracker);

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