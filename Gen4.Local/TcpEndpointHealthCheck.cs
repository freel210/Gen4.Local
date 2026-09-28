using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gen4.Local;

/// <summary>
/// Readiness gate for a Gen4 service, based on its listener actually accepting connections.
/// <para>
/// Aspire treats a resource without health checks as ready as soon as its process is spawned,
/// which releases <c>WaitFor</c> dependents far too early: <c>his-api</c> reads its configuration
/// over HTTP from <c>core-api</c> and only tolerates a few seconds of unavailability
/// (<c>Gen4.HP.Configuration.ConfigurationDefines.Retries</c>). This check makes Aspire wait for
/// Kestrel to bind instead.
/// </para>
/// A TCP connect is used rather than an HTTPS request: <c>/ok</c> is mapped before
/// <c>StartAsync</c>, so an accepted connection already implies the endpoint is servable, and this
/// avoids depending on the stand's mTLS client certificate.
/// </summary>
public sealed class TcpEndpointHealthCheck(int port, string host = "127.0.0.1") : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);

            return client.Connected
                ? HealthCheckResult.Healthy($"{host}:{port} is accepting connections")
                : HealthCheckResult.Unhealthy($"{host}:{port} refused the connection");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy($"{host}:{port} is not accepting connections: {exception.Message}");
        }
    }
}
