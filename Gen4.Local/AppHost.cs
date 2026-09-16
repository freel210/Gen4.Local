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

var initJob = builder.AddProject<Projects.Gen4_Local_Init>("init-job")
    .WithEnvironment("GEN4HP_CONFIGROOT", configPath)
    .WithEnvironment("GEN4HP_CERTSROOT", certsPath)
    .WaitFor(postgres)
    .WaitFor(nats);

var hisApi = builder.AddProject<Projects.Gen4_HP_HIS_API>("his-api")
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("HIS_CORE_ENDPOINT", "core-api:5100")
    .WithEndpoint(port: 5200, scheme: "https")
    .WaitForCompletion(initJob);

var coreApi = builder.AddProject<Projects.Gen4_HP_Core_API>("core-api")
    .WithEnvironment("CONFIG_ROOT", configPath)
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("CORE_TEMP_DIR", tempPath)
    .WithEnvironment("HIS_ENDPOINT", "his-api:5200")
    .WithEnvironment("STYX_ENDPOINT", "styx-api:5001")
    .WithEndpoint(port: 5100, scheme: "https")
    .WaitForCompletion(initJob);

var styxApi = builder.AddProject<Projects.Gen4_HP_Styx_API>("styx-api")
    .WithEnvironment("CERTS_ROOT", certsPath)
    .WithEnvironment("STYX_HTTPS_PORT", "5050")
    .WithEnvironment("CORE_ENDPOINT", "core-api:5100")
    .WithEnvironment("HIS_ENDPOINT", "his-api:5200")
    .WithEndpoint(port: 5050, scheme: "https", name: "https")
    .WithEndpoint(port: 5001, scheme: "https", name: "grpc")
    .WaitForCompletion(initJob);

builder.Build().Run();
