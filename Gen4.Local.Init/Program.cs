using Gen4.HP.Master.CLI;
using Gen4.HP.Master.CLI.Commands;
using Gen4.Local.Init;
using Microsoft.EntityFrameworkCore;

if (string.IsNullOrEmpty(Database.GetConnectionString(Defines.CoreDatabaseConfigPath)))
{
    return 1;
}

MigrateCoreDatabase();

await using var context = Database.CreateDbContext(Defines.CoreDatabaseConfigPath);

if (NeedsEntry(context, Defines.Nats.EndpointKeyName))
{
    if (await SetNatsEndpointCommand.Run([LocalDefines.Nats.Endpoint]) != 0)
    {
        return 1;
    }
}

if (NeedsEntry(context, Defines.RsaKeys.PrivateKeyConfigurationName)
    || NeedsEntry(context, Defines.RsaKeys.PublicKeyConfigurationName))
{
    context.PutConfigurationEntry(
        Defines.RsaKeys.PrivateKeyConfigurationName,
        File.ReadAllText(LocalDefines.RsaKeys.PrivateKeyPath));
    context.PutConfigurationEntry(
        Defines.RsaKeys.PublicKeyConfigurationName,
        File.ReadAllText(LocalDefines.RsaKeys.PublicKeyPath));
}

if (NeedsEntry(context, Defines.MasterPassword.PasswordConfigurationKey))
{
    if (GenerateMasterPasswordCommand.Run() != 0)
    {
        return 1;
    }
}

ConfigureLyra3Options(context);
ConfigureJeconbol3Options(context);

if (NeedsHisMigrations())
{
    if (MigrateDatabaseCommand.Run(Defines.DatabaseType.His) != 0)
    {
        return 1;
    }
}

ConfigureJeconbol3OperatingRoom();

return 0;

static void MigrateCoreDatabase()
{
    var connectionString = Database.GetConnectionString(Defines.CoreDatabaseConfigPath)
        ?? throw new InvalidOperationException("Failed to get core database connection string.");

    using var dataSource = Gen4.HP.Core.Data.OptionsBuilderExtensions.BuildNpgsqlDataSource(connectionString);
    var optionsBuilder = new DbContextOptionsBuilder<Gen4.HP.Core.Data.Context>();
    Gen4.HP.Core.Data.OptionsBuilderExtensions.UseGen4Database(
        optionsBuilder,
        dataSource,
        typeof(Gen4.HP.Core.Data.Migrations.Init).Assembly.GetName().Name);

    using var context = new Gen4.HP.Core.Data.Context(optionsBuilder.Options);
    var pending = context.Database.GetPendingMigrations().ToList();
    if (pending.Count == 0)
    {
        return;
    }

    Console.WriteLine("Applying migrations:");
    foreach (var migration in pending)
    {
        Console.WriteLine($"\t{migration}");
    }

    context.Database.Migrate();
}

static bool NeedsHisMigrations()
{
    try
    {
        using var context = Database.CreateHisDbContext(Defines.HisDatabaseConfigPath);
        return !context.Database.CanConnect() || context.Database.GetPendingMigrations().Any();
    }
    catch
    {
        return true;
    }
}

static bool NeedsEntry(Gen4.HP.Core.Data.Context context, string key)
{
    return context.ConfigurationEntries.All(entry => entry.Key != key);
}

static void ConfigureLyra3Options(Gen4.HP.Core.Data.Context context)
{
    var entries = new Dictionary<string, string>
    {
        [LocalDefines.Lyra3.IsEnabledKey] = LocalDefines.Lyra3.IsEnabled,
        [LocalDefines.Lyra3.ServiceUrlKey] = LocalDefines.Lyra3.ServiceUrl,
        [LocalDefines.Lyra3.AuthApiAbsoluteUrlKey] = LocalDefines.Lyra3.AuthApiAbsoluteUrl,
    };

    foreach (var (key, value) in entries)
    {
        if (context.ConfigurationEntries.FirstOrDefault(entry => entry.Key == key)?.Value == value)
        {
            continue;
        }

        context.PutConfigurationEntry(key, value);
        Console.WriteLine($"Set configuration entry '{key}' = '{value}'");
    }
}

static void ConfigureJeconbol3Options(Gen4.HP.Core.Data.Context context)
{
    var entries = new Dictionary<string, string>
    {
        [LocalDefines.Jeconbol3.IsEnabledKey] = LocalDefines.Jeconbol3.IsEnabled,
        [LocalDefines.Jeconbol3.ServiceUrlKey] = LocalDefines.Jeconbol3.ServiceUrl,
        [LocalDefines.Jeconbol3.MinutesUpdateIntervalKey] = LocalDefines.Jeconbol3.MinutesUpdateInterval,
        [LocalDefines.Jeconbol3.ExportMinutesIntervalKey] = LocalDefines.Jeconbol3.ExportMinutesInterval,
    };

    foreach (var (key, value) in entries)
    {
        if (context.ConfigurationEntries.FirstOrDefault(entry => entry.Key == key)?.Value == value)
        {
            continue;
        }

        context.PutConfigurationEntry(key, value);
        Console.WriteLine($"Set configuration entry '{key}' = '{value}'");
    }
}

static void ConfigureJeconbol3OperatingRoom()
{
    var connectionString = Database.GetConnectionString(Defines.HisDatabaseConfigPath);
    if (string.IsNullOrEmpty(connectionString))
    {
        Console.WriteLine("Failed to get HIS database connection string. Jeconbol3 operating room skipped.");
        return;
    }

    using var context = Database.CreateHisDbContextFromConnectionString(connectionString);
    var roomId = Guid.Parse(LocalDefines.Jeconbol3.OperatingRoomId);

    if (context.OrganizationParts.Any(part => part.Id == roomId))
    {
        return;
    }

    context.OrganizationParts.Add(new Gen4.HP.HIS.Domain.Entities.OrganizationPart
    {
        Id = roomId,
        Name = LocalDefines.Jeconbol3.OperatingRoomName,
        Type = Gen4.HP.HIS.Domain.Enums.OrganizationPartType.OperatingRoom,
    });

    context.OperatingRoomAdditionalInfos.Add(new Gen4.HP.HIS.Domain.Entities.OperatingRoomAdditionalInfo
    {
        OrganizationPartId = roomId,
        ExternalHisId = LocalDefines.Jeconbol3.OperatingRoomExternalHisId,
        ExternalHisType = LocalDefines.Jeconbol3.ExternalHisType,
    });

    context.SaveChanges();
    Console.WriteLine($"Created Jeconbol3 operating room '{roomId}'");
}
