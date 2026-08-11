namespace YTile.Cli;

internal static class Program
{
    private const string Version = "0.1.0-dev";

    private static int Main(string[] args)
    {
        if (args is ["--version"] or ["-V"])
        {
            Console.WriteLine($"ytile {Version}");
            return 0;
        }

        Console.WriteLine($"ytile {Version} — CLI for the YTile daemon (ytiled).");
        Console.WriteLine("The IPC protocol is not implemented yet; commands land alongside the daemon's message loop.");
        return args.Length == 0 ? 0 : 2;
    }
}
