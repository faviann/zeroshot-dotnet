namespace Zeroshot.Cli;

public static class CliApp
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            output.WriteLine("Zeroshot CLI");
            output.WriteLine("Usage: zeroshot-cli [--help]");
            output.WriteLine("Commands will be added after the client protocol is designed.");
            return 0;
        }

        error.WriteLine($"Unknown argument: {args[0]}");
        error.WriteLine("Use --help to see available options.");
        return 2;
    }
}
