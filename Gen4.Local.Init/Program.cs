using System.Diagnostics;
using Gen4.HP.Master.CLI;
using Npgsql;

var connectionString = Database.GetConnectionString(Defines.CoreDatabaseConfigPath);
if (!string.IsNullOrEmpty(connectionString))
{
    UpsertLyra3Options(connectionString);
}

return;

var masterCliPath = @"C:\Shared\Repos\gen4.hp.master.cli\src\Gen4.HP.Master.CLI\Gen4.HP.Master.CLI.csproj";

var commands = new[]
{
    "--migrate-core-database",
    "--migrate-his-database",
    @"--import-rsa-keys C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Keys\privateKey.txt C:\Users\user2\Documents\Projects\Gen4.Local\Lyra3\Keys\publicKey.txt",
    "--set-nats-endpoint nats:nats@127.0.0.1:4222",
    "--generate-master-password"
};

int exitCode = 0;
foreach (var cmd in commands)
{
    Console.WriteLine($"\n{cmd}");
    exitCode = RunDotNetRun(masterCliPath, cmd);

    if (exitCode != 0)
    {
        Console.WriteLine($"ERROR: Command '{cmd}' failed with exit code {exitCode}. Aborting.");
        Environment.Exit(exitCode);
    }
}

Console.WriteLine("Done");
Environment.Exit(0);

static void UpsertLyra3Options(string connectionString)
{
    const string isEnabledKey = "/Configuration/Lyra3Options/IsEnabled";
    const string serviceUrlKey = "/Configuration/Lyra3Options/ServiceUrl";
    const string serviceUrlValue = "http://localhost";

    var entries = new Dictionary<string, string>
    {
        [isEnabledKey] = "true",
        [serviceUrlKey] = serviceUrlValue,
    };

    try
    {
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        foreach (var (key, value) in entries)
        {
            using var command = dataSource.CreateCommand(
                """
                INSERT INTO configuration_entries (key, value)
                VALUES (@key, @value)
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
                """);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("value", value);
            command.ExecuteNonQuery();
            Console.WriteLine($"Upserted configuration entry '{key}' = '{value}'");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"Failed to upsert Lyra3 options: {e.Message}.");
    }
}

static int RunDotNetRun(string projectPath, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{projectPath}\" -- {arguments}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;

    process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[Master.CLI] {e.Data}"); };
    process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[Master.CLI ERR] {e.Data}"); };

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    process.WaitForExit();
    return process.ExitCode;
}
