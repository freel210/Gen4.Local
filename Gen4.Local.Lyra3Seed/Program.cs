using BCrypt.Net;
using MongoDB.Bson;
using MongoDB.Driver;

var connectionString = Environment.GetEnvironmentVariable("LYRA3_MONGO_CONNECTION")
    ?? throw new InvalidOperationException("LYRA3_MONGO_CONNECTION is not set.");

var client = new MongoClient(connectionString);
var db = client.GetDatabase("Lyra3CoreDb");

var existingCollections = (await db.ListCollectionNamesAsync()).ToList();

var collections = new[]
{
    new { Name = "accounts", Indexes = new[] { new { Name = "IX_Unique_Login", Keys = BsonDocument.Parse("{ login: 1 }"), Unique = true } } },
    new { Name = "configurations", Indexes = new[] { new { Name = "IX_Unique_Key", Keys = BsonDocument.Parse("{ key: 1 }"), Unique = true } } },
    new { Name = "equipments", Indexes = new[] { new { Name = "hpKey_1", Keys = BsonDocument.Parse("{ hpKey: 1 }"), Unique = true }, new { Name = "IX_Name", Keys = BsonDocument.Parse("{ name: 1 }"), Unique = false } } },
    new { Name = "components", Indexes = new[] { new { Name = "IX_Unique_Name", Keys = BsonDocument.Parse("{ name: 1 }"), Unique = true }, new { Name = "IX_Unique_InstanceId", Keys = BsonDocument.Parse("{ instanceId: 1 }"), Unique = true }, new { Name = "IX_Unique_Url", Keys = BsonDocument.Parse("{ url: 1 }"), Unique = true } } },
    new { Name = "finishedSurgeries", Indexes = new[] { new { Name = "IX_StartedAt", Keys = BsonDocument.Parse("{ startedAt: 1 }"), Unique = false }, new { Name = "IX_FinishedAt", Keys = BsonDocument.Parse("{ finishedAt: 1 }"), Unique = false }, new { Name = "IX_Unique_EquipmentId_EquipmentSurgeryId", Keys = BsonDocument.Parse("{ equipmentId: 1, equipmentSurgeryId: 1 }"), Unique = true } } },
    new { Name = "finishedSurgeryStates", Indexes = new[] { new { Name = "IX_FinishedSurgeryId", Keys = BsonDocument.Parse("{ finishedSurgeryId: 1 }"), Unique = false }, new { Name = "IX_RegisteredAt", Keys = BsonDocument.Parse("{ registeredAt: 1 }"), Unique = false } } },
    new { Name = "certificates", Indexes = new[] { new { Name = "IX_Unique_SerialNumber", Keys = BsonDocument.Parse("{ serialNumber: 1 }"), Unique = true } } },
    new { Name = "comments", Indexes = new[] { new { Name = "IX_FinishedSurgeryId_CreatedAt", Keys = BsonDocument.Parse("{ finishedSurgeryId: 1, createdAt: 1 }"), Unique = false } } },
    new { Name = "cleanupTasks", Indexes = new[] { new { Name = "IX_Unique_EntityType_EntityId", Keys = BsonDocument.Parse("{ entityType: 1, entityId: 1 }"), Unique = true } } },
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
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = indexDef.Name,
            Unique = indexDef.Unique,
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