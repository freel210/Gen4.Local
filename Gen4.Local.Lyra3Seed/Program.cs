using BCrypt.Net;
using MongoDB.Bson;
using MongoDB.Driver;

var connectionString = Environment.GetEnvironmentVariable("LYRA3_MONGO_CONNECTION")
    ?? throw new InvalidOperationException("LYRA3_MONGO_CONNECTION is not set.");

var client = new MongoClient(connectionString);
var db = client.GetDatabase("Lyra3CoreDb");

var existingCollections = (await db.ListCollectionNamesAsync()).ToList();

// MVS.Lyra3.HP.Jumbo.API validates that these collections exist and that the index names below are
// present, by exact name, before it will start (Src\Services\MongoDbService.cs). A partial filter
// expression is required on the unique "external HIS id" indexes: without it Mongo treats every
// document that omits the field as a duplicate null and rejects the second insert.
var externalHisFilter = BsonDocument.Parse("{ externalHisId: { $exists: true, $type: 2 } }");
var doctorIdFilter = BsonDocument.Parse("{ doctorId: { $exists: true, $type: 2 } }");

var collections = new[]
{
    new CollectionDef("accounts",
    [
        new IndexDef("IX_Unique_Login", BsonDocument.Parse("{ login: 1 }"), Unique: true),
        new IndexDef("IX_UniqueDoctorId", BsonDocument.Parse("{ doctorId: 1 }"), Unique: true, PartialFilter: doctorIdFilter),
    ]),
    new CollectionDef("configurations",
    [
        new IndexDef("IX_Unique_Key", BsonDocument.Parse("{ key: 1 }"), Unique: true),
    ]),
    new CollectionDef("equipments",
    [
        new IndexDef("hpKey_1", BsonDocument.Parse("{ hpKey: 1 }"), Unique: true),
        new IndexDef("IX_Name", BsonDocument.Parse("{ name: 1 }"), Unique: false),
    ]),
    new CollectionDef("components",
    [
        new IndexDef("IX_Unique_Name", BsonDocument.Parse("{ name: 1 }"), Unique: true),
        new IndexDef("IX_Unique_InstanceId", BsonDocument.Parse("{ instanceId: 1 }"), Unique: true),
        new IndexDef("IX_Unique_Url", BsonDocument.Parse("{ url: 1 }"), Unique: true),
    ]),
    new CollectionDef("doctors",
    [
        new IndexDef("IX_AvatarId", BsonDocument.Parse("{ avatarId: 1 }"), Unique: false),
        new IndexDef("IX_SearchString", BsonDocument.Parse("{ fullNameSearchString: 1 }"), Unique: false),
        new IndexDef("IX_Comment", BsonDocument.Parse("{ comment: 1 }"), Unique: false),
        new IndexDef("IX_Unique_ExternalHis", BsonDocument.Parse("{ externalHisId: 1, externalHisType: 1 }"), Unique: true, PartialFilter: externalHisFilter),
        new IndexDef("IX_Unique_Doctor", BsonDocument.Parse("{ firstName: 1, lastName: 1, patronymic: 1 }"), Unique: true),
    ]),
    new CollectionDef("patients",
    [
        new IndexDef("IX_AvatarId", BsonDocument.Parse("{ avatarId: 1 }"), Unique: false),
        new IndexDef("IX_SearchString", BsonDocument.Parse("{ fullNameSearchString: 1 }"), Unique: false),
        new IndexDef("IX_Comment", BsonDocument.Parse("{ comment: 1 }"), Unique: false),
        new IndexDef("IX_Unique_ExternalHis", BsonDocument.Parse("{ externalHisId: 1, externalHisType: 1 }"), Unique: true, PartialFilter: externalHisFilter),
        new IndexDef("IX_Unique_Patient", BsonDocument.Parse("{ firstName: 1, lastName: 1, patronymic: 1, dateOfBirth: 1 }"), Unique: true),
    ]),
    new CollectionDef("plannedSurgeries",
    [
        new IndexDef("IX_Unique_ExternalHis", BsonDocument.Parse("{ externalHisId: 1, externalHisType: 1 }"), Unique: true, PartialFilter: externalHisFilter),
    ]),
    new CollectionDef("operatingRooms",
    [
        new IndexDef("IX_Unique_ExternalHis", BsonDocument.Parse("{ externalHisId: 1, externalHisType: 1 }"), Unique: true, PartialFilter: externalHisFilter),
    ]),
    new CollectionDef("finishedSurgeries",
    [
        new IndexDef("IX_StartedAt", BsonDocument.Parse("{ startedAt: 1 }"), Unique: false),
        new IndexDef("IX_FinishedAt", BsonDocument.Parse("{ finishedAt: 1 }"), Unique: false),
        new IndexDef("IX_Unique_EquipmentId_EquipmentSurgeryId", BsonDocument.Parse("{ equipmentId: 1, equipmentSurgeryId: 1 }"), Unique: true),
    ]),
    new CollectionDef("finishedSurgeryStates",
    [
        new IndexDef("IX_FinishedSurgeryId", BsonDocument.Parse("{ finishedSurgeryId: 1 }"), Unique: false),
        new IndexDef("IX_RegisteredAt", BsonDocument.Parse("{ registeredAt: 1 }"), Unique: false),
    ]),
    new CollectionDef("certificates",
    [
        new IndexDef("IX_Unique_SerialNumber", BsonDocument.Parse("{ serialNumber: 1 }"), Unique: true),
    ]),
    new CollectionDef("comments",
    [
        new IndexDef("IX_FinishedSurgeryId_CreatedAt", BsonDocument.Parse("{ finishedSurgeryId: 1, createdAt: 1 }"), Unique: false),
    ]),
    new CollectionDef("cleanupTasks",
    [
        new IndexDef("IX_Unique_EntityType_EntityId", BsonDocument.Parse("{ entityType: 1, entityId: 1 }"), Unique: true),
    ]),
};

foreach (var collectionDef in collections)
{
    if (!existingCollections.Contains(collectionDef.Name))
    {
        await db.CreateCollectionAsync(collectionDef.Name);
    }

    var collection = db.GetCollection<BsonDocument>(collectionDef.Name);
    var existingIndexNames = (await collection.Indexes.ListAsync()).ToList()
        .Select(i => i["name"].AsString)
        .ToHashSet();

    foreach (var indexDef in collectionDef.Indexes)
    {
        if (existingIndexNames.Contains(indexDef.Name))
        {
            continue;
        }

        var keys = new BsonDocumentIndexKeysDefinition<BsonDocument>(indexDef.Keys);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions<BsonDocument>
        {
            Name = indexDef.Name,
            Unique = indexDef.Unique,
            PartialFilterExpression = indexDef.PartialFilter,
        }));
    }
}

var accounts = db.GetCollection<BsonDocument>("accounts");
var adminFilter = Builders<BsonDocument>.Filter.And(
    Builders<BsonDocument>.Filter.Eq("type", "Administrator"),
    Builders<BsonDocument>.Filter.Eq("login", "admin"));

var existingAdmin = await accounts.Find(adminFilter).FirstOrDefaultAsync();
if (existingAdmin is null)
{
    var adminId = Guid.NewGuid().ToString();
    var admin = new BsonDocument
    {
        { "_id", adminId },
        { "type", "Administrator" },
        { "login", "admin" },
        { "passwordHash", BCrypt.Net.BCrypt.HashPassword("Passw0rd123") },
        { "isEnabled", true },
    };
    await accounts.InsertOneAsync(admin);
    Console.WriteLine($"Created administrator with id '{adminId}'.");
}
else
{
    Console.WriteLine($"Administrator already exists with id '{existingAdmin["_id"]}'.");
}

var configurations = db.GetCollection<BsonDocument>("configurations");
var now = DateTime.UtcNow;

var storageConfigurations = new Dictionary<string, string>
{
    { "/MVS/Configuration/Storage/S3/ServiceUri", "http://localhost:4566" },
    { "/MVS/Configuration/Storage/S3/BucketName", "lyra3" },
    { "/MVS/Configuration/Storage/S3/AccessKey", "lyra3" },
    { "/MVS/Configuration/Storage/S3/SecretKey", "Passw0rd123" },
    { "/MVS/Configuration/Storage/S3Host/HostType", "Minio" },
    { "/MVS/Configuration/Storage/S3Host/ConsoleUri", "http://localhost:4566" },
    { "/MVS/Configuration/Storage/S3Host/ConsoleLogin", "lyra3" },
    { "/MVS/Configuration/Storage/S3Host/ConsolePassword", "Passw0rd123" },
    { "/MVS/Configuration/Storage/S3Thresholds/Warning", "80" },
    { "/MVS/Configuration/Storage/S3Thresholds/Critical", "90" },
};

foreach (var (key, value) in storageConfigurations)
{
    var filter = Builders<BsonDocument>.Filter.Eq("key", key);
    var update = Builders<BsonDocument>.Update
        .Set("value", value)
        .Set("updateDate", now)
        .SetOnInsert("_id", Guid.NewGuid().ToString())
        .SetOnInsert("createDate", now);
    var result = await configurations.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    Console.WriteLine(result.ModifiedCount > 0 || result.UpsertedId is not null
        ? $"Storage configuration '{key}' = '{value}'."
        : $"Storage configuration '{key}' already up to date.");
}

Console.WriteLine("Lyra3 seed completed.");

// Auth.API has no self-registration endpoint: POST /api/tokens/components/byInstanceId is a plain
// lookup of "components" by instanceId and answers 401 "Invalid instance id" on a miss. A component
// with no document therefore never obtains a token - Core retries every 3s forever and Jumbo would
// silently never connect to the Core hub. instanceId must equal HospitalPlatformOptions:AppInstanceId
// from the corresponding *.API.yml byte for byte.
var components = db.GetCollection<BsonDocument>("components");

var componentSeeds = new[]
{
    new ComponentDef("Lyra3.HP.Auth", "Auth.API", "Lyra3.HP.Auth.API", "http://127.0.0.1:5052"),
    new ComponentDef("Lyra3.HP.Core", "Core.API", "Lyra3.HP.Core.API", "http://127.0.0.1:5602"),
    new ComponentDef("Lyra3.HP.Jumbo", "Jumbo.API", "Lyra3.HP.Jumbo.API", "http://127.0.0.1:5042"),
};

foreach (var component in componentSeeds)
{
    var filter = Builders<BsonDocument>.Filter.Eq("instanceId", component.InstanceId);
    var update = Builders<BsonDocument>.Update
        .Set("name", component.Name)
        .Set("service", component.Service)
        .Set("url", component.Url)
        .Set("ip", "127.0.0.1")
        .Set("version", string.Empty)
        .Set("comment", string.Empty)
        .Set("updateDate", now)
        .SetOnInsert("_id", Guid.NewGuid().ToString())
        .SetOnInsert("id", Guid.NewGuid().ToString())
        .SetOnInsert("createDate", now);
    var result = await components.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    Console.WriteLine(result.ModifiedCount > 0 || result.UpsertedId is not null
        ? $"Component '{component.InstanceId}' seeded at '{component.Url}'."
        : $"Component '{component.InstanceId}' already up to date.");
}

Console.WriteLine("Lyra3 seed completed.");

internal sealed record CollectionDef(string Name, IndexDef[] Indexes);

internal sealed record IndexDef(string Name, BsonDocument Keys, bool Unique, BsonDocument? PartialFilter = null);

internal sealed record ComponentDef(string InstanceId, string Name, string Service, string Url);