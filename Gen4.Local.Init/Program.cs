using System.Diagnostics;

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
