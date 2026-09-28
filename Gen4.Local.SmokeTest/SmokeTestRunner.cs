using System.Net;
using System.Net.Http.Json;
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

        await RunPWAScenarioAsync(styx, config, userBundle.AccessToken, logger);

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

    private static async Task RunPWAScenarioAsync(HttpClient styx, SmokeTestConfig config, string userToken, ConsoleLogger logger)
    {
        const int expectedMaxAge = 400 * 24 * 60 * 60;

        var userClaims = JwtInspector.Parse(userToken);
        var leaked = userClaims.Roles
            .Where(role => !string.Equals(role, "User", StringComparison.Ordinal))
            .ToList();
        if (leaked.Count == 0)
        {
            throw new InvalidOperationException("The /user token is expected to carry Lyra3 and ClientRole values, but it carries only 'User'; the PWA assertion would be vacuous.");
        }

        // The /user and /pwa tokens are issued for the same Lyra3 account, so the /user token's sub
        // identifies the PWA rows. Clear leftovers before logging in, so the row assertions are exact.
        var loginSubject = Guid.Parse(userClaims.Subject!);
        var stale = await DbSeeder.DeleteLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject);
        if (stale > 0)
        {
            logger.Warn("  removed {0} stale lyra3_users_long_life_tokens rows for user {1}", stale, loginSubject);
        }

        logger.Info("PWA login: POST /core/api/tokens/pwa");
        TokenResult pwaBundle = null!;
        await RetryAsync(
            async () => pwaBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/pwa", TestData.UserLogin, TestData.Password),
            "PWA login",
            logger);

        var loginClaims = CheckPWAToken(pwaBundle.AccessToken, "login pwa token", logger);
        if (pwaBundle.CookieMaxAgeSeconds != expectedMaxAge)
        {
            throw new InvalidOperationException($"login pwa token: refresh cookie max-age is {pwaBundle.CookieMaxAgeSeconds}, expected {expectedMaxAge} (400 days).");
        }

        logger.Info("  refresh cookie: {0}, max-age={1}", CoreApiClient.GetRefreshTokenCookieName("pwa"), pwaBundle.CookieMaxAgeSeconds);

        var row = await DbSeeder.GetLyra3PwaTokenRowAsync(config.CoreDbConnection, loginSubject)
            ?? throw new InvalidOperationException($"PWA login: no lyra3_users_long_life_tokens row for user {loginSubject}.");
        Expect(row.Login, TestData.UserLogin, "stored login");
        Expect(row.Audience, "lyra3", "stored audience");
        Expect(row.RefreshToken, $"PWA:{pwaBundle.RefreshToken}", "stored refresh token");
        if (string.IsNullOrEmpty(row.PasswordHash))
        {
            throw new InvalidOperationException("PWA login: stored password_hash is empty.");
        }

        logger.Info("  lyra3_users_long_life_tokens row: login={0}, audience={1}, password_hash={2} chars", row.Login, row.Audience, row.PasswordHash!.Length);

        logger.Info("PWA refresh: POST /core/api/tokens/pwa/refresh");
        var rotatedBundle = await CoreApiClient.RefreshAsync(styx, "pwa", pwaBundle.RefreshToken);
        if (string.Equals(rotatedBundle.RefreshToken, pwaBundle.RefreshToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PWA refresh: the refresh token was not rotated.");
        }

        var staleStatus = await CoreApiClient.TryRefreshAsync(styx, "pwa", pwaBundle.RefreshToken);
        if (staleStatus != HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"PWA refresh: the rotated-out refresh token returned {(int)staleStatus}, expected 401.");
        }

        var refreshClaims = CheckPWAToken(rotatedBundle.AccessToken, "refreshed pwa token", logger);
        Expect(refreshClaims.Subject, loginClaims.Subject, "refreshed sub");
        Expect(refreshClaims.UniqueName, loginClaims.UniqueName, "refreshed unique_name");
        Expect(refreshClaims.Audience, loginClaims.Audience, "refreshed aud");
        Expect(rotatedBundle.CookieMaxAgeSeconds, expectedMaxAge, "refreshed cookie max-age");

        var rotatedRow = await DbSeeder.GetLyra3PwaTokenRowAsync(config.CoreDbConnection, loginSubject)
            ?? throw new InvalidOperationException($"PWA refresh: the lyra3_users_long_life_tokens row for user {loginSubject} disappeared.");
        Expect(rotatedRow.RefreshToken, $"PWA:{rotatedBundle.RefreshToken}", "rotated stored refresh token");
        Expect(await DbSeeder.CountLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject), 1, "row count after rotation");
        logger.Info("  refresh token rotated, row count still 1");

        logger.Info("PWA logout with a wrong password: POST /core/api/tokens/pwa/logout");
        var badLogout = await TryPWALogoutAsync(styx, rotatedBundle.RefreshToken, "not-the-password");
        if (badLogout != HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"PWA logout with a wrong password returned {(int)badLogout}, expected 401.");
        }

        logger.Info("PWA logout: POST /core/api/tokens/pwa/logout");
        await CoreApiClient.LogoutAsync(styx, "pwa", rotatedBundle.RefreshToken, TestData.Password);

        var afterLogout = await CoreApiClient.TryRefreshAsync(styx, "pwa", rotatedBundle.RefreshToken);
        if (afterLogout != HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"PWA refresh after logout returned {(int)afterLogout}, expected 401.");
        }

        var remaining = await DbSeeder.CountLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject);
        if (remaining != 0)
        {
            throw new InvalidOperationException($"PWA logout left {remaining} lyra3_users_long_life_tokens rows for user {loginSubject}, expected 0.");
        }

        logger.Info("PWA logout rejected a wrong password, accepted the right one, and removed the stored row.");
    }

    private static async Task<HttpStatusCode> TryPWALogoutAsync(HttpClient styx, string refreshToken, string? password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/core/api/tokens/pwa/logout")
        {
            Content = JsonContent.Create(new { password }),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"{CoreApiClient.GetRefreshTokenCookieName("pwa")}={refreshToken}");

        using var response = await styx.SendAsync(request);
        return response.StatusCode;
    }

    private static void Expect<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"{what} is '{actual}', expected '{expected}'.");
        }
    }

    private static JwtClaims CheckPWAToken(string accessToken, string what, ConsoleLogger logger)
    {
        var claims = JwtInspector.Parse(accessToken);
        ValidateCommon(claims, what);

        if (!string.Equals(claims.Audience, "lyra3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}: aud is '{claims.Audience}', expected 'lyra3'.");
        }

        if (!Guid.TryParse(claims.Subject, out _))
        {
            throw new InvalidOperationException($"{what}: sub claim '{claims.Subject}' is not a Guid.");
        }

        Expect(claims.UniqueName, TestData.UserLogin, $"{what}: unique_name");

        var roles = claims.Roles.Distinct().OrderBy(role => role, StringComparer.Ordinal).ToList();
        if (roles.Count != 1 || !string.Equals(roles[0], "User", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}: expected exactly one role 'User', got [{string.Join(", ", roles)}].");
        }

        logger.Info("{0}: aud=lyra3, sub={1}, unique_name={2}, roles=[User]", what, claims.Subject, claims.UniqueName);
        logger.Info("  access token: {0}", accessToken);
        return claims;
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