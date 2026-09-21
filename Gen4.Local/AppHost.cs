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

var lyra3ConfigPath = @"C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Config";

var lyra3Seed = builder.AddProject<Projects.Gen4_Local_Lyra3Seed>("lyra3-seed")
    .WithEnvironment("LYRA3_MONGO_CONNECTION", mongo.Resource.ConnectionStringExpression)
    .WaitFor(mongo);

var lyra3Auth = builder.AddExecutable("lyra3-auth", "dotnet",
        @"C:\Shared\Repos\Lyra3.HP.Auth.API\Src",
        @"C:\Shared\Repos\Lyra3.HP.Auth.API\Src\bin\Debug\net8.0\MVS.Lyra3.HP.Auth.API.dll")
    .WithEnvironment("MVS_LYRA3_AUTH_API_SRC", Path.Combine(lyra3ConfigPath, "Auth.API.yml"))
    .WithEndpoint(port: 5052, targetPort: 5052, name: "dmz", isProxied: false)
    .WithEndpoint(port: 5051, targetPort: 5051, name: "public", isProxied: false)
    .WaitForCompletion(lyra3Seed)
    .WaitFor(mongo)
    .WaitFor(redis)
    .WaitFor(rabbitmq);

var lyra3Core = builder.AddExecutable("lyra3-core", "dotnet",
        @"C:\Shared\Repos\Lyra3.HP.Core.API\Src",
        @"C:\Shared\Repos\Lyra3.HP.Core.API\Src\bin\Debug\net8.0\MVS.Lyra3.HP.Core.API.dll")
    .WithEnvironment("MVS_LYRA3_CORE_API_SRC", Path.Combine(lyra3ConfigPath, "Core.API.yml"))
    .WithEndpoint(port: 5602, targetPort: 5602, name: "dmz", isProxied: false)
    .WithEndpoint(port: 5601, targetPort: 5601, name: "public", isProxied: false)
    .WaitForCompletion(lyra3Seed)
    .WaitFor(mongo)
    .WaitFor(redis)
    .WaitFor(rabbitmq)
    .WaitFor(lyra3Auth)
    .WaitFor(s3Storage);

builder.Build().Run();

public static class EndpointReferenceExtensions
{
    public static ReferenceExpression HostPort(this EndpointReference endpoint)
    {
        return ReferenceExpression.Create(
            $"{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");
    }
}
