using Gen4.HP.HIS.Data;
using Gen4.HP.HIS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

var hisConn = "Host=localhost;Username=postgres;Password=postgres;Database=his-db";
var mongoConn = "mongodb://root:Passw0rd123@localhost:27018";
var mongoDbName = "Lyra3CoreDb";

await using var ds = NpgsqlDataSource.Create(hisConn);
await using var conn = await ds.OpenConnectionAsync();

// Existing patient for "already in Gen4" case
var existingPatientId = Guid.NewGuid();
await using (var cmd = new NpgsqlCommand("INSERT INTO patients (id, first_name, last_name, birth_date, full_name_search_string, created_at, updated_at) VALUES (@id, 'ExistP', 'ExistLast', '1990-01-01', 'existlast existp', now(), now()) ON CONFLICT DO NOTHING", conn))
{
    cmd.Parameters.AddWithValue("id", existingPatientId);
    await cmd.ExecuteNonQueryAsync();
}
// fetch id if conflict
var epId = await new NpgsqlCommand("SELECT id FROM patients WHERE first_name='ExistP' AND last_name='ExistLast' LIMIT 1", conn).ExecuteScalarAsync();
existingPatientId = (Guid)epId!;

// Existing doctor
var existingDoctorId = Guid.NewGuid();
await using (var cmd = new NpgsqlCommand("INSERT INTO employees (id, first_name, last_name, is_enabled, full_name_search_string, created_at, updated_at) VALUES (@id, 'ExistDoc', 'ExistDocLast', true, 'existdoclast existdoc', now(), now()) ON CONFLICT DO NOTHING", conn))
{
    cmd.Parameters.AddWithValue("id", existingDoctorId);
    await cmd.ExecuteNonQueryAsync();
}
var edId = await new NpgsqlCommand("SELECT id FROM employees WHERE first_name='ExistDoc' AND last_name='ExistDocLast' LIMIT 1", conn).ExecuteScalarAsync();
existingDoctorId = (Guid)edId!;

Console.WriteLine("HIS seed done. PatientId=" + existingPatientId + " DoctorId=" + existingDoctorId);

// Mongo seed
var client = new MongoClient(mongoConn);
var db = client.GetDatabase(mongoDbName);
var pColl = db.GetCollection<BsonDocument>("patients");
await pColl.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
var dColl = db.GetCollection<BsonDocument>("doctors");
await dColl.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);

// Patients cases
var pDocs = new List<BsonDocument>
{
    // no first name
    new BsonDocument { { "lastName", "X" }, { "dateOfBirth", new DateTime(1990,1,1,0,0,0,DateTimeKind.Utc) } },
    // no last name
    new BsonDocument { { "firstName", "X" }, { "dateOfBirth", new DateTime(1990,1,1,0,0,0,DateTimeKind.Utc) } },
    // name too long (129 chars)
    new BsonDocument { { "firstName", new string('A',129) }, { "lastName", "X" }, { "dateOfBirth", new DateTime(1990,1,1,0,0,0,DateTimeKind.Utc) } },
    // no birth date
    new BsonDocument { { "firstName", "NoBirth" }, { "lastName", "X" } },
    // birth date outside range
    new BsonDocument { { "firstName", "Old" }, { "lastName", "X" }, { "dateOfBirth", new DateTime(1800,1,1,0,0,0,DateTimeKind.Utc) } },
    // duplicate
    // already in Gen4
    new BsonDocument { { "firstName", "ExistP" }, { "lastName", "ExistLast" }, { "dateOfBirth", new DateTime(1990,1,1,0,0,0,DateTimeKind.Utc) } },
    // created
    new BsonDocument { { "firstName", "NewPatient" }, { "lastName", "NewLast" }, { "dateOfBirth", new DateTime(1992,3,3,0,0,0,DateTimeKind.Utc) } },
    // ResolveId reassign (id holder exists with different data)
    new BsonDocument { { "_id", existingPatientId.ToString() }, { "firstName", "Different" }, { "lastName", "Person" }, { "dateOfBirth", new DateTime(1993,4,4,0,0,0,DateTimeKind.Utc) } },
};
await pColl.InsertManyAsync(pDocs);

// Doctors cases
var binaryAvatar = new byte[] { 1,2,3 };
var dDocs = new List<BsonDocument>
{
    // no first name
    new BsonDocument { { "lastName", "X" } },
    // no last name
    new BsonDocument { { "firstName", "X" } },
    // name too long
    new BsonDocument { { "firstName", new string('A',129) }, { "lastName", "X" } },
    // avatar not binary (string)
    new BsonDocument { { "firstName", "AvatarBad" }, { "lastName", "X" }, { "avatar", "notbinary" } },
    // duplicate
    // already in Gen4
    new BsonDocument { { "firstName", "ExistDoc" }, { "lastName", "ExistDocLast" } },
    // created
    new BsonDocument { { "firstName", "NewDoc" }, { "lastName", "NewDocLast" } },
    // avatar binary
    new BsonDocument { { "firstName", "AvatarBin" }, { "lastName", "X" }, { "avatar", new BsonBinaryData(binaryAvatar) } },
};
await dColl.InsertManyAsync(dDocs);

Console.WriteLine("Mongo seed done.");


