var builder = DistributedApplication.CreateBuilder(args);

var pgUser = builder.AddParameter("pg-user", "postgres");
var pgPassword = builder.AddParameter("pg-password", "postgres");

var postgres = builder.AddPostgres("postgres", userName: pgUser, password: pgPassword)
    .WithImage("postgres", "16-alpine")
    .WithDataVolume()
    .WithPgAdmin()
    .WithEndpoint(port: 5432, targetPort: 5432, name: "tcp");

var natsUser = builder.AddParameter("nats-user", "nats");
var natsPassword = builder.AddParameter("nats-password", "nats", secret: true);

var nats = builder.AddNats("nats", userName: natsUser, password: natsPassword)
    .WithImage("nats", "latest")
    .WithEndpoint(port: 4222, targetPort: 4222, name: "tcp")
    .WithJetStream()
    .WithDataVolume();

var configPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Config";
var certsPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Certs";
var tempPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Temp";

Directory.CreateDirectory(tempPath);

var initJob = builder.AddProject<Projects.Gen4_Local_Init>("init-job");
var hisApi = builder.AddProject<Projects.Gen4_HP_HIS_API>("his-api");
var coreApi = builder.AddProject<Projects.Gen4_HP_Core_API>("core-api");
var styxApi = builder.AddProject<Projects.Gen4_HP_Styx_API>("styx-api");

initJob
    .WithEnvironment("GEN4HP_CONFIGROOT", configPath)
    .WithEnvironment("GEN4HP_CERTSROOT", certsPath)
    .WaitFor(postgres)
    .WaitFor(nats);

hisApi
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("HIS_CORE_ENDPOINT", coreApi.GetEndpoint("https").HostPort())
    .WithEndpoint(port: 5200, scheme: "https", isProxied: false)
    .WaitForCompletion(initJob);

coreApi
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("CORE_TEMP_DIR", tempPath)
    .WithEnvironment("HIS_ENDPOINT", hisApi.GetEndpoint("https").HostPort())
    .WithEnvironment("STYX_ENDPOINT", styxApi.GetEndpoint("grpc").HostPort())
    .WithEndpoint(port: 5100, scheme: "https", isProxied: false)
    .WaitForCompletion(initJob);

styxApi
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("STYX_HTTPS_PORT", "5050")
    .WithEnvironment("CORE_ENDPOINT", coreApi.GetEndpoint("https").HostPort())
    .WithEnvironment("HIS_ENDPOINT", hisApi.GetEndpoint("https").HostPort())
    .WithEndpoint(port: 5050, scheme: "https", name: "https", isProxied: false)
    .WithEndpoint(port: 5001, scheme: "https", name: "grpc", isProxied: false)
    .WaitForCompletion(initJob);

var styxHttps = styxApi.GetEndpoint("https");

var smokeTest = builder.AddProject<Projects.Gen4_Local_SmokeTest>("smoke-test")
    .WithEnvironment("STYX_URL", ReferenceExpression.Create($"https://{styxHttps.Property(EndpointProperty.Host)}:{styxHttps.Property(EndpointProperty.Port)}"))
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("HIS_DB_CONNECTION", "Host=localhost;Username=postgres;Password=postgres;Database=his-db")
    .WithEnvironment("CORE_DB_CONNECTION", "Host=localhost;Username=postgres;Password=postgres;Database=core-db")
    .WaitFor(styxApi);

builder.Build().Run();

public static class EndpointReferenceExtensions
{
    public static ReferenceExpression HostPort(this EndpointReference endpoint)
    {
        return ReferenceExpression.Create(
            $"{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    }
}
