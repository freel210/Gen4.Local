using Gen4.Local.SmokeTest;
using Gen4.Local.SmokeTest.Infrastructure;

var logger = new ConsoleLogger();
int exitCode;

try
{
    var config = SmokeTestConfig.FromEnvironment();
    exitCode = await SmokeTestRunner.RunAsync(config, logger);
}
catch (Exception ex)
{
    logger.Error(ex.ToString());
    exitCode = 1;
}

Console.WriteLine(exitCode == 0 ? "\n>>> SmokeTest PASSED" : "\n>>> SmokeTest FAILED");
return exitCode;