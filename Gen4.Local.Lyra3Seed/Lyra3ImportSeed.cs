using MongoDB.Bson;
using MongoDB.Driver;

internal static class Lyra3ImportSeed
{
    private const int Gen4ExternalHisIdLength = 36;
    private const int Gen4NameLength = 128;
    private const int Gen4CommentLength = 256;

    private const string ExternalHisType = "Lyra3";

    private const string ValidPatientId = "a1000000-0000-4000-8000-000000000001";
    private const string NoPatronymicPatientId = "a1000000-0000-4000-8000-000000000002";
    private const string OldBirthDatePatientId = "a1000000-0000-4000-8000-000000000003";
    private const string NoBirthDatePatientId = "a1000000-0000-4000-8000-000000000004";
    private const string LongNamePatientId = "a1000000-0000-4000-8000-000000000005";
    private const string UnimportablePatientId = "a1000000-0000-4000-8000-000000000006";

    private const string ValidDoctorId = "b1000000-0000-4000-8000-000000000001";
    private const string NoPatronymicDoctorId = "b1000000-0000-4000-8000-000000000002";
    private const string NonBinaryAvatarDoctorId = "b1000000-0000-4000-8000-000000000003";
    private const string UnimportableDoctorId = "b1000000-0000-4000-8000-000000000004";

    private const string ValidOperatingRoomId = "c1000000-0000-4000-8000-000000000001";
    private const string ShortNameOperatingRoomId = "c1000000-0000-4000-8000-000000000002";
    private const string UnimportableOperatingRoomId = "c1000000-0000-4000-8000-000000000003";
    private const string ExternalHisOperatingRoomId = "c1000000-0000-4000-8000-000000000004";

    private const string ExternalHisOperatingRoomName = "Операционная №3";
    private const string ExternalHisOperatingRoomExternalHisId = "room-3-external";

    private const string ValidSurgeryId = "d1000000-0000-4000-8000-000000000001";
    private const string ContradictingDatesSurgeryId = "d1000000-0000-4000-8000-000000000002";
    private const string NoPatientSurgeryId = "d1000000-0000-4000-8000-000000000003";
    private const string BrokenTeamSurgeryId = "d1000000-0000-4000-8000-000000000004";
    private const string UnimportableSurgeryId = "d1000000-0000-4000-8000-000000000005";
    private const string ZeroOperatingRoomIdSurgeryId = "d1000000-0000-4000-8000-000000000006";
    private const string ExternalHisOperatingRoomSurgeryId = "d1000000-0000-4000-8000-000000000007";
    private const string DroppedOperatingRoomSurgeryId = "d1000000-0000-4000-8000-000000000008";
    private const string NoDoctorsSurgeryId = "d1000000-0000-4000-8000-000000000009";
    private const string UnknownDoctorsSurgeryId = "d1000000-0000-4000-8000-000000000010";
    private const string DoctorIdOnlySurgeryId = "d1000000-0000-4000-8000-000000000011";
    private const string DeletedSurgeryId = "d1000000-0000-4000-8000-000000000012";
    private const string PartialTeamSurgeryId = "d1000000-0000-4000-8000-000000000013";

    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    private static readonly string LongExternalHisId = new('7', Gen4ExternalHisIdLength + 1);
    private static readonly string LongName = new('Я', Gen4NameLength + 1);
    private static readonly string LongComment = new('к', Gen4CommentLength + 1);

    public static async Task Seed(IMongoDatabase database)
    {
        var count = 0;

        foreach (var (collectionName, documents) in Collections())
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);

            foreach (var document in documents)
            {
                await collection.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
                    document,
                    new ReplaceOptions { IsUpsert = true });
                count++;
            }
        }

        Console.WriteLine($"Lyra3 import seed: {count} documents upserted.");
    }

    private static (string CollectionName, BsonDocument[] Documents)[] Collections()
    {
        return
        [
            ("patients", Patients()),
            ("doctors", Doctors()),
            ("operatingRooms", OperatingRooms()),
            ("plannedSurgeries", PlannedSurgeries()),
        ];
    }

    private static BsonDocument[] Patients()
    {
        return
        [
            new BsonDocument
            {
                { "_id", ValidPatientId },
                { "id", ValidPatientId },
                { "firstName", "Иван" },
                { "lastName", "Петров" },
                { "patronymic", "Сергеевич" },
                { "dateOfBirth", Date(1980, 1, 2) },
                { "comment", "пациент с отчеством и полной датой рождения" },
                { "idHospitalization", "A-1001" },
                { "isImported", false },
            },
            new BsonDocument
            {
                { "_id", NoPatronymicPatientId },
                { "id", NoPatronymicPatientId },
                { "firstName", "Пётр" },
                { "lastName", "Сидоров" },
                { "dateOfBirth", Date(1990, 3, 4) },
                { "comment", LongComment },
            },
            new BsonDocument
            {
                { "_id", OldBirthDatePatientId },
                { "id", OldBirthDatePatientId },
                { "firstName", "Анна" },
                { "lastName", "Кузнецова" },
                { "patronymic", "Ивановна" },
                { "dateOfBirth", Date(1890, 5, 5) },
            },
            new BsonDocument
            {
                { "_id", NoBirthDatePatientId },
                { "id", NoBirthDatePatientId },
                { "firstName", "Ольга" },
                { "lastName", "Смирнова" },
                { "patronymic", "Алексеевна" },
            },
            new BsonDocument
            {
                { "_id", LongNamePatientId },
                { "id", LongNamePatientId },
                { "firstName", LongName },
                { "lastName", "Длинная" },
                { "dateOfBirth", Date(1975, 6, 7) },
            },
            new BsonDocument
            {
                { "_id", UnimportablePatientId },
                { "id", UnimportablePatientId },
                { "firstName", "Борис" },
                { "lastName", "Ядовитов" },
                { "patronymic", "Борисович" },
                { "dateOfBirth", Date(1970, 1, 1) },
                { "externalHisId", LongExternalHisId },
                { "externalHisType", ExternalHisType },
            },
        ];
    }

    private static BsonDocument[] Doctors()
    {
        return
        [
            new BsonDocument
            {
                { "_id", ValidDoctorId },
                { "id", ValidDoctorId },
                { "firstName", "Сергей" },
                { "lastName", "Хирургов" },
                { "patronymic", "Петрович" },
                { "comment", "хирург с аватаром" },
                { "isEnabled", true },
                { "avatar", new BsonBinaryData([1, 2, 3, 4]) },
            },
            new BsonDocument
            {
                { "_id", NoPatronymicDoctorId },
                { "id", NoPatronymicDoctorId },
                { "firstName", "Мария" },
                { "lastName", "Анестезиолова" },
                { "isEnabled", true },
            },
            new BsonDocument
            {
                { "_id", NonBinaryAvatarDoctorId },
                { "id", NonBinaryAvatarDoctorId },
                { "firstName", "Аватар" },
                { "lastName", "Небинарный" },
                { "patronymic", "Тестович" },
                { "avatar", "avatar is a string in Lyra3" },
            },
            new BsonDocument
            {
                { "_id", UnimportableDoctorId },
                { "id", UnimportableDoctorId },
                { "firstName", "Яд" },
                { "lastName", "Докторов" },
                { "patronymic", "Ядович" },
                { "externalHisId", LongExternalHisId },
                { "externalHisType", ExternalHisType },
            },
        ];
    }

    private static BsonDocument[] OperatingRooms()
    {
        return
        [
            new BsonDocument
            {
                { "_id", ValidOperatingRoomId },
                { "id", ValidOperatingRoomId },
                { "name", "Операционная №1" },
                { "comment", "операционная с корректным именем" },
            },
            new BsonDocument
            {
                { "_id", ShortNameOperatingRoomId },
                { "id", ShortNameOperatingRoomId },
                { "name", "A" },
            },
            new BsonDocument
            {
                { "_id", UnimportableOperatingRoomId },
                { "id", UnimportableOperatingRoomId },
                { "name", "Операционная №2" },
                { "externalHisId", LongExternalHisId },
                { "externalHisType", ExternalHisType },
            },
            new BsonDocument
            {
                { "_id", ExternalHisOperatingRoomId },
                { "id", ExternalHisOperatingRoomId },
                { "name", ExternalHisOperatingRoomName },
                { "externalHisId", ExternalHisOperatingRoomExternalHisId },
                { "externalHisType", ExternalHisType },
            },
        ];
    }

    private static BsonDocument[] PlannedSurgeries()
    {
        return
        [
            new BsonDocument
            {
                { "_id", ValidSurgeryId },
                { "id", ValidSurgeryId },
                { "startTime", Date(2026, 11, 10, 8) },
                { "finishTime", Date(2026, 11, 10, 10) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                {
                    "doctors", new BsonArray
                    {
                        DoctorSnapshot("Сергей", "Хирургов", "Петрович"),
                        DoctorSnapshot("Мария", "Анестезиолова", null),
                    }
                },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
                { "diagnosis", "Острый аппендицит" },
                { "comment", "плановая операция с корректными ссылками" },
                { "isDeleted", false },
            },
            new BsonDocument
            {
                { "_id", ContradictingDatesSurgeryId },
                { "id", ContradictingDatesSurgeryId },
                { "startTime", Date(2026, 11, 11, 10) },
                { "finishTime", Date(2026, 11, 11, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
            },
            new BsonDocument
            {
                { "_id", NoPatientSurgeryId },
                { "id", NoPatientSurgeryId },
                { "startTime", Date(2026, 11, 12, 8) },
                { "finishTime", Date(2026, 11, 12, 9) },
            },
            new BsonDocument
            {
                { "_id", BrokenTeamSurgeryId },
                { "id", BrokenTeamSurgeryId },
                { "startTime", Date(2026, 11, 13, 8) },
                { "finishTime", Date(2026, 11, 13, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { "surgeon is a string in Lyra3" } },
            },
            new BsonDocument
            {
                { "_id", UnimportableSurgeryId },
                { "id", UnimportableSurgeryId },
                { "startTime", Date(2026, 11, 14, 8) },
                { "finishTime", Date(2026, 11, 14, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "externalHisId", LongExternalHisId },
                { "externalHisType", ExternalHisType },
            },
            new BsonDocument
            {
                { "_id", ZeroOperatingRoomIdSurgeryId },
                { "id", ZeroOperatingRoomIdSurgeryId },
                { "startTime", Date(2026, 11, 15, 8) },
                { "finishTime", Date(2026, 11, 15, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Сергей", "Хирургов", "Петрович") } },
                {
                    "operatingRoom", new BsonDocument
                    {
                        { "id", ZeroGuid },
                        { "name", "Операционная №1" },
                    }
                },
            },
            new BsonDocument
            {
                { "_id", ExternalHisOperatingRoomSurgeryId },
                { "id", ExternalHisOperatingRoomSurgeryId },
                { "startTime", Date(2026, 11, 16, 8) },
                { "finishTime", Date(2026, 11, 16, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Мария", "Анестезиолова", null) } },
                {
                    "operatingRoom", new BsonDocument
                    {
                        { "name", "Операционная переименована в Lyra3" },
                        { "externalHisId", ExternalHisOperatingRoomExternalHisId.ToUpperInvariant() },
                        { "externalHisType", ExternalHisType.ToUpperInvariant() },
                    }
                },
            },
            new BsonDocument
            {
                { "_id", DroppedOperatingRoomSurgeryId },
                { "id", DroppedOperatingRoomSurgeryId },
                { "startTime", Date(2026, 11, 17, 8) },
                { "finishTime", Date(2026, 11, 17, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Сергей", "Хирургов", "Петрович") } },
                { "operatingRoom", new BsonDocument { { "name", "A" } } },
            },
            new BsonDocument
            {
                { "_id", NoDoctorsSurgeryId },
                { "id", NoDoctorsSurgeryId },
                { "startTime", Date(2026, 11, 18, 8) },
                { "finishTime", Date(2026, 11, 18, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray() },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
            },
            new BsonDocument
            {
                { "_id", UnknownDoctorsSurgeryId },
                { "id", UnknownDoctorsSurgeryId },
                { "startTime", Date(2026, 11, 19, 8) },
                { "finishTime", Date(2026, 11, 19, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Неизвестный", "Врач", "Тестович") } },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
            },
            new BsonDocument
            {
                { "_id", DoctorIdOnlySurgeryId },
                { "id", DoctorIdOnlySurgeryId },
                { "startTime", Date(2026, 11, 20, 8) },
                { "finishTime", Date(2026, 11, 20, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Несовпадающее", "Имя", null, ValidDoctorId) } },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
            },
            new BsonDocument
            {
                { "_id", DeletedSurgeryId },
                { "id", DeletedSurgeryId },
                { "startTime", Date(2026, 11, 21, 8) },
                { "finishTime", Date(2026, 11, 21, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                { "doctors", new BsonArray { DoctorSnapshot("Сергей", "Хирургов", "Петрович") } },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
                { "isDeleted", true },
            },
            new BsonDocument
            {
                { "_id", PartialTeamSurgeryId },
                { "id", PartialTeamSurgeryId },
                { "startTime", Date(2026, 11, 22, 8) },
                { "finishTime", Date(2026, 11, 22, 9) },
                { "patient", PatientSnapshot("Иван", "Петров", "Сергеевич", Date(1980, 1, 2)) },
                {
                    "doctors", new BsonArray
                    {
                        DoctorSnapshot("Сергей", "Хирургов", "Петрович"),
                        DoctorSnapshot("Неизвестный", "Врач", "Тестович"),
                    }
                },
                { "operatingRoom", new BsonDocument { { "id", ValidOperatingRoomId } } },
            },
        ];
    }

    private static BsonDocument PatientSnapshot(string firstName, string lastName, string? patronymic, BsonDateTime birthDate)
    {
        var patient = new BsonDocument
        {
            { "firstName", firstName },
            { "lastName", lastName },
            { "dateOfBirth", birthDate },
        };

        if (patronymic is not null)
        {
            patient.Add("patronymic", patronymic);
        }

        return patient;
    }

    private static BsonDocument DoctorSnapshot(string firstName, string lastName, string? patronymic, string? lyra3Id = null)
    {
        var doctor = new BsonDocument();

        if (lyra3Id is not null)
        {
            doctor.Add("_id", lyra3Id);
        }

        doctor.Add("firstName", firstName);
        doctor.Add("lastName", lastName);

        if (patronymic is not null)
        {
            doctor.Add("patronymic", patronymic);
        }

        return doctor;
    }

    private static BsonDateTime Date(int year, int month, int day, int hour = 0)
    {
        return new BsonDateTime(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc));
    }
}
