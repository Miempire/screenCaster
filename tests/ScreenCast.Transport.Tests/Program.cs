using ScreenCast.Transport.Tests;

Console.WriteLine("ScreenCast transport tests");
TransportTests.All();
if (Environment.ExitCode != 0)
{
    Console.WriteLine("FAILED");
    return 1;
}

Console.WriteLine("All transport tests passed.");
return 0;