namespace FlipPix.IosCompanion;

/// <summary>
/// <c>flippix-companion serve</c> runs the companion (the systemd user service does this);
/// <c>flippix-companion</c> / <c>status</c> prints the pairing code and what's running;
/// <c>code</c> asks the running companion for a fresh pairing code and prints it.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant() ?? "status")
        {
            case "serve":
                return await Service.RunAsync();
            case "status":
                return StatusCommand.Print();
            case "code":
                return await StatusCommand.NewCodeAsync();
            case "--version":
                Console.WriteLine(typeof(Program).Assembly.GetName().Version);
                return 0;
            default:
                Console.WriteLine("Usage: flippix-companion [status | code | serve]");
                Console.WriteLine("  status   the pairing code and whether everything the iPad needs is running (default)");
                Console.WriteLine("  code     a fresh pairing code");
                Console.WriteLine("  serve    run the companion (the flippix-companion service does this)");
                return args[0] is "-h" or "--help" or "help" ? 0 : 2;
        }
    }
}
