using System.Net;
using System.Net.Http.Json;
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

            await RunScenarioAsync(styx, tracker, seeded, userToken, checklistTemplateId, logger);

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
