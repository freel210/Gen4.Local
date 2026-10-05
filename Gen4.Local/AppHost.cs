using Gen4.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

var mongoUser = builder.AddParameter("mongo-user", "root");
var mongoPassword = builder.AddParameter("mongo-password", "Passw0rd123");

var mongo = builder.AddMongoDB("mongo", userName: mongoUser, password: mongoPassword)
    .WithImage("mongo", "8.3")
    .WithDataVolume()
    .WithEndpoint(port: 27018, targetPort: 27017, name: "tcp");

var redisPassword = builder.AddParameter("redis-password", "Passw0rd123");
var redis = builder.AddRedis("redis", password: redisPassword)
    .WithImage("redis", "7")
    .WithEndpoint(port: 6380, targetPort: 6380, name: "secondary", isProxied: false);

var rabbitUser = builder.AddParameter("rabbit-user", "lyra3");
var rabbitPassword = builder.AddParameter("rabbit-password", "Passw0rd123");

var rabbitmq = builder.AddRabbitMQ("rabbitmq", userName: rabbitUser, password: rabbitPassword)
    .WithImage("rabbitmq", "3.13")
    .WithDataVolume()
    .WithEndpoint(port: 5673, targetPort: 5672, name: "tcp");

var s3AccessKey = builder.AddParameter("s3-access-key", "lyra3");
var s3SecretKey = builder.AddParameter("s3-secret-key", "Passw0rd123");

var s3InitScripts = @"C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\LocalStack\init";

var s3Storage = builder.AddContainer("s3-storage", "gresau/localstack-persist", "4.14.0")
    .WithEnvironment("SERVICES", "s3")
    .WithEnvironment("AWS_ACCESS_KEY_ID", s3AccessKey.Resource.Value)
    .WithEnvironment("AWS_SECRET_ACCESS_KEY", s3SecretKey.Resource.Value)
    .WithEnvironment("AWS_REGION", "us-east-1")
    .WithEnvironment("PERSIST_S3", "1")
    .WithVolume("localstack-persist-data", "/persisted-data")
    .WithBindMount(s3InitScripts, "/etc/localstack/init/ready.d")
    .WithEndpoint(port: 4566, targetPort: 4566, name: "s3");

var configPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Config";
var certsPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Certs";
var tempPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Temp";

Directory.CreateDirectory(tempPath);

const int hisPort = 5200;
const int corePort = 5100;
const int styxPort = 5050;

// Without a health check Aspire considers a resource ready the moment its process is spawned and
// releases WaitFor dependents immediately. his-api then reads its configuration over HTTP from
// core-api and gives up after ~6s, so on a cold start it died before core-api ever bound its
// listener. These gates hold dependents until Kestrel is actually accepting connections.
var coreListening = new TcpEndpointHealthCheck(corePort);
var hisListening = new TcpEndpointHealthCheck(hisPort);
var styxListening = new TcpEndpointHealthCheck(styxPort);

builder.Services
    .AddHealthChecks()
    .AddAsyncCheck("core-listening", ct => coreListening.CheckHealthAsync(new HealthCheckContext(), ct), timeout: TimeSpan.FromSeconds(5))
    .AddAsyncCheck("his-listening", ct => hisListening.CheckHealthAsync(new HealthCheckContext(), ct), timeout: TimeSpan.FromSeconds(5))
    .AddAsyncCheck("styx-listening", ct => styxListening.CheckHealthAsync(new HealthCheckContext(), ct), timeout: TimeSpan.FromSeconds(5));

var initJob = builder.AddProject<Projects.Gen4_Local_Init>("init-job");
var hisApi = builder.AddProject<Projects.Gen4_HP_HIS_API>("his-api")
    .WithHealthCheck("his-listening");
var coreApi = builder.AddProject<Projects.Gen4_HP_Core_API>("core-api")
    .WithHealthCheck("core-listening");
var styxApi = builder.AddProject<Projects.Gen4_HP_Styx_API>("styx-api")
    .WithHealthCheck("styx-listening");

initJob
    .WithEnvironment("GEN4HP_CONFIGROOT", configPath)
    .WithEnvironment("GEN4HP_CERTSROOT", certsPath)
    .WaitFor(postgres)
    .WaitFor(nats);

hisApi
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("HIS_CORE_ENDPOINT", coreApi.GetEndpoint("https").HostPort())
    .WithEndpoint(port: hisPort, scheme: "https", isProxied: false)
    .WaitFor(coreApi)
    .WaitForCompletion(initJob);

coreApi
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("CORE_TEMP_DIR", tempPath)
    .WithEnvironment("HIS_ENDPOINT", hisApi.GetEndpoint("https").HostPort())
    .WithEnvironment("STYX_ENDPOINT", styxApi.GetEndpoint("grpc").HostPort())
    .WithEndpoint(port: corePort, scheme: "https", isProxied: false)
    .WaitForCompletion(initJob);

styxApi
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("STYX_HTTPS_PORT", styxPort.ToString())
    .WithEnvironment("CORE_ENDPOINT", coreApi.GetEndpoint("https").HostPort())
    .WithEnvironment("HIS_ENDPOINT", hisApi.GetEndpoint("https").HostPort())
    .WithEndpoint(port: styxPort, scheme: "https", name: "https", isProxied: false)
    .WithEndpoint(port: 5001, scheme: "https", name: "grpc", isProxied: false)
    .WaitFor(coreApi)
    .WaitFor(hisApi)
    .WaitForCompletion(initJob);

var styxHttps = styxApi.GetEndpoint("https");

var lyra3ConfigPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Config";

var lyra3Seed = builder.AddProject<Projects.Gen4_Local_Lyra3Seed>("lyra3-seed")
    .WithEnvironment("LYRA3_MONGO_CONNECTION", mongo.Resource.ConnectionStringExpression)
    .WaitFor(mongo);

var lyra3Auth = builder.AddExecutable("lyra3-auth", "dotnet",
        @"C:\Shared\Repos\Lyra3.HP.Auth.API\Src",
        @"C:\Shared\Repos\Lyra3.HP.Auth.API\Src\bin\Debug\net8.0\MVS.Lyra3.HP.Auth.API.dll")
    .WithEnvironment("MVS_LYRA3_AUTH_API_SRC", Path.Combine(lyra3ConfigPath, "Auth.API.yml"))
    .WithEndpoint(port: 5052, targetPort: 5052, name: "dmz", isProxied: false)
    .WithEndpoint(port: 3304, targetPort: 3304, name: "public", isProxied: false)
    .WaitForCompletion(lyra3Seed)
    .WaitFor(mongo)
    .WaitFor(redis)
    .WaitFor(rabbitmq);

var lyra3Core = builder.AddExecutable("lyra3-core", "dotnet",
        @"C:\Shared\Repos\Lyra3.HP.Core.API\Src",
        @"C:\Shared\Repos\Lyra3.HP.Core.API\Src\bin\Debug\net8.0\MVS.Lyra3.HP.Core.API.dll")
    .WithEnvironment("MVS_LYRA3_CORE_API_SRC", Path.Combine(lyra3ConfigPath, "Core.API.yml"))
    .WithEndpoint(port: 5602, targetPort: 5602, name: "dmz", isProxied: false)
    .WithEndpoint(port: 3302, targetPort: 3302, name: "public", isProxied: false)
    .WaitForCompletion(lyra3Seed)
    .WaitFor(mongo)
    .WaitFor(redis)
    .WaitFor(rabbitmq)
    .WaitFor(lyra3Auth)
    .WaitFor(s3Storage);

// Kestrel endpoints for the Lyra3 services come from ListenOptions in their *.API.yml and are bound
// with options.Listen(...), so ASPNETCORE_URLS and Aspire port injection have no effect: the ports
// here are declarative only and must match the yaml. Jumbo additionally blocks in
// ConfigureAppDependencies until {Auth,Core}AppUri + "api/health" answers, so it needs both waits.
var lyra3Jumbo = builder.AddExecutable("lyra3-jumbo", "dotnet",
        @"C:\Shared\Repos\Lyra3.HP.Jumbo.API\Src",
        @"C:\Shared\Repos\Lyra3.HP.Jumbo.API\Src\bin\Debug\net8.0\MVS.Lyra3.HP.Jumbo.API.dll")
    .WithEnvironment("MVS_LYRA3_JUMBO_API_SRC", Path.Combine(lyra3ConfigPath, "Jumbo.API.yml"))
    .WithEndpoint(port: 5042, targetPort: 5042, name: "dmz", isProxied: false)
    .WithEndpoint(port: 3308, targetPort: 3308, name: "public", isProxied: false)
    .WaitForCompletion(lyra3Seed)
    .WaitFor(mongo)
    .WaitFor(rabbitmq)
    .WaitFor(lyra3Auth)
    .WaitFor(lyra3Core);

var smokeTest = builder.AddProject<Projects.Gen4_Local_SmokeTest>("smoke-test")
    .WithEnvironment("STYX_URL", ReferenceExpression.Create($"https://{styxHttps.Property(EndpointProperty.Host)}:{styxHttps.Property(EndpointProperty.Port)}"))
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("HIS_DB_CONNECTION", "Host=localhost;Username=postgres;Password=postgres;Database=his-db")
    .WithEnvironment("CORE_DB_CONNECTION", "Host=localhost;Username=postgres;Password=postgres;Database=core-db")
    .WithEnvironment("LYRA3_CORE_URL", "http://localhost:3302")
    .WithEnvironment("LYRA3_MONGO_CONNECTION", mongo.Resource.ConnectionStringExpression)
    .WaitFor(styxApi)
    .WaitFor(lyra3Seed)
    .WaitFor(lyra3Core);

builder.Build().Run();

public static class EndpointReferenceExtensions
{
    public static ReferenceExpression HostPort(this EndpointReference endpoint)
    {
        return ReferenceExpression.Create(
            $"{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    }
}
