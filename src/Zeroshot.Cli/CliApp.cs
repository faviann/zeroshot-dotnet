using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Cli;

public static class CliApp
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var output = new CliOutput(stdout, stderr, args.Contains("--json"));
        var operation = args.Length > 0 && CommandLine.IsCommand(args[0]) ? args[0] : null;
        try
        {
            if (args is [] or ["help"] or ["--help"] or ["-h"])
            {
                stdout.WriteLine(CommandLine.Usage);
                return ExitCodes.Success;
            }
            var invocation = CommandLine.Parse(args);
            if (invocation.Help)
            {
                stdout.WriteLine(CommandLine.Usage);
                return ExitCodes.Success;
            }
            return invocation.Command switch
            {
                "prepare" => Prepare(invocation, output),
                "run" => Submit(invocation),
                _ => KnownRun(invocation),
            };
        }
        catch (CliFailure failure)
        {
            output.Error(operation, failure);
            return failure.ExitCode;
        }
        catch (Exception unexpected)
        {
            // Exception text can carry payload fragments, so only the type is reported.
            output.Error(operation, new CliFailure("internal", $"Unexpected {unexpected.GetType().Name}.", ExitCodes.Failure));
            return ExitCodes.Failure;
        }
    }

    /// <summary>Local only: parse, fix identity with the SDK and write the exact exported bytes.</summary>
    private static int Prepare(Invocation invocation, CliOutput output)
    {
        var request = ReadRequest(invocation.Required("--request"));
        var destination = invocation.Required("--out");
        var prepared = request.Prepare();
        CliFiles.Write(destination, prepared.ExportUtf8(), invocation.Flag("--overwrite"), "prepared request");
        output.Prepared(prepared.RunId, destination);
        return ExitCodes.Success;
    }

    private static int Submit(Invocation invocation)
    {
        invocation.Exclusive("--request", "--prepared");
        invocation.Exclusive("--detach", "--timeout");
        _ = invocation.WaitBudget("--timeout");
        var requestTimeout = invocation.Duration("--request-timeout");
        var configuration = TargetConfiguration.Load(invocation.Required("--config"));
        if (invocation.Value("--request") is { } requestPath) _ = ReadRequest(requestPath);
        else if (invocation.Value("--prepared") is { } preparedPath)
        {
            if (invocation.Has("--save-request")) throw CliFailure.Invocation("--save-request applies only to --request.");
            _ = ImportPrepared(preparedPath);
        }
        else throw CliFailure.Invocation("'run' requires --request or --prepared.");
        using var client = configuration.CreateClient(requestTimeout, recover: null);
        _ = configuration.ResolveRunCredentials();
        throw NotAvailable(invocation.Command);
    }

    private static int KnownRun(Invocation invocation)
    {
        var requestTimeout = invocation.Duration("--request-timeout");
        bool? recover = null;
        switch (invocation.Command)
        {
            case "wait":
                _ = invocation.WaitBudget("--timeout");
                break;
            case "force-stop":
                invocation.Exclusive("--wait-timeout", "--request-only");
                _ = invocation.WaitBudget("--wait-timeout");
                break;
            case "watch" or "logs":
                invocation.Exclusive("--after", "--checkpoint");
                if (invocation.Value("--recovery") is { } mode)
                    recover = TargetConfiguration.Recovery(mode)
                        ?? throw CliFailure.Invocation("--recovery must be 'established-interruptions' or 'none'.");
                if (invocation.Value("--after") is { } after) _ = Value(() => new Cursor(after), "--after must be a native cursor.");
                if (invocation.Value("--execution") is { } execution) _ = Value(() => new ExecutionRef(execution), "--execution must be a native execution reference.");
                if (invocation.Value("--checkpoint") is { } checkpoint)
                    _ = Value(() => HistoryCheckpoint.Parse(Utf8(checkpoint, "checkpoint")), $"The checkpoint file '{checkpoint}' is not a valid history checkpoint.", CliFailure.Input);
                break;
        }

        var runFile = invocation.Value("--run-file");
        var attach = invocation.Command == "attach";
        if (invocation.Positionals.Count != (runFile is null ? 1 : 0) + (attach ? 1 : 0))
            throw CliFailure.Invocation(attach
                ? "'attach' takes RUN_ID EXECUTION, or EXECUTION with --run-file FILE."
                : $"'{invocation.Command}' takes one RUN_ID, or --run-file FILE instead.");
        if (attach) _ = Value(() => new ExecutionRef(invocation.Positionals[^1]), "EXECUTION must be a native execution reference.");

        var reference = runFile is null ? null
            : Value(() => RunReference.Parse(Utf8(runFile, "run")), $"The run file '{runFile}' is not a valid run reference.", CliFailure.Input);
        var configuration = invocation.Value("--config") is { } configPath ? TargetConfiguration.Load(configPath) : null;
        using var client = configuration?.CreateClient(requestTimeout, recover)
            ?? TargetConfiguration.CreateClient(new ZeroshotClientOptions
            {
                Target = reference?.Target ?? throw CliFailure.Invocation("RUN_ID requires --config FILE naming its target."),
                NativeBinding = reference.NativeBinding,
                Transport = requestTimeout is { } timeout ? new() { RequestTimeout = timeout } : new(),
                Observation = recover is { } value ? new() { Recover = value } : new(),
            }, requestTimeout is null ? $"The run file '{runFile}'" : $"The run file '{runFile}' with --request-timeout");
        if (reference is not null)
            _ = Value(() => client.GetRun(reference), $"The run file '{runFile}' names a different target than the configuration.", CliFailure.Input);
        else
            _ = Value(() => client.GetRun(new RunId(invocation.Positionals[0])), "RUN_ID must be a native run ID.");
        throw NotAvailable(invocation.Command);
    }

    // Until each command's SDK workflow lands, it validates everything it needs locally and stops before any I/O.
    private static CliFailure NotAvailable(string command)
        => new("unavailable", $"'{command}' is not available in this build; nothing was sent.", ExitCodes.Invalid);

    private static RunRequest ReadRequest(string path)
        => Value(() => RunRequest.ParseUtf8(CliFiles.Read(path, "request")), $"The request file '{path}' is not a valid run request.", CliFailure.Input);

    private static PreparedSubmission ImportPrepared(string path)
        => Value(() => PreparedSubmission.ImportUtf8(CliFiles.Read(path, "prepared request")), $"The prepared request file '{path}' is not a valid prepared request.", CliFailure.Input);

    private static string Utf8(string path, string what)
        => new UTF8Encoding(false, true).GetString(CliFiles.Read(path, what));

    /// <summary>Runs an SDK parse or value check, replacing its exception with a safe, CLI-authored failure.</summary>
    private static T Value<T>(Func<T> parse, string message, Func<string, CliFailure>? failure = null)
    {
        try { return parse(); }
        catch (Exception error) when (error is JsonException or ArgumentException or DecoderFallbackException)
        { throw (failure ?? CliFailure.Invocation)(message); }
    }
}
