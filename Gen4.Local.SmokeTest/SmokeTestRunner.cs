using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

        await RunPWABlockedUserScenarioAsync(styx, config, userBundle.AccessToken, logger);

        await RunUserWithoutLyra3UserScenarioAsync(styx, config, logger);

        logger.Info("Refresh: POST /core/api/tokens/user/refresh");
        userBundle = await CoreApiClient.RefreshAsync(styx, "user", userBundle.RefreshToken);
        logger.Info("Refresh: POST /core/api/tokens/admin/refresh");
        adminBundle = await CoreApiClient.RefreshAsync(styx, "admin", adminBundle.RefreshToken);

        CheckUserToken(userBundle.AccessToken, "refreshed user token", logger);
        CheckAdminToken(adminBundle.AccessToken, "refreshed admin token", logger);

        await CheckLyra3CoreAsync(config, userBundle.AccessToken, logger);

        await CheckLyra3AbsoluteUrlsAsync(styx, adminBundle.AccessToken, logger);

        await CheckUnauthorizedAsync(styx, logger);

        await CheckCertOnlyAsync(styx, logger);

        await CheckPdfConvertAsync(styx, userBundle.AccessToken, logger);

        var adminToken = adminBundle.AccessToken;
        var userToken = userBundle.AccessToken;

        styx.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);

        // /core/hubs/checklists and /core/api/checklists/monitoring are certificate-only endpoints,
        // so they must be reached the way a real component reaches them: without a token.
        var component = new HttpClient(tlsHandler)
        {
            BaseAddress = new Uri(config.StyxUrl),
        };

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

        var checklistsChanged = 0;
        await using var checklistsHub = await RetryAsync(
            () => ConnectHubAsync(config, "/core/hubs/checklists", null, connection =>
                connection.On("ChecklistsChanged", new Func<Task>(() =>
                {
                    Interlocked.Increment(ref checklistsChanged);
                    return Task.CompletedTask;
                }))),
            "Checklists hub connection",
            logger);

        var checklistTemplateId = Guid.NewGuid();
        Guid? checklistId = null;

        try
        {
            await RetryAsync(
                () =>
                {
                    if (Volatile.Read(ref checklistsChanged) < 1)
                    {
                        throw new InvalidOperationException("no readiness signal received");
                    }

                    return Task.CompletedTask;
                },
                "Checklists hub readiness signal",
                logger,
                TimeSpan.FromSeconds(30));

            var readinessSignals = Volatile.Read(ref checklistsChanged);
            logger.Info("  Checklists hub readiness signals: {0}", readinessSignals);

            await RunScenarioAsync(styx, config, tracker, seeded, userToken, checklistTemplateId, logger);

        await RunJeconbolScenarioAsync(styx, config, logger);

            logger.Info("Checklists list-full: POST /core/api/checklists/list-full");
            var listFullBody = await ChecklistApiClient.GetFullChecklistsAsync(styx, userToken);
            logger.Info("  response: {0}", listFullBody);

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

            await RetryAsync(
                async () =>
                {
                    if (Volatile.Read(ref checklistsChanged) <= readinessSignals)
                    {
                        throw new InvalidOperationException("no change signal received after checklist create");
                    }

                    var monitoring = await ChecklistApiClient.GetMonitoringChecklistsAsync(component);

                    if (checklistId is Guid created && !monitoring.Any(x => x.Id == created))
                    {
                        throw new InvalidOperationException($"checklist {created} is absent from {monitoring.Count} monitoring entries");
                    }

                    logger.Info("ChecklistsChanged signals after connect: {0}", Volatile.Read(ref checklistsChanged) - readinessSignals);
                    logger.Info("GET /core/api/checklists/monitoring returned {0} entries: {1}", monitoring.Count, string.Join(", ", monitoring.Select(x => $"{x.Id} [{x.Status}]")));
                },
                "ChecklistsChanged signal and monitoring list",
                logger,
                TimeSpan.FromSeconds(30));

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
        var beyondPwa = userClaims.Roles
            .Where(role => !TestData.ExpectedPwaRoles.Contains(role, StringComparer.Ordinal))
            .Distinct()
            .ToList();
        if (beyondPwa.Count == 0)
        {
            throw new InvalidOperationException("The /user token is expected to carry roles beyond the PWA set; the PWA assertion would be vacuous.");
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

        var storedCost = ReadBcryptCost(row.PasswordHash);
        if (storedCost != TestData.ExpectedPasswordHashWorkFactor)
        {
            throw new InvalidOperationException($"PWA login: stored password_hash uses BCrypt cost {storedCost}, expected {TestData.ExpectedPasswordHashWorkFactor}.");
        }

        logger.Info("  lyra3_users_long_life_tokens row: login={0}, audience={1}, password_hash={2} chars, bcrypt cost={3}", row.Login, row.Audience, row.PasswordHash!.Length, storedCost);

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

    private static async Task RunPWABlockedUserScenarioAsync(HttpClient styx, SmokeTestConfig config, string userToken, ConsoleLogger logger)
    {
        logger.Info("PWA blocked user: a Lyra3 account that stopped being active must lose its long life token");

        var loginSubject = Guid.Parse(JwtInspector.Parse(userToken).Subject!);
        await DbSeeder.DeleteLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject);

        TokenResult pwaBundle = null!;
        await RetryAsync(
            async () => pwaBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/pwa", TestData.UserLogin, TestData.Password),
            "PWA login before blocking",
            logger);

        logger.Info("  disabling the Lyra3 account '{0}'", TestData.UserLogin);
        await DbSeeder.SetLyra3AccountEnabledAsync(config.Lyra3MongoConnection, TestData.UserLogin, false);
        try
        {
            var status = await CoreApiClient.TryRefreshAsync(styx, "pwa", pwaBundle.RefreshToken);
            if (status != HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException($"PWA refresh with a disabled Lyra3 account returned {(int)status}, expected 401.");
            }

            Expect(await DbSeeder.CountLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject), 0, "row count after a refused refresh");
            logger.Info("  refresh -> 401 and the lyra3_users_long_life_tokens row was dropped");
        }
        finally
        {
            await DbSeeder.SetLyra3AccountEnabledAsync(config.Lyra3MongoConnection, TestData.UserLogin, true);
            logger.Info("  the Lyra3 account '{0}' is enabled again", TestData.UserLogin);
        }

        TokenResult restoredBundle = null!;
        await RetryAsync(
            async () => restoredBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/pwa", TestData.UserLogin, TestData.Password),
            "PWA login after enabling",
            logger);
        CheckPWAToken(restoredBundle.AccessToken, "pwa token after enabling", logger);

        await CoreApiClient.LogoutAsync(styx, "pwa", restoredBundle.RefreshToken, TestData.Password);
        Expect(await DbSeeder.CountLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject), 0, "row count after the final logout");

        logger.Info("PWA blocked user: the refresh was refused, the stored token was dropped, and a fresh login worked afterwards.");
    }

    private static async Task RunUserWithoutLyra3UserScenarioAsync(HttpClient styx, SmokeTestConfig config, ConsoleLogger logger)
    {
        logger.Info(
            "Lyra3 account without the {0} role: Gen4 grants {1} and must not expose {0} on Gen4-issued tokens",
            TestData.Lyra3UserRole,
            TestData.Gen4UserRole);

        Guid? loginSubject = null;
        await DbSeeder.SetLyra3AccountRoleAsync(config.Lyra3MongoConnection, TestData.UserLogin, TestData.Lyra3UserRole, false);
        try
        {
            TokenResult userBundle = null!;
            await RetryAsync(
                async () => userBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/user", TestData.UserLogin, TestData.Password),
                "User login without the Lyra3 User role",
                logger);

            var userClaims = JwtInspector.Parse(userBundle.AccessToken);
            if (userClaims.Roles.Contains(TestData.Lyra3UserRole, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"/user token carries the Lyra3 {TestData.Lyra3UserRole} role the account does not hold: [{string.Join(", ", userClaims.Roles)}].");
            }

            if (!userClaims.Roles.Contains(TestData.Gen4UserRole, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"/user token is missing the {TestData.Gen4UserRole} role: [{string.Join(", ", userClaims.Roles)}].");
            }

            logger.Info("  /user token: {0} present, {1} absent", TestData.Gen4UserRole, TestData.Lyra3UserRole);

            loginSubject = Guid.Parse(userClaims.Subject!);
            await DbSeeder.DeleteLyra3PwaTokenRowsAsync(config.CoreDbConnection, loginSubject.Value);

            TokenResult pwaBundle = null!;
            await RetryAsync(
                async () => pwaBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/pwa", TestData.UserLogin, TestData.Password),
                "PWA login without the Lyra3 User role",
                logger);

            var pwaClaims = CheckPWAToken(pwaBundle.AccessToken, "pwa token without the Lyra3 User role", logger);

            var pwaLyra3Status = await GetLyra3StatusAsync(config, "/api/timezoneOffset", pwaClaims.AccessToken);
            if (pwaLyra3Status != HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"a PWA token reached the Lyra3 {TestData.Lyra3UserRole}-only endpoint with {(int)pwaLyra3Status}, expected 403.");
            }

            logger.Info("  PWA token -> Lyra3 {0}-only endpoint: 403", TestData.Lyra3UserRole);

            var userLyra3Status = await GetLyra3StatusAsync(config, "/api/timezoneOffset", userClaims.AccessToken);
            if (userLyra3Status != HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"a /user token reached the Lyra3 {TestData.Lyra3UserRole}-only endpoint with {(int)userLyra3Status}, expected 403.");
            }

            logger.Info("  /user token -> Lyra3 {0}-only endpoint: 403", TestData.Lyra3UserRole);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/core/api/config/lyra3");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {userClaims.AccessToken}");
            using var response = await styx.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"/core/api/config/lyra3 returned {(int)response.StatusCode} for a /user token, expected 200.");
            }

            logger.Info("  /user token -> Gen4 {0}-only endpoint: 200", TestData.Gen4UserRole);

            await CoreApiClient.LogoutAsync(styx, "pwa", pwaBundle.RefreshToken, TestData.Password);
            logger.Info("  the account without {0} keeps Gen4 access and has no Lyra3 {0} access", TestData.Lyra3UserRole);
        }
        finally
        {
            await DbSeeder.SetLyra3AccountRoleAsync(config.Lyra3MongoConnection, TestData.UserLogin, TestData.Lyra3UserRole, true);
            if (loginSubject is Guid subject)
            {
                await DbSeeder.DeleteLyra3PwaTokenRowsAsync(config.CoreDbConnection, subject);
            }

            logger.Info("  the Lyra3 {0} role is restored for '{1}'", TestData.Lyra3UserRole, TestData.UserLogin);
        }
    }

    private static async Task<HttpStatusCode> GetLyra3StatusAsync(SmokeTestConfig config, string path, string accessToken)
    {
        using var lyra3 = new HttpClient
        {
            BaseAddress = new Uri(config.Lyra3CoreUrl),
        };
        lyra3.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        using var response = await lyra3.GetAsync(path);
        return response.StatusCode;
    }

    private static async Task CheckLyra3AbsoluteUrlsAsync(HttpClient styx, string adminAccessToken, ConsoleLogger logger)
    {
        logger.Info("Lyra3 absolute URLs: GET /core/api/config/lyra3");

        TokenResult pwaBundle = null!;
        await RetryAsync(
            async () => pwaBundle = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/pwa", TestData.UserLogin, TestData.Password),
            "PWA login for absolute URLs",
            logger);

        string[] expectedProperties =
        [
            "authApiAbsoluteUrl",
            "jumboApiAbsoluteUrl",
            "coreApiAbsoluteUrl",
            "conferenceApiAbsoluteUrl",
            "icuApiAbsoluteUrl",
            "notificationApiAbsoluteUrl",
            "storageAbsoluteUrl",
            "externalAbsoluteUrl",
        ];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/core/api/config/lyra3");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pwaBundle.AccessToken}");

        string body;
        string? responseContentType;
        using (var response = await styx.SendAsync(request))
        {
            body = await response.Content.ReadAsStringAsync();
            responseContentType = response.Content.Headers.ContentType?.ToString();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"lyra3 absolute urls returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }
        }

        using var doc = JsonDocument.Parse(body);
        foreach (var name in expectedProperties)
        {
            var value = doc.RootElement.TryGetProperty(name, out var element) ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"lyra3 absolute urls: '{name}' is '{value}', expected a non-empty url.");
            }
        }

        Expect(
            doc.RootElement.TryGetProperty("authApiAbsoluteUrl", out var authApiElement) ? authApiElement.GetString() : null,
            "http://localhost:3304",
            "lyra3 auth api absolute url");

        logger.Info(
            "  pwa token accepted, all {0} urls present: {1}",
            expectedProperties.Length,
            string.Join(", ", expectedProperties.Select(name => $"{name}={doc.RootElement.GetProperty(name).GetString()}")));

        logger.Info("  response body: {0}", body);
        logger.Info("  response content-type: {0}", responseContentType);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/core/api/config/lyra3");
        adminRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {adminAccessToken}");

        using var adminResponse = await styx.SendAsync(adminRequest);
        if (adminResponse.StatusCode != HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"lyra3 absolute urls with an admin token returned {(int)adminResponse.StatusCode}, expected 403.");
        }

        logger.Info("  admin token rejected with 403");

        await CoreApiClient.LogoutAsync(styx, "pwa", pwaBundle.RefreshToken, TestData.Password);
    }

    private static async Task CheckCertOnlyAsync(HttpClient styx, ConsoleLogger logger)
    {
        logger.Info("Authorization: cert ok, token missing -> inspect status");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/core/api/checklists/{Guid.NewGuid()}");
        using var response = await styx.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        logger.Info("  cert-only request -> {0}, body: {1}", (int)response.StatusCode, body);
    }

    private static async Task CheckPdfConvertAsync(HttpClient styx, string userToken, ConsoleLogger logger)
    {
        logger.Info("Pdf: POST /core/api/pdf/convert must keep answering 200 for a valid token");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/core/api/pdf/convert")
        {
            Content = JsonContent.Create(new { html = "<html><body>smoketest</body></html>", fileName = "smoketest.pdf" }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {userToken}");

        using var response = await styx.SendAsync(request);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"pdf convert with a valid token returned {(int)response.StatusCode}, expected 200. Body: {errorBody}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var length = response.Content.Headers.ContentLength ?? (await response.Content.ReadAsByteArrayAsync()).LongLength;

        if (contentType != "application/pdf" || length == 0)
        {
            throw new InvalidOperationException(
                $"pdf convert returned content-type '{contentType}' and {length} bytes, expected application/pdf and a non-empty body");
        }

        logger.Info("  valid token -> 200, content-type {0}, {1} bytes", contentType, length);
    }

    private static async Task CheckUnauthorizedAsync(HttpClient styx, ConsoleLogger logger)
    {
        logger.Info("Authorization: an unusable token must answer 401, not 403");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/core/api/checklists/{Guid.NewGuid()}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {InvalidTokenFactory.Create()}");

        using var response = await styx.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"an unusable token returned {(int)response.StatusCode}, expected 401. Body: {body}");
        }

        logger.Info("  unusable token -> 401, body: {0}", body);
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

    private static int ReadBcryptCost(string passwordHash)
    {
        // Modular crypt format: $<id>$<version>$<cost>$<salt><hash>
        var parts = passwordHash.Split('$');
        if (parts.Length < 4 || !int.TryParse(parts[2], out var cost))
        {
            throw new InvalidOperationException($"'{passwordHash}' is not a BCrypt hash with an explicit cost.");
        }

        return cost;
    }

    private static JwtClaims CheckPWAToken(string accessToken, string what, ConsoleLogger logger)
    {
        var claims = JwtInspector.Parse(accessToken);
        ValidateCommon(claims, what, "lyra3");

        if (!Guid.TryParse(claims.Subject, out _))
        {
            throw new InvalidOperationException($"{what}: sub claim '{claims.Subject}' is not a Guid.");
        }

        Expect(claims.UniqueName, TestData.UserLogin, $"{what}: unique_name");

        var roles = claims.Roles.Distinct().OrderBy(role => role, StringComparer.Ordinal).ToList();
        var expectedRoles = TestData.ExpectedPwaRoles.OrderBy(role => role, StringComparer.Ordinal).ToList();
        if (!roles.SequenceEqual(expectedRoles, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{what}: expected exactly [{string.Join(", ", expectedRoles)}], got [{string.Join(", ", roles)}].");
        }

        var writeRoles = TestData.ExpectedUserRoles
            .Where(role => !TestData.ExpectedPwaRoles.Contains(role, StringComparer.Ordinal))
            .Where(role => roles.Contains(role, StringComparer.Ordinal))
            .ToList();
        if (writeRoles.Count > 0)
        {
            throw new InvalidOperationException(
                $"{what}: a PWA token must not carry Create/Edit/Delete ClientRole values, found [{string.Join(", ", writeRoles)}].");
        }

        logger.Info("{0}: aud=lyra3, sub={1}, unique_name={2}", what, claims.Subject, claims.UniqueName);
        logger.Info("  roles: {0}", string.Join(", ", roles));
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
        ValidateCommon(claims, what, "lyra3");

        var missing = TestData.ExpectedUserRoles
            .Where(role => !claims.Roles.Contains(role, StringComparer.Ordinal))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{what}: missing ClientRole values: {string.Join(", ", missing)}.");
        }

        foreach (var required in new[] { TestData.Gen4UserRole, TestData.Lyra3UserRole })
        {
            if (!claims.Roles.Contains(required, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"{what}: missing the {required} role.");
            }
        }

        logger.Info(
            "{0}: aud=lyra3, sub={1}, all {2} ClientRole values present, {3} and {4} present.",
            what,
            claims.Subject,
            TestData.ExpectedUserRoles.Length,
            TestData.Gen4UserRole,
            TestData.Lyra3UserRole);
        logger.Info("  roles: {0}", string.Join(", ", claims.Roles.Distinct().OrderBy(role => role, StringComparer.Ordinal)));
        logger.Info("  access token: {0}", accessToken);
    }

    private static void CheckAdminToken(string accessToken, string what, ConsoleLogger logger)
    {
        var claims = JwtInspector.Parse(accessToken);
        ValidateCommon(claims, what, "gen4audience");

        var roles = claims.Roles.Distinct().OrderBy(role => role, StringComparer.Ordinal).ToList();
        var expectedRoles = TestData.ExpectedAdminRoles.OrderBy(role => role, StringComparer.Ordinal).ToList();
        if (!roles.SequenceEqual(expectedRoles, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{what}: expected exactly [{string.Join(", ", expectedRoles)}], got [{string.Join(", ", roles)}].");
        }

        logger.Info("{0}: aud=gen4audience (issued by Gen4, not Lyra3), sub={1}, roles=[{2}]", what, claims.Subject, string.Join(", ", roles));
    }

    private static void ValidateCommon(JwtClaims claims, string what, string expectedAudience)
    {
        if (!string.Equals(claims.Audience, expectedAudience, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}: aud is '{claims.Audience}', expected '{expectedAudience}'.");
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

    private static async Task RunScenarioAsync(HttpClient styx, SmokeTestConfig config, NotificationTracker tracker, SeededData seeded, string userToken, Guid checklistTemplateId, ConsoleLogger logger)
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

        logger.Info("Scenario: OrganizationPart create (root + department + operating room) /update/delete...");
        var rootId = await HisApiClient.AddOrganizationRootAsync(styx, TestData.RootName);
        tracker.Expect("OrganizationPartCreated", rootId);
        logger.Info("  root created {0}", rootId);

        var departmentId = await HisApiClient.AddOrganizationPartAsync(styx, TestData.DepartmentPartName, "Department", rootId);
        tracker.Expect("OrganizationPartCreated", departmentId);
        logger.Info("  department created {0}", departmentId);

        var operatingRoomId = await HisApiClient.AddOrganizationPartAsync(styx, TestData.OperatingRoomPartName, "OperatingRoom", departmentId);
        tracker.Expect("OrganizationPartCreated", operatingRoomId);
        logger.Info("  operating room created {0}", operatingRoomId);

        await HisApiClient.UpdateOrganizationPartAsync(styx, operatingRoomId, TestData.OperatingRoomPartNameUpdated, "OperatingRoom", departmentId);
        tracker.Expect("OrganizationPartUpdated", operatingRoomId);
        logger.Info("  operating room updated {0}", operatingRoomId);

        logger.Info("Scenario: PlannedSurgery create/update/delete...");
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);
        var surgeryId = await HisApiClient.AddPlannedSurgeryAsync(styx, start, end, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, operatingRoomId);
        tracker.Expect("PlannedSurgeryCreated", surgeryId, start, end);
        logger.Info("  created {0}", surgeryId);

        var start2 = start.AddDays(1);
        var end2 = end.AddDays(1);
        await HisApiClient.UpdatePlannedSurgeryAsync(styx, surgeryId, start2, end2, seeded.PatientId, seeded.DiagnosisId, surgeryTypeId, operatingRoomId);
        tracker.Expect("PlannedSurgeryUpdated", surgeryId, start2, end2);
        logger.Info("  updated {0}", surgeryId);

        await RunFhirScenarioAsync(styx, config, operatingRoomId, start2, end2, userToken, logger);

        await HisApiClient.DeletePlannedSurgeryAsync(styx, surgeryId);
        tracker.Expect("PlannedSurgeryDeleted", surgeryId, start2, end2);
        logger.Info("  deleted {0}", surgeryId);

        logger.Info("Scenario: ChecklistTemplate + Checklist create (core)...");
        await ChecklistApiClient.CreateChecklistTemplateAsync(styx, checklistTemplateId);
        logger.Info("  template created {0}", checklistTemplateId);

        tracker.Expect("CheckListCreated");
        await ChecklistApiClient.CreateChecklistAsync(styx, userToken, surgeryTypeId, checklistTemplateId);
        var createdChecklistId = await WaitForChecklistCreatedAsync(tracker, logger);
        logger.Info("  checklist created {0}", createdChecklistId);

        logger.Info("Scenario: Checklist edit (core)...");
        await ChecklistApiClient.UpdateOperatingRoomAsync(styx, userToken, createdChecklistId, operatingRoomId);
        logger.Info("  operating room updated");
        await ChecklistApiClient.UpdateOperatingDepartmentAsync(styx, userToken, createdChecklistId, departmentId);
        logger.Info("  operating department updated");
        await ChecklistApiClient.UpdateNumberMedicalHistoryAsync(styx, userToken, createdChecklistId, TestData.NumberMedicalHistory);
        logger.Info("  number medical history updated");

        var updatedChecklist = await ChecklistApiClient.GetChecklistAsync(styx, userToken, createdChecklistId);
        if (updatedChecklist.Id != createdChecklistId
            || updatedChecklist.OperatingRoom?.Id != operatingRoomId
            || updatedChecklist.OperatingDepartment?.Id != departmentId
            || updatedChecklist.NumberMedicalHistory != TestData.NumberMedicalHistory)
        {
            throw new InvalidOperationException(
                $"Checklist edit verification failed: room={updatedChecklist.OperatingRoom?.Id}, department={updatedChecklist.OperatingDepartment?.Id}, number='{updatedChecklist.NumberMedicalHistory}'.");
        }

        logger.Info("  checklist edit verified");

        logger.Info("Scenario: deletes...");
        await HisApiClient.DeleteOrganizationPartAsync(styx, operatingRoomId);
        tracker.Expect("OrganizationPartDeleted", operatingRoomId);
        logger.Info("  operating room deleted {0}", operatingRoomId);

        await HisApiClient.DeleteOrganizationPartAsync(styx, departmentId);
        tracker.Expect("OrganizationPartDeleted", departmentId);
        logger.Info("  department deleted {0}", departmentId);

        await HisApiClient.DeleteEmployeeAsync(styx, employeeId);
        tracker.Expect("EmployeeDeleted", employeeId);
        logger.Info("  employee deleted {0}", employeeId);

        await HisApiClient.DeleteSurgeryTypeAsync(styx, surgeryTypeId);
        tracker.Expect("SurgeryTypeDeleted", surgeryTypeId);
        logger.Info("  surgery type deleted {0}", surgeryTypeId);
    }

    private static async Task RunFhirScenarioAsync(
        HttpClient styx,
        SmokeTestConfig config,
        Guid operatingRoomId,
        DateTime surgeryStart,
        DateTime surgeryEnd,
        string userToken,
        ConsoleLogger logger)
    {
        logger.Info("Scenario: FHIR facade impersonating Ariadna (through styx)...");

        await DbSeeder.SeedFhirPatientsAsync(config.HisDbConnection);
        var apiKey = await DbSeeder.GetFhirApiKeyAsync(config.CoreDbConnection);

        using (var response = await PostFhirAuthAsync(styx, "00000000000000000000"))
        {
            Expect(response.StatusCode, HttpStatusCode.Unauthorized, "auth-fhir with a wrong api key");
        }

        string token;
        using (var response = await PostFhirAuthAsync(styx, apiKey))
        {
            Expect(response.StatusCode, HttpStatusCode.OK, "auth-fhir status");
            token = ReadAccessToken(await response.Content.ReadAsStringAsync());
        }

        logger.Info("  auth-fhir returned an access token");

        var patientPage = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/Patient?name={TestData.FhirPatientLastName}&_count=1");

        Expect(patientPage.GetProperty("total").GetInt32(), 3, "patient bundle total");
        Expect(patientPage.GetProperty("entry").GetArrayLength(), 1, "patient bundle page size");

        var firstPatientId = GetResourceId(patientPage, 0);
        Expect(Guid.TryParse(firstPatientId, out _), true, "patient id is a guid");

        var nextLink = GetLink(patientPage, "next");
        if (nextLink is null)
        {
            throw new InvalidOperationException("Patient bundle has no next link although total is 3 and page size is 1.");
        }

        Expect(nextLink.StartsWith(config.StyxUrl, StringComparison.OrdinalIgnoreCase), true, "next link is absolute");
        Expect(nextLink.Contains("/mvs-service/fhir/Patient", StringComparison.Ordinal), true, "next link path");

        var secondPage = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/Patient?name={TestData.FhirPatientLastName}&_count=1&_getpagesoffset=1");

        Expect(GetResourceId(secondPage, 0) == firstPatientId, false, "second patient page differs from the first");

        var singlePatient = await GetFhirJsonAsync(styx, token, $"/mvs-service/fhir/Patient/{firstPatientId}");
        Expect(TryGetString(singlePatient, "id"), firstPatientId, "patient by id");

        using (var response = await SendFhirAsync(styx, HttpMethod.Get, "/mvs-service/fhir/Patient/11111111-1111-1111-1111-111111111111", token))
        {
            Expect(response.StatusCode, HttpStatusCode.NotFound, "unknown patient id");
        }

        var seededPatient = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/Patient?name={TestData.PatientLastName}&_count=1");

        var seededPatientId = GetResourceId(seededPatient, 0);
        Expect(Guid.TryParse(seededPatientId, out _), true, "seeded patient id is a guid");

        var doctors = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/Practitioner?name={TestData.EmployeeLastName}&_count=1");

        Expect(doctors.GetProperty("total").GetInt32() >= 1, true, "practitioner bundle total");

        var doctorId = GetResourceId(doctors, 0);
        Expect(Guid.TryParse(doctorId, out _), true, "practitioner id is a guid");

        var doctorsById = await GetFhirJsonAsync(styx, token, $"/mvs-service/fhir/Practitioner?id={doctorId}");
        Expect(doctorsById.GetProperty("total").GetInt32(), 1, "practitioner bundle by id");

        var doctorsExcluding = await GetFhirJsonAsync(styx, token, $"/mvs-service/fhir/Practitioner?id%3Anot={doctorId}");
        Expect(ContainsResourceId(doctorsExcluding, doctorId), false, "practitioner id:not filter");

        var locations = await GetFhirJsonAsync(styx, token, $"/mvs-service/fhir/Location?name={TestData.OperatingRoomPartNameUpdated}");
        Expect(locations.GetProperty("total").GetInt32() >= 1, true, "location bundle total");
        Expect(GetLink(locations, "last") is null, true, "location bundle has no last link");
        Expect(ContainsResourceId(locations, operatingRoomId.ToString()), true, "location bundle contains the operating room");

        var from = Uri.EscapeDataString(surgeryStart.AddHours(-1).ToString("o"));
        var to = Uri.EscapeDataString(surgeryEnd.AddHours(1).ToString("o"));

        var procedures = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/Procedure?startDate={from}&endDate={to}&status=preparation&_count=1");

        Expect(procedures.GetProperty("total").GetInt32() >= 1, true, "procedure bundle total");
        Expect(GetLink(procedures, "last") is null, true, "procedure bundle has no last link");

        var procedure = procedures.GetProperty("entry")[0].GetProperty("resource");
        var procedureId = TryGetString(procedure, "id") ?? string.Empty;

        Expect(Guid.TryParse(procedureId, out _), true, "procedure id is a guid");
        Expect(TryGetString(procedure.GetProperty("subject"), "reference"), $"Patient/{seededPatientId}", "procedure subject reference");
        Expect(TryGetString(procedure.GetProperty("location"), "reference"), $"Location/{operatingRoomId}", "procedure location reference");

        var performed = procedure.TryGetProperty("performedPeriod", out var performedPeriod)
            ? performedPeriod
            : procedure.GetProperty("performed");

        Expect(TryGetString(performed, "start") is not null, true, "procedure performed period start");
        Expect(
            TryGetString(procedure.GetProperty("category").GetProperty("coding")[0], "display"),
            TestData.SurgeryTypeName,
            "procedure category display");

        var continuation = await GetFhirJsonAsync(
            styx,
            token,
            $"/mvs-service/fhir/?startDate={from}&endDate={to}&status=preparation&_count=1&_getpagesoffset=0");

        Expect(GetResourceId(continuation, 0), procedureId, "procedure continuation through the FHIR root");

        var procedureXml = $"""
            <Procedure xmlns="http://hl7.org/fhir">
              <status value="completed"/>
              <subject><reference value="Patient/{seededPatientId}"/></subject>
              <performedPeriod>
                <start value="{surgeryStart:o}"/>
                <end value="{surgeryEnd:o}"/>
              </performedPeriod>
            </Procedure>
            """;

        string createdId;
        using (var content = new StringContent(procedureXml, Encoding.UTF8, "application/fhir+xml"))
        using (var response = await SendFhirAsync(styx, HttpMethod.Post, "/mvs-service/fhir/Procedure", token, content))
        {
            Expect(response.StatusCode, HttpStatusCode.OK, "procedure create status");
            createdId = TryGetString(ReadJsonObject(await response.Content.ReadAsStringAsync()), "id") ?? string.Empty;
        }

        Expect(string.IsNullOrWhiteSpace(createdId), false, "procedure create returned an id");

        using (var content = new StringContent(procedureXml, Encoding.UTF8, "application/fhir+xml"))
        using (var response = await SendFhirAsync(styx, HttpMethod.Put, $"/mvs-service/fhir/Procedure/{createdId}", token, content))
        {
            Expect(response.StatusCode, HttpStatusCode.OK, "procedure update status");
            Expect(TryGetString(ReadJsonObject(await response.Content.ReadAsStringAsync()), "id"), createdId, "procedure update id");
        }

        logger.Info("  FHIR facade scenario passed");

        await RunLyra3IntegrationAsync(config, userToken, operatingRoomId, surgeryStart, surgeryEnd, logger);
    }

    private static async Task RunJeconbolScenarioAsync(HttpClient styx, SmokeTestConfig config, ConsoleLogger logger)
    {
        logger.Info("Scenario: Jeconbol contract (emulator)...");

        using var jeconbol = new HttpClient { BaseAddress = new Uri(config.JeconbolUrl) };
        var roomExternalId = config.JeconbolOperatingRoomExternalId;

        var rooms = await PostJeconbolAsync(jeconbol, "/operations/objectlist", new
        {
            index = "OperationRoom",
            list = new[] { new { id = roomExternalId } },
        });

        Expect(TryGetString(rooms, "index"), "OperationRoom", "objectlist index");
        Expect(rooms.GetProperty("list").GetArrayLength(), 1, "objectlist room count");
        Expect(
            TryGetString(rooms.GetProperty("list")[0].GetProperty("object"), "id"),
            roomExternalId,
            "objectlist room id");

        var schedule = await PostJeconbolAsync(jeconbol, "/operations/operationroomschedule", new
        {
            date = DateTime.Today.ToString("o"),
            operRoomId = roomExternalId,
        });

        Expect(schedule.GetProperty("list").GetArrayLength() >= 1, true, "schedule is not empty");

        var surgery = schedule.GetProperty("list")[0].GetProperty("operation");
        var surgeryId = TryGetString(surgery, "id")
            ?? throw new InvalidOperationException("schedule surgery id is missing");
        var patientId = TryGetString(surgery, "patientId")
            ?? throw new InvalidOperationException("schedule surgery patient id is missing");
        var operatorId = TryGetString(surgery, "plannedOperator")
            ?? throw new InvalidOperationException("schedule surgery operator id is missing");

        var patients = await PostJeconbolAsync(jeconbol, "/operations/objectlist", new
        {
            index = "Patient",
            list = new[] { new { id = patientId } },
        });

        Expect(patients.GetProperty("list").GetArrayLength(), 1, "objectlist patient count");
        Expect(
            TryGetString(patients.GetProperty("list")[0].GetProperty("object"), "id"),
            patientId,
            "objectlist patient id");

        var persons = await PostJeconbolAsync(jeconbol, "/operations/objectlist", new
        {
            index = "Person",
            list = new[] { new { id = operatorId } },
        });

        Expect(persons.GetProperty("list").GetArrayLength(), 1, "objectlist person count");

        logger.Info("  Jeconbol contract passed, surgery {0}", surgeryId);

        logger.Info("Scenario: operating room binding through the Gen4 API...");

        var adminToken = await GetAdminTokenAsync(styx, config, logger);

        Guid rootId;
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Get, "/his/api/organization/parts", adminToken))
        {
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            rootId = body.EnumerateArray()
                .First(part => TryGetString(part, "type") == "Organization")
                .GetProperty("id")
                .GetGuid();
        }

        Guid boundRoomId;
        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartName,
            type = "OperatingRoom",
            partOf = rootId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Post, "/his/api/organization/parts", adminToken, content))
        {
            EnsureSuccess(response, "organization part create");
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            boundRoomId = body.GetProperty("id").GetGuid();
            Expect(TryGetString(body, "externalHisId") is null, true, "new room has no external id");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartName,
            type = "OperatingRoom",
            partOf = rootId,
            externalHisId = TestData.JeconbolSecondOperatingRoomExternalId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            EnsureSuccess(response, "organization part external id binding");
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            Expect(
                TryGetString(body, "externalHisId"),
                TestData.JeconbolSecondOperatingRoomExternalId,
                "binding response external id");
        }

        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Get, $"/his/api/organization/parts/{boundRoomId}", adminToken))
        {
            EnsureSuccess(response, "organization part get by id");
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            Expect(TryGetString(body, "externalHisId"), TestData.JeconbolSecondOperatingRoomExternalId, "get by id external id");
        }

        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Get, "/his/api/organization/parts", adminToken))
        {
            EnsureSuccess(response, "organization parts list");
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            var listed = body.EnumerateArray().First(part => part.GetProperty("id").GetGuid() == boundRoomId);
            Expect(TryGetString(listed, "externalHisId"), TestData.JeconbolSecondOperatingRoomExternalId, "list external id");
        }

        using (var response = await SendAuthorizedAsync(
            styx,
            HttpMethod.Get,
            $"/his/api/organization/parts/checkExternalHisExistence/{TestData.JeconbolSecondOperatingRoomExternalId}",
            adminToken))
        {
            EnsureSuccess(response, "external his room existence check");
        }

        using (var response = await SendAuthorizedAsync(
            styx,
            HttpMethod.Get,
            "/his/api/organization/parts/checkExternalHisExistence/99999999-9999-9999-9999-999999999999",
            adminToken))
        {
            Expect(response.StatusCode, HttpStatusCode.UnprocessableEntity, "unknown external his room");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartNameUpdated,
            type = "OperatingRoom",
            partOf = rootId,
            externalHisId = TestData.JeconbolSecondOperatingRoomExternalId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            EnsureSuccess(response, "organization part rename with external id");
        }

        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Get, $"/his/api/organization/parts/{boundRoomId}", adminToken))
        {
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            Expect(TryGetString(body, "name"), TestData.JeconbolOperatingRoomPartNameUpdated, "renamed part name");
            Expect(TryGetString(body, "externalHisId"), TestData.JeconbolSecondOperatingRoomExternalId, "binding survives a rename");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartNameUpdated,
            type = "OperatingRoom",
            partOf = rootId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            EnsureSuccess(response, "organization part rename without external id");
        }

        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Get, $"/his/api/organization/parts/{boundRoomId}", adminToken))
        {
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            Expect(TryGetString(body, "externalHisId") is null, true, "put without external id unbinds");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartNameUpdated,
            type = "OperatingRoom",
            partOf = rootId,
            externalHisId = "not-a-guid",
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            Expect(response.StatusCode, HttpStatusCode.BadRequest, "non-guid external id");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolDepartmentPartName,
            type = "Department",
            partOf = rootId,
            externalHisId = TestData.JeconbolSecondOperatingRoomExternalId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Post, "/his/api/organization/parts", adminToken, content))
        {
            Expect(response.StatusCode, HttpStatusCode.UnprocessableEntity, "external id on a department");
        }

        Guid secondRoomId;
        using (var content = JsonBody(new
        {
            name = TestData.JeconbolSecondRoomPartName,
            type = "OperatingRoom",
            partOf = rootId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Post, "/his/api/organization/parts", adminToken, content))
        {
            EnsureSuccess(response, "second organization part create");
            var body = ReadJsonObject(await response.Content.ReadAsStringAsync());
            secondRoomId = body.GetProperty("id").GetGuid();
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartNameUpdated,
            type = "OperatingRoom",
            partOf = rootId,
            externalHisId = TestData.JeconbolSecondOperatingRoomExternalId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            EnsureSuccess(response, "organization part rebinding");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolSecondRoomPartName,
            type = "OperatingRoom",
            partOf = rootId,
            externalHisId = TestData.JeconbolSecondOperatingRoomExternalId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{secondRoomId}", adminToken, content))
        {
            Expect(response.StatusCode, HttpStatusCode.Conflict, "duplicate external id");
        }

        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Delete, $"/his/api/organization/parts/{secondRoomId}", adminToken))
        {
            EnsureSuccess(response, "second organization part delete");
        }

        using (var content = JsonBody(new
        {
            name = TestData.JeconbolOperatingRoomPartNameUpdated,
            type = "OperatingRoom",
            partOf = rootId,
        }))
        using (var response = await SendAuthorizedAsync(styx, HttpMethod.Put, $"/his/api/organization/parts/{boundRoomId}", adminToken, content))
        {
            EnsureSuccess(response, "organization part external id unbinding");
        }

        logger.Info("  binding scenario passed");

        logger.Info("Scenario: Jeconbol configuration through the Gen4 API...");

        var jeconbolConfiguration = await GetJeconbolConfigurationAsync(styx, adminToken);
        var originalAuthorization = TryGetString(jeconbolConfiguration, "authorization") ?? string.Empty;

        await PutJeconbolConfigurationAsync(styx, adminToken, jeconbolConfiguration, TestData.JeconbolTestAuthorization);

        var updatedConfiguration = await RetryAsync(
            async () =>
            {
                var current = await GetJeconbolConfigurationAsync(styx, adminToken);

                return TryGetString(current, "authorization") == TestData.JeconbolTestAuthorization
                    ? current
                    : throw new InvalidOperationException("Jeconbol configuration was not updated.");
            },
            "Jeconbol configuration update",
            logger,
            TimeSpan.FromSeconds(30));

        Expect(
            TryGetString(updatedConfiguration, "serviceUrl"),
            TryGetString(jeconbolConfiguration, "serviceUrl"),
            "jeconbol service url is kept");

        await PutJeconbolConfigurationAsync(styx, adminToken, updatedConfiguration, originalAuthorization);

        await RetryAsync(
            async () =>
            {
                var current = await GetJeconbolConfigurationAsync(styx, adminToken);

                return TryGetString(current, "authorization") == originalAuthorization
                    ? true
                    : throw new InvalidOperationException("Jeconbol configuration was not restored.");
            },
            "Jeconbol configuration restore",
            logger,
            TimeSpan.FromSeconds(30));

        logger.Info("  configuration scenario passed");

        logger.Info("Scenario: Jeconbol import into HIS...");

        async Task<JsonElement> GetJeconbolConfigurationAsync(HttpClient client, string token)
        {
            using var response = await SendAuthorizedAsync(client, HttpMethod.Get, "/core/api/config/jeconbol", token);
            EnsureSuccess(response, "jeconbol configuration get");

            return ReadJsonObject(await response.Content.ReadAsStringAsync());
        }

        async Task PutJeconbolConfigurationAsync(HttpClient client, string token, JsonElement current, string authorization)
        {
            using var content = JsonBody(new
            {
                isEnabled = current.GetProperty("isEnabled").GetBoolean(),
                serviceUrl = TryGetString(current, "serviceUrl") ?? string.Empty,
                authorization,
            });
            using var response = await SendAuthorizedAsync(client, HttpMethod.Put, "/core/api/config/jeconbol", token, content);
            EnsureSuccess(response, "jeconbol configuration put");
        }

        var apiKey = await DbSeeder.GetFhirApiKeyAsync(config.CoreDbConnection);
        string token;
        using (var response = await PostFhirAuthAsync(styx, apiKey))
        {
            EnsureSuccess(response, "auth-fhir");
            token = ReadAccessToken(await response.Content.ReadAsStringAsync());
        }

        var from = Uri.EscapeDataString(DateTime.Today.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.Today.AddDays(9).ToString("o"));

        var procedures = await RetryAsync(
            async () =>
            {
                var bundle = await GetFhirJsonAsync(
                    styx,
                    token,
                    $"/mvs-service/fhir/Procedure?startDate={from}&endDate={to}&status=preparation&_count=50");

                return bundle.GetProperty("total").GetInt32() > 0
                    ? bundle
                    : throw new InvalidOperationException("No planned surgeries imported from Jeconbol yet.");
            },
            "Jeconbol import",
            logger,
            TimeSpan.FromMinutes(4));

        var entryCount = procedures.GetProperty("entry").GetArrayLength();
        var ids = new HashSet<string>();

        for (var index = 0; index < entryCount; index++)
        {
            var resourceId = GetResourceId(procedures, index);
            Expect(ids.Add(resourceId), true, "imported procedure ids are unique");
        }

        var procedure = procedures.GetProperty("entry")[0].GetProperty("resource");
        var plannedSurgeryId = TryGetString(procedure, "id") ?? string.Empty;
        var subjectReference = TryGetString(procedure.GetProperty("subject"), "reference") ?? string.Empty;
        var locationReference = GetProcedureLocationReference(procedure);

        Expect(subjectReference.StartsWith("Patient/", StringComparison.Ordinal), true, "imported procedure subject");
        Expect(locationReference.StartsWith("Location/", StringComparison.Ordinal), true, "imported procedure location");
        Expect(entryCount >= 2, true, "at least two imported procedures");

        var secondProcedure = procedures.GetProperty("entry")[1].GetProperty("resource");
        var secondPlannedSurgeryId = TryGetString(secondProcedure, "id") ?? string.Empty;
        var secondSubjectReference = TryGetString(secondProcedure.GetProperty("subject"), "reference") ?? string.Empty;
        var secondLocationReference = GetProcedureLocationReference(secondProcedure);

        var jeconbolStartTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        for (var dayShift = 0; dayShift < TestData.JeconbolImportDaysAhead; dayShift++)
        {
            var roomSchedule = await PostJeconbolAsync(
                jeconbol,
                "/operations/operationroomschedule",
                new
                {
                    date = DateTime.Now.Date.AddDays(dayShift),
                    operRoomId = config.JeconbolOperatingRoomExternalId,
                });

            foreach (var item in roomSchedule.GetProperty("list").EnumerateArray())
            {
                var operation = item.GetProperty("operation");

                if (TryGetString(operation, "id") is { } externalId
                    && TryGetString(operation, "plannedTime") is { } plannedTime)
                {
                    jeconbolStartTimes[externalId] = ParseJeconbolInstant(plannedTime);
                }
            }
        }

        var importedPlans = await HisDbReader.ReadJeconbolPlannedSurgeryTimesAsync(config.HisDbConnection);

        var comparablePlans = importedPlans
            .Where(plan => jeconbolStartTimes.ContainsKey(plan.ExternalHisId))
            .ToList();

        if (comparablePlans.Count == 0)
        {
            logger.Info("  import checks skipped: HIS holds no plans of the current Jeconbol schedule");
        }
        else
        {
            var teamMembers = await RetryAsync(
                async () =>
                {
                    var members = (await HisDbReader.ReadJeconbolPlannedSurgeryTeamAsync(config.HisDbConnection))
                        .Where(member => jeconbolStartTimes.ContainsKey(member.ExternalHisId))
                        .ToList();

                    return members.Count > 0
                           && members.All(member => member.MembersCount == 1)
                           && members.All(member => member.RoleName == TestData.JeconbolOperatorRoleName)
                        ? members
                        : throw new InvalidOperationException(
                            $"Imported plans must have a single '{TestData.JeconbolOperatorRoleName}' team member.");
                },
                "Jeconbol operator role",
                logger,
                TimeSpan.FromMinutes(2));

            logger.Info("  operator role check passed ({0} plans)", teamMembers.Count);

            var defaultTimePlans = comparablePlans
                .Where(plan => plan.Start.Year <= 2000)
                .ToList();

            Expect(
                defaultTimePlans.Count,
                0,
                $"imported plans keep a real start time, default: {string.Join(", ", defaultTimePlans.Take(3).Select(plan => plan.ExternalHisId))}");

            logger.Info("  import time check passed ({0} plans)", comparablePlans.Count);

            var updatedPlanCount = await RetryAsync(
                async () =>
                {
                    var stamps = await HisDbReader.ReadJeconbolPlannedSurgeryTimesAsync(config.HisDbConnection);
                    var updated = stamps.Count(stamp =>
                        jeconbolStartTimes.ContainsKey(stamp.ExternalHisId) && stamp.UpdatedAt > stamp.CreatedAt);

                    return updated > 0
                        ? updated
                        : throw new InvalidOperationException(
                            "No imported planned surgery has been updated since creation yet.");
                },
                "Jeconbol import update",
                logger,
                TimeSpan.FromMinutes(3));

            logger.Info("  import update check passed ({0} plans updated)", updatedPlanCount);
        }

        logger.Info("  import scenario passed ({0} procedures)", entryCount);

        logger.Info("Scenario: finished surgery export to Jeconbol...");

        await IngestFinishedSurgery(plannedSurgeryId, subjectReference, locationReference, 90);
        await IngestFinishedSurgery(secondPlannedSurgeryId, secondSubjectReference, secondLocationReference, 120);

        logger.Info("  export scenario passed");

        async Task<FinishedSurgeryExportState> IngestFinishedSurgery(
            string planId,
            string subject,
            string location,
            int durationMinutes)
        {
            var finishedSurgeryId = Guid.NewGuid();
            var start = DateTime.UtcNow.AddHours(-3);

            var finishedSurgery = new
            {
                resourceType = "Procedure",
                id = planId,
                status = "completed",
                subject = new { reference = subject },
                location = new[] { new { reference = location } },
                performedPeriod = new
                {
                    start = start.ToString("o"),
                    end = start.AddMinutes(durationMinutes).ToString("o"),
                },
                extension = new[]
                {
                    new
                    {
                        url = TestData.JeconbolFinishedSurgeryIdExtensionUrl,
                        valueString = finishedSurgeryId.ToString(),
                    },
                },
            };

            using (var content = new StringContent(JsonSerializer.Serialize(finishedSurgery), Encoding.UTF8, "application/fhir+json"))
            using (var response = await SendFhirAsync(styx, HttpMethod.Put, $"/mvs-service/fhir/Procedure/{planId}", token, content))
            {
                EnsureSuccess(response, "finished surgery ingest");
            }

            var exported = await RetryAsync(
                async () =>
                {
                    var state = await HisDbReader.ReadFinishedSurgeryAsync(config.HisDbConnection, finishedSurgeryId);

                    return state is { ExportState: TestData.JeconbolExportedState }
                        ? state.Value
                        : throw new InvalidOperationException(
                            state is null
                                ? $"Finished surgery {finishedSurgeryId} is not in HIS yet."
                                : $"Surgery {finishedSurgeryId} is not exported yet (state '{state.Value.ExportState}', error '{state.Value.ErrorMessage}').");
                },
                "Jeconbol export",
                logger,
                TimeSpan.FromMinutes(4));

            Expect(exported.ErrorMessage ?? string.Empty, string.Empty, "exported surgery error message");
            Expect(
                exported.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                start.ToString("yyyy-MM-ddTHH:mm:ss"),
                "exported surgery start time");
            Expect(
                (int)Math.Round((exported.Finish - exported.Start).TotalMinutes),
                durationMinutes,
                "exported surgery duration");
            Expect(exported.PlannedSurgeryExternalHisId is not null, true, "exported planned surgery external id");
            Expect(exported.PatientExternalHisId is not null, true, "exported patient external id");
            Expect(exported.OperatingRoomExternalHisId is not null, true, "exported operating room external id");
            Expect(exported.OperatorExternalHisId is not null, true, "exported operator external id");

            return exported;
        }
    }

    private static async Task<JsonElement> PostJeconbolAsync(HttpClient jeconbol, string path, object payload)
    {
        using var content = JsonBody(payload);
        using var response = await jeconbol.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jeconbol POST {path} returned {(int)response.StatusCode}: {body}");
        }

        return ReadJsonObject(body);
    }

    private static async Task<string> GetAdminTokenAsync(HttpClient styx, SmokeTestConfig config, ConsoleLogger logger)
    {
        var token = await CoreApiClient.GetTokenAsync(styx, "/core/api/tokens/admin", TestData.AdminLogin, TestData.Password);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new InvalidOperationException($"{what} returned {(int)response.StatusCode}.");
    }

    private static async Task RunLyra3IntegrationAsync(
        SmokeTestConfig config,
        string userToken,
        Guid operatingRoomId,
        DateTime surgeryStart,
        DateTime surgeryEnd,
        ConsoleLogger logger)
    {
        logger.Info("Scenario: live Lyra3 (Jumbo) treating Gen4 as Ariadna...");

        using var jumbo = new HttpClient { BaseAddress = new Uri(config.Lyra3JumboUrl) };
        using var core = new HttpClient { BaseAddress = new Uri(config.Lyra3CoreUrl) };
        using var auth = new HttpClient { BaseAddress = new Uri(config.Lyra3AuthUrl) };

        var adminToken = await LoginLyra3AdministratorAsync(auth);

        using (var response = await SendLyra3Async(core, HttpMethod.Delete, "/api/ariadnaConfiguration", adminToken))
        {
            Expect(response.StatusCode, HttpStatusCode.OK, "ariadna configuration reset before the run");
        }

        var apiKey = await DbSeeder.GetFhirApiKeyAsync(config.CoreDbConnection);

        using (var content = JsonBody(new { url = $"{config.StyxUrl.TrimEnd('/')}/", apiKey }))
        using (var response = await SendLyra3Async(core, HttpMethod.Post, "/api/ariadnaConfiguration", adminToken, content))
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Ariadna configuration was rejected: {(int)response.StatusCode} {body}");
            }
        }

        logger.Info("  Ariadna configuration accepted by Core and pushed to Jumbo");

        await WaitForHisIntegrationAsync(jumbo, adminToken, "ariadna", logger);

        var patients = await GetLyra3JsonAsync(
            jumbo,
            userToken,
            $"/api/patients?limit=10&pageNumber=0&filter={TestData.FhirPatientLastName}");

        var patientItems = patients.GetProperty("items");
        Expect(patientItems.GetArrayLength(), 3, "Jumbo patients imported from Gen4");

        Expect(
            Guid.TryParse(TryGetString(patientItems[0], "id") ?? string.Empty, out _),
            true,
            "Jumbo patient id is the Gen4 guid");
        Expect(TryGetString(patientItems[0], "lastName"), TestData.FhirPatientLastName, "Jumbo patient last name");

        var doctors = await GetLyra3JsonAsync(
            jumbo,
            userToken,
            $"/api/doctors?limit=10&pageNumber=0&filter={TestData.EmployeeLastName}");

        var doctorItems = doctors.GetProperty("items");
        Expect(doctorItems.GetArrayLength() >= 1, true, "Jumbo doctors imported from Gen4");
        Expect(
            Guid.TryParse(TryGetString(doctorItems[0], "id") ?? string.Empty, out _),
            true,
            "Jumbo doctor id is the Gen4 guid");

        var from = Uri.EscapeDataString(surgeryStart.AddHours(-1).ToString("o"));
        var to = Uri.EscapeDataString(surgeryEnd.AddHours(1).ToString("o"));

        var surgeries = await GetLyra3JsonAsync(jumbo, userToken, $"/api/plannedSurgeries?from={from}&to={to}");
        Expect(surgeries.GetArrayLength() >= 1, true, "Jumbo planned surgeries imported from Gen4");

        var surgery = surgeries[0];
        Expect(
            TryGetString(surgery.GetProperty("patient"), "lastName"),
            TestData.PatientLastName,
            "Jumbo surgery patient resolved through the Gen4 FHIR references");
        Expect(
            Guid.TryParse(TryGetString(surgery, "externalHisId") ?? string.Empty, out _),
            true,
            "Jumbo surgery externalHisId is the Gen4 guid");
        Expect(
            Guid.TryParse(TryGetString(surgery.GetProperty("patient"), "externalHisId") ?? string.Empty, out _),
            true,
            "Jumbo surgery patient externalHisId is the Gen4 guid");

        var locations = await GetLyra3JsonAsync(
            jumbo,
            adminToken,
            $"/api/externalHis/locations?name={TestData.OperatingRoomPartNameUpdated}");

        Expect(locations.GetArrayLength() >= 1, true, "Jumbo external HIS locations imported from Gen4");
        Expect(
            locations.EnumerateArray().Any(location => TryGetString(location, "id") == operatingRoomId.ToString()),
            true,
            "Jumbo external HIS location matches the Gen4 operating room");

        using (var response = await SendLyra3Async(core, HttpMethod.Delete, "/api/ariadnaConfiguration", adminToken))
        {
            Expect(response.StatusCode, HttpStatusCode.OK, "ariadna configuration removal");
        }

        await WaitForHisIntegrationAsync(jumbo, adminToken, null, logger);

        logger.Info("  live Lyra3 integration scenario passed");
    }

    private static async Task<string> LoginLyra3AdministratorAsync(HttpClient auth)
    {
        using var content = JsonBody(new { login = TestData.Lyra3AdminLogin, password = TestData.Lyra3AdminPassword });
        using var response = await SendLyra3Async(auth, HttpMethod.Post, "/api/tokens/administrators/byLoginPassword", token: null, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lyra3 administrator login returned {(int)response.StatusCode}: {body}");
        }

        return TryGetString(ReadJsonObject(body), "accessToken")
            ?? throw new InvalidOperationException($"Lyra3 administrator login returned no access token: {body}");
    }

    private static async Task WaitForHisIntegrationAsync(
        HttpClient jumbo,
        string adminToken,
        string? expectedType,
        ConsoleLogger logger)
    {
        const int attempts = 30;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var response = await SendLyra3Async(jumbo, HttpMethod.Get, "/api/integrations/his", adminToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var type = TryGetString(ReadJsonObject(body), "type");

                if (string.Equals(type, expectedType, StringComparison.Ordinal))
                {
                    logger.Info("  Jumbo HIS integration is '{0}'", expectedType ?? "none");
                    return;
                }
            }

            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException($"Jumbo did not switch its HIS integration to '{expectedType ?? "none"}'.");
    }

    private static async Task<JsonElement> GetLyra3JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendLyra3Async(client, HttpMethod.Get, path, token);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lyra3 GET {path} returned {(int)response.StatusCode}: {body}");
        }

        return ReadJsonObject(body);
    }

    private static StringContent JsonBody(object payload)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task<HttpResponseMessage> SendLyra3Async(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostFhirAuthAsync(HttpClient styx, string apiKey)
    {
        using var content = JsonContent.Create(new { api_key = apiKey });
        return await SendFhirAsync(styx, HttpMethod.Post, "/mvs-service/fhir/auth-fhir", token: null, content);
    }

    private static async Task<HttpResponseMessage> SendFhirAsync(
        HttpClient styx,
        HttpMethod method,
        string path,
        string? token,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await styx.SendAsync(request);
    }

    private static async Task<JsonElement> GetFhirJsonAsync(HttpClient styx, string token, string path)
    {
        using var response = await SendFhirAsync(styx, HttpMethod.Get, path, token);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FHIR GET {path} returned {(int)response.StatusCode}: {body}");
        }

        return ReadJsonObject(body);
    }

    private static JsonElement ReadJsonObject(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static string ReadAccessToken(string body)
        => TryGetString(ReadJsonObject(body), "accessToken")
            ?? throw new InvalidOperationException($"auth-fhir returned no access token: {body}");

    private static string? TryGetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime ParseJeconbolInstant(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).UtcDateTime;

    private static string GetProcedureLocationReference(JsonElement procedure)
    {
        if (!procedure.TryGetProperty("location", out var locations))
        {
            return string.Empty;
        }

        var location = locations.ValueKind == JsonValueKind.Array ? locations[0] : locations;
        return TryGetString(location, "reference") ?? string.Empty;
    }

    private static string GetResourceId(JsonElement bundle, int index)
        => TryGetString(bundle.GetProperty("entry")[index].GetProperty("resource"), "id") ?? string.Empty;

    private static bool ContainsResourceId(JsonElement bundle, string id)
    {
        if (!bundle.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("resource", out var resource) && TryGetString(resource, "id") == id)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetLink(JsonElement bundle, string relation)
    {
        if (!bundle.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (TryGetString(link, "relation") == relation)
            {
                return TryGetString(link, "url");
            }
        }

        return null;
    }

    private static async Task<Guid> WaitForChecklistCreatedAsync(NotificationTracker tracker, ConsoleLogger logger)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var id = tracker.SnapshotReceived()
                .Where(notification => notification.Method == "CheckListCreated")
                .Select(notification => (Guid?)notification.Id)
                .LastOrDefault();

            if (id is Guid value)
            {
                return value;
            }

            await Task.Delay(250);
        }

        logger.Error("CheckListCreated notification was not received within the timeout.");
        throw new InvalidOperationException("CheckListCreated notification was not received within the timeout.");
    }

    private static async Task<HubConnection> ConnectHubAsync(
        SmokeTestConfig config,
        string path,
        string? userToken,
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
