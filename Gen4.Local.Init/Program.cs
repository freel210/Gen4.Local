using System.Diagnostics;

var masterCliPath = @"C:\Shared\Repos\gen4.hp.master.cli\src\Gen4.HP.Master.CLI\Gen4.HP.Master.CLI.csproj";

var commands = new[]
{
    "--migrate-core-database",
    "--migrate-his-database",
    "--generate-rsa-keys",
    "--set-nats-endpoint nats:nats@127.0.0.1:4222",
    "--generate-master-password"
};

int exitCode = 0;
foreach (var cmd in commands)
{
    Console.WriteLine($"\n>>> Running Master.CLI: {cmd}");
    exitCode = RunDotNetRun(masterCliPath, cmd);

    if (exitCode != 0)
    {
        Console.WriteLine($"ERROR: Command '{cmd}' failed with exit code {exitCode}. Aborting.");
        Environment.Exit(exitCode);
    }
}

Console.WriteLine("\n=== Initialization Completed Successfully ===");
Environment.Exit(0);

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
