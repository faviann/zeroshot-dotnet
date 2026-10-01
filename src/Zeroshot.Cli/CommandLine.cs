namespace Zeroshot.Cli;

/// <summary>One parsed command: its name, flags, option values and positional arguments.</summary>
internal sealed class Invocation(string command, HashSet<string> flags, Dictionary<string, string> values, List<string> positionals)
{
    public string Command { get; } = command;
    public IReadOnlyList<string> Positionals { get; } = positionals;
    public bool Json => flags.Contains("--json");
    public bool Help => flags.Contains("--help");

    public bool Flag(string name) => flags.Contains(name);
    public string? Value(string name) => values.GetValueOrDefault(name);
    public string Required(string name)
        => Value(name) ?? throw CliFailure.Invocation($"'{Command}' requires {name}.");

    public void Exclusive(string first, string second)
    {
        if (Has(first) && Has(second)) throw CliFailure.Invocation($"{first} and {second} cannot be combined.");
    }

    public bool Has(string name) => flags.Contains(name) || values.ContainsKey(name);

    public TimeSpan? Duration(string name)
    {
        if (Value(name) is not { } text) return null;
        return CliDuration.TryParse(text, out var value) ? value
            : throw CliFailure.Invocation($"{name} must be {CliDuration.Rule}.");
    }

    /// <summary>A wait budget; absent or <c>infinite</c> is indefinite (null).</summary>
    public TimeSpan? WaitBudget(string name)
    {
        if (Value(name) is not { } text) return null;
        return CliDuration.TryParseWaitBudget(text, out var value) ? value
            : throw CliFailure.Invocation($"{name} must be 'infinite' or {CliDuration.Rule}, {CliDuration.MaxWaitRule}.");
    }
}

/// <summary>The accepted command grammar. Every command also takes --json and --help.</summary>
internal static class CommandLine
{
    private sealed record Grammar(string[] Flags, string[] Values, int MaxPositionals);

    private static readonly string[] TargetOptions = ["--config", "--request-timeout"];
    private static readonly string[] KnownRun = ["--run-file", .. TargetOptions];
    private static readonly string[] History = [.. KnownRun, "--after", "--checkpoint", "--recovery"];

    private static readonly Dictionary<string, Grammar> Commands = new(StringComparer.Ordinal)
    {
        ["prepare"] = new(["--overwrite"], ["--request", "--out"], 0),
        ["run"] = new(["--detach", "--overwrite"],
            ["--request", "--prepared", "--timeout", "--save-request", "--save-run", .. TargetOptions], 0),
        ["status"] = new([], KnownRun, 1),
        ["wait"] = new([], [.. KnownRun, "--timeout"], 1),
        ["watch"] = new([], History, 1),
        ["logs"] = new([], [.. History, "--execution"], 1),
        ["attach"] = new([], KnownRun, 2),
        ["force-stop"] = new(["--request-only"], [.. KnownRun, "--wait-timeout"], 1),
    };

    public static bool IsCommand(string name) => Commands.ContainsKey(name);

    public static Invocation Parse(string[] args)
    {
        if (!Commands.TryGetValue(args[0], out var grammar))
            throw CliFailure.Invocation($"Unknown command '{args[0]}'.");
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i] == "-h" ? "--help" : args[i];
            // --name=value takes any value verbatim, including one that begins with "--" (opaque cursors and
            // references allow it); the separate form refuses such a value as a probably forgotten one.
            var (name, inline) = arg.IndexOf('=') is > 2 and var at && arg.StartsWith("--", StringComparison.Ordinal)
                ? (arg[..at], arg[(at + 1)..]) : (arg, null);
            if (inline is null && (arg is "--json" or "--help" || grammar.Flags.Contains(arg)))
            {
                if (!flags.Add(arg)) throw CliFailure.Invocation($"{arg} was given more than once.");
            }
            else if (grammar.Values.Contains(name))
            {
                if (inline is null && (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                    throw CliFailure.Invocation($"{name} requires a value.");
                if (!values.TryAdd(name, inline ?? args[++i])) throw CliFailure.Invocation($"{name} was given more than once.");
            }
            else if (arg.StartsWith('-'))
                throw CliFailure.Invocation($"'{args[0]}' does not accept {arg}.");
            else if (positionals.Count == grammar.MaxPositionals)
                throw CliFailure.Invocation($"Unexpected argument '{arg}'.");
            else positionals.Add(arg);
        }
        return new Invocation(args[0], flags, values, positionals);
    }

    public const string Usage = """
        zeroshot-dotnet: thin command line over the Zeroshot .NET SDK (native 10.9.0).

        Usage:
          zeroshot-dotnet prepare --request FILE --out FILE [--overwrite]
          zeroshot-dotnet run --config FILE (--request FILE | --prepared FILE)
                              [--detach | --timeout WAIT] [--save-request FILE] [--save-run FILE]
                              [--overwrite] [--request-timeout DURATION]
          zeroshot-dotnet status RUN [--request-timeout DURATION]
          zeroshot-dotnet wait RUN [--timeout WAIT] [--request-timeout DURATION]
          zeroshot-dotnet watch RUN [--after CURSOR | --checkpoint FILE] [--recovery MODE] [--request-timeout DURATION]
          zeroshot-dotnet logs RUN [--execution EXECUTION] [--after CURSOR | --checkpoint FILE] [--recovery MODE]
                               [--request-timeout DURATION]
          zeroshot-dotnet attach RUN EXECUTION [--request-timeout DURATION]
          zeroshot-dotnet force-stop RUN [--wait-timeout WAIT | --request-only] [--request-timeout DURATION]
          zeroshot-dotnet --version

        Every command accepts --json (versioned zeroshot-dotnet/cli/v1 records) and --help. An option
        value can also be given as --option=VALUE, which is required for a value that begins with --.

          RUN        RUN_ID with --config FILE, or --run-file FILE with an optional matching --config FILE.
          --config   Target configuration: address, caller-supplied native binding, transport/observation
                     settings and the names of environment variables that hold credentials.
          DURATION   A whole number with a unit ms, s, m or h, such as 1500ms, 45s or 10m.
          WAIT       A DURATION or 'infinite' (the default).
          CURSOR     An opaque native cursor from an earlier watch or logs record.
          MODE       established-interruptions (default) or none.

        prepare needs no target, credentials or network. run submits once and waits for the result
        unless --detach is given; --save-request is written before submitting and --save-run only
        after an acknowledgement. force-stop sends one force request, then waits unless
        --request-only is given. Existing output files are refused unless --overwrite is given.
        watch and logs replay retained history exclusively after --after or --checkpoint, then follow
        it live until native closes the stream, reopening an interrupted stream after the last record
        written unless --recovery none is given. attach streams one active execution live, with no
        replay and no reopen. A normal close exits 0 and says nothing about the run's outcome.
        Ctrl+C detaches or abandons the pending request; it never stops a run.

        Exit codes: 0 success (including status of a failed run), 1 operational failure or native
        rejection, 2 invalid invocation, configuration, input or binding, 3 run, wait or force-stop
        observed a failed run, 4 wait timeout, 5 unknown mutation outcome, 130 cancelled.
        """;
}
