using System.Text;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Cli;

public static class CliApp
{
    /// <summary>
    /// Runs one command. <paramref name="cancellationToken"/> is Ctrl+C: it detaches observation or abandons a pending
    /// request, and never sends a stop.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        // Until parsing succeeds, a scan decides how a parse error is reported; afterwards the invocation decides.
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
            output = new CliOutput(stdout, stderr, invocation.Json);
            if (invocation.Help)
            {
                stdout.WriteLine(CommandLine.Usage);
                return ExitCodes.Success;
            }
            return invocation.Command switch
            {
                "prepare" => Prepare(invocation, output),
                "run" => await SubmitAsync(invocation, output, cancellationToken),
                _ => await KnownRunAsync(invocation, output, cancellationToken),
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

    /// <summary>
    /// One submission attempt, then (unless detached) the SDK wait. The acknowledgement is reported, and saved when
    /// requested, before waiting, so a later failure never loses it; the proposed ID is never reported as the run.
    /// </summary>
    private static async Task<int> SubmitAsync(Invocation invocation, CliOutput output, CancellationToken cancellationToken)
    {
        invocation.Exclusive("--request", "--prepared");
        invocation.Exclusive("--detach", "--timeout");
        var timeout = invocation.WaitBudget("--timeout");
        var requestTimeout = invocation.Duration("--request-timeout");
        var configuration = TargetConfiguration.Load(invocation.Required("--config"));
        var overwrite = invocation.Flag("--overwrite");
        PreparedSubmission prepared;
        if (invocation.Value("--request") is { } requestPath) prepared = ReadRequest(requestPath).Prepare();
        else if (invocation.Value("--prepared") is { } preparedPath)
        {
            if (invocation.Has("--save-request")) throw CliFailure.Invocation("--save-request applies only to --request.");
            prepared = ImportPrepared(preparedPath);
        }
        else throw CliFailure.Invocation("'run' requires --request or --prepared.");
        var saveRun = invocation.Value("--save-run");
        // Refused now rather than after the mutation, which would leave an acknowledged run without its file.
        if (saveRun is not null && !overwrite && (File.Exists(saveRun) || Directory.Exists(saveRun)))
            throw CliFailure.OutputExists($"The run reference destination '{saveRun}' already exists; pass --overwrite to replace it.");

        using var client = configuration.CreateClient(requestTimeout, recover: null);
        var credentials = configuration.ResolveRunCredentials();
        if (invocation.Value("--save-request") is { } saveRequest)
            CliFiles.Write(saveRequest, prepared.ExportUtf8(), overwrite, "prepared request");

        var attempt = await client.SubmitAttemptAsync(prepared, credentials, cancellationToken);
        if (attempt.AcknowledgedRunId is not { } acknowledged)
            throw AttemptFailure(attempt, $"The submission of proposed run {prepared.RunId.Value}", runId: null);
        output.Submission(attempt);
        var run = client.GetRun(acknowledged);
        var evidence = AttemptEvidence.Of(attempt);

        if (saveRun is not null)
        {
            try { CliFiles.Write(saveRun, Encoding.UTF8.GetBytes(run.Reference.ToJson()), overwrite, "run reference"); }
            catch (CliFailure failure)
            {
                throw new CliFailure(failure.Category, $"Run {acknowledged.Value} was acknowledged, but its reference was not saved: {failure.Message}",
                    failure.ExitCode) { RunId = acknowledged, Attempt = evidence };
            }
        }
        if (invocation.Flag("--detach")) return ExitCodes.Success;

        try { return Completed(output, await run.WaitAsync(timeout, cancellationToken)); }
        catch (Exception error) when (error is not CliFailure) { throw Classify(error, invocation.Command, acknowledged, evidence); }
    }

    private static async Task<int> KnownRunAsync(Invocation invocation, CliOutput output, CancellationToken cancellationToken)
    {
        var requestTimeout = invocation.Duration("--request-timeout");
        TimeSpan? timeout = null;
        bool? recover = null;
        ExecutionRef? execution = null;
        Cursor? after = null;
        HistoryCheckpoint? checkpoint = null;
        switch (invocation.Command)
        {
            case "wait":
                timeout = invocation.WaitBudget("--timeout");
                break;
            case "force-stop":
                invocation.Exclusive("--wait-timeout", "--request-only");
                timeout = invocation.WaitBudget("--wait-timeout");
                break;
            case "watch" or "logs":
                invocation.Exclusive("--after", "--checkpoint");
                if (invocation.Value("--recovery") is { } mode)
                    recover = TargetConfiguration.Recovery(mode)
                        ?? throw CliFailure.Invocation("--recovery must be 'established-interruptions' or 'none'.");
                if (invocation.Value("--after") is { } cursor) after = Value(() => new Cursor(cursor), "--after must be a native cursor.");
                if (invocation.Value("--execution") is { } filter) execution = Value(() => new ExecutionRef(filter), "--execution must be a native execution reference.");
                if (invocation.Value("--checkpoint") is { } path)
                    checkpoint = Value(() => HistoryCheckpoint.Parse(Utf8(path, "checkpoint")), $"The checkpoint file '{path}' is not a valid history checkpoint.", CliFailure.Input);
                break;
        }

        var runFile = invocation.Value("--run-file");
        var attach = invocation.Command == "attach";
        if (invocation.Positionals.Count != (runFile is null ? 1 : 0) + (attach ? 1 : 0))
            throw CliFailure.Invocation(attach
                ? "'attach' takes RUN_ID EXECUTION, or EXECUTION with --run-file FILE."
                : $"'{invocation.Command}' takes one RUN_ID, or --run-file FILE instead.");
        if (attach) execution = Value(() => new ExecutionRef(invocation.Positionals[^1]), "EXECUTION must be a native execution reference.");

        var reference = runFile is null ? null
            : Value(() => RunReference.Parse(Utf8(runFile, "run")), $"The run file '{runFile}' is not a valid run reference.", CliFailure.Input);
        var configuration = invocation.Value("--config") is { } configPath ? TargetConfiguration.Load(configPath) : null;
        using var client = configuration?.CreateClient(requestTimeout, recover)
            ?? TargetConfiguration.CreateClient(new ZeroshotClientOptions
            {
                Target = reference?.Target ?? throw CliFailure.Invocation("RUN_ID requires --config FILE naming its target."),
                NativeBinding = reference.NativeBinding,
                Transport = requestTimeout is { } requested ? new() { RequestTimeout = requested } : new(),
                Observation = recover is { } value ? new() { Recover = value } : new(),
            }, requestTimeout is null ? $"The run file '{runFile}'" : $"The run file '{runFile}' with --request-timeout");
        var run = reference is not null
            ? Value(() => client.GetRun(reference), $"The run file '{runFile}' names a different target than the configuration.", CliFailure.Input)
            : Value(() => client.GetRun(new RunId(invocation.Positionals[0])), "RUN_ID must be a native run ID.");

        try
        {
            switch (invocation.Command)
            {
                case "status":
                    output.Status(await run.StatusAsync(cancellationToken));
                    return ExitCodes.Success;
                case "wait":
                    return Completed(output, await run.WaitAsync(timeout, cancellationToken));
                case "force-stop" when invocation.Flag("--request-only"):
                    var attempt = await run.ForceAttemptAsync(cancellationToken);
                    if (attempt.Response is null) throw AttemptFailure(attempt, $"The force request for run {run.Id.Value}", run.Id);
                    output.Force(attempt);
                    return ExitCodes.Success;
                case "force-stop":
                    return Completed(output, await run.ForceStopAsync(timeout, cancellationToken));
                case "attach":
                    return await StreamAsync(invocation.Command, run.Id, run.AttachAsync(execution!, cancellationToken),
                        output.Attachment, _ => null, cancellationToken);
            }
            var stream = invocation.Command == "watch" ? HistoryStream.Watch : HistoryStream.Logs;
            var start = checkpoint ?? (after is null ? null : new HistoryCheckpoint(client.Target, run.Id, stream, execution, after));
            // The SDK checks a checkpoint's scope before any I/O; --after is scoped here, so only a file can mismatch.
            T Scoped<T>(Func<T> open) => Value(open,
                $"The checkpoint file '{invocation.Value("--checkpoint")}' belongs to another target, run, stream or execution filter.", CliFailure.Input);
            return stream == HistoryStream.Watch
                ? await StreamAsync(invocation.Command, run.Id, Scoped(() => run.WatchAsync(start, cancellationToken)),
                    output.Watch, record => record.Checkpoint.Cursor, cancellationToken)
                : await StreamAsync(invocation.Command, run.Id, Scoped(() => run.LogsAsync(execution, start, cancellationToken)),
                    output.Log, record => record.Checkpoint.Cursor, cancellationToken);
        }
        catch (Exception error) when (error is not CliFailure) { throw Classify(error, invocation.Command, run.Id); }
    }

    /// <summary>
    /// Writes each record as the SDK delivers it. A normal native close exits 0 with no further record: it ends the
    /// stream, not the run. Every failure keeps the cursor of the last record written, after which a later command
    /// resumes; a closed stdout detaches. Nothing here stops the run.
    /// </summary>
    private static async Task<int> StreamAsync<T>(string command, RunId runId, IAsyncEnumerable<T> records, Action<T> write,
        Func<T, Cursor?> cursorOf, CancellationToken cancellationToken)
    {
        Cursor? delivered = null;
        try
        {
            await foreach (var record in records)
            {
                try { write(record); }
                catch (IOException)
                {
                    throw new CliFailure("output", $"Standard output was closed; the {command} of run {runId.Value} was detached and the run was not stopped.",
                        ExitCodes.Failure) { RunId = runId, Observation = delivered is null ? null : new(null, null, delivered) };
                }
                delivered = cursorOf(record);
            }
            return ExitCodes.Success;
        }
        // Ctrl+C can surface as the failure of whatever it interrupted, such as a connection being opened.
        catch (Exception error) when (error is not CliFailure && cancellationToken.IsCancellationRequested)
        {
            throw new CliFailure("cancelled", $"The {command} of run {runId.Value} was cancelled; the run was not stopped.", ExitCodes.Cancelled)
                { RunId = runId, Observation = delivered is null ? null : new(null, null, delivered) };
        }
        catch (Exception error) when (error is not CliFailure) { throw Classify(error, command, runId, delivered: delivered); }
    }

    /// <summary>A completion command succeeds only when the run did; a failed run is still reported as its result.</summary>
    private static int Completed(CliOutput output, RunResult result)
    {
        output.Result(result);
        return result.IsSuccess ? ExitCodes.Success : ExitCodes.RunFailed;
    }

    /// <summary>
    /// Maps the SDK's typed failures to exits without re-deriving their classification. Every failure keeps the
    /// known run, the acknowledged attempt that produced it (<paramref name="acknowledged"/> or a composed force)
    /// and the wait's latest evidence.
    /// </summary>
    private static CliFailure Classify(Exception error, string command, RunId runId, AttemptEvidence? acknowledged = null,
        Cursor? delivered = null)
    {
        if (error is ForceStopException force) return AttemptFailure(force.Attempt, $"The force request for run {runId.Value}", runId);
        if (error is ForceStopCanceledException forceCancelled)
            return AttemptFailure(forceCancelled.Attempt, $"The force request for run {runId.Value}", runId);
        var (category, message, exit) = error switch
        {
            NativeBindingException binding => ("binding", BindingMessage(binding), ExitCodes.Invalid),
            RunWaitTimeoutException => ("timeout",
                $"Run {runId.Value} reported no terminal result within the {command} timeout; it was not stopped.", ExitCodes.Timeout),
            RunWaitCanceledException => ("cancelled",
                $"Waiting for run {runId.Value} was cancelled; the run was not stopped and nothing was resent.", ExitCodes.Cancelled),
            RunWaitException { Kind: RunWaitFailureKind.Status } wait =>
                ("operational", $"Reading the status of run {runId.Value} failed while waiting ({Name(wait.InnerException)}).", ExitCodes.Failure),
            RunWaitException { Kind: RunWaitFailureKind.Observation } wait =>
                ("operational", $"Watching run {runId.Value} failed while waiting ({Name(wait.InnerException)}).", ExitCodes.Failure),
            RunWaitException => ("operational",
                $"The watch of run {runId.Value} ended without a terminal result and its status is not terminal.", ExitCodes.Failure),
            RunObservationException observation => ("operational", $"The {command} of run {runId.Value} failed: " + observation.Kind switch
            {
                RunObservationFailureKind.Establishment => "it could not be established.",
                RunObservationFailureKind.Interrupted => "it was interrupted and recovery is disabled.",
                RunObservationFailureKind.SourceUnavailable => "native reported its retained history unavailable.",
                RunObservationFailureKind.Protocol => "the target sent malformed or foreign data.",
                _ => "a local size or queue limit was reached.",
            }, ExitCodes.Failure),
            NativeSubscriptionException => ("operational",
                $"The attachment to run {runId.Value} ended without a native close; it is not reopened and missed output is not replayed.", ExitCodes.Failure),
            OperationCanceledException => ("cancelled", $"'{command}' for run {runId.Value} was cancelled; nothing was stopped.", ExitCodes.Cancelled),
            _ => ("operational", $"'{command}' for run {runId.Value} failed ({Name(error)}).", ExitCodes.Failure),
        };
        (RunWaitEvidence? evidence, NativeAttempt<RunForceResult>? forced) = error switch
        {
            RunWaitException wait => (wait.Evidence, wait.ForceAttempt),
            RunWaitCanceledException wait => (wait.Evidence, wait.ForceAttempt),
            _ => (null, null),
        };
        var observed = error switch
        {
            RunObservationException observation => new ObservationEvidence(Kebab(observation.Kind), observation.Recoveries, delivered),
            NativeSubscriptionException subscription => new ObservationEvidence(Kebab(subscription.Kind), null, null),
            _ => delivered is null ? null : new ObservationEvidence(null, null, delivered),
        };
        return new CliFailure(category, message, exit)
        {
            RunId = runId, Evidence = evidence, Attempt = forced is null ? acknowledged : AttemptEvidence.Of(forced),
            Native = NativeFailure.Of(error), Observation = observed,
        };
    }

    /// <summary>An unacknowledged mutation attempt, by the SDK's outcome. An unknown outcome is never resent or replaced.</summary>
    private static CliFailure AttemptFailure<T>(NativeAttempt<T> attempt, string what, RunId? runId) where T : class
    {
        var evidence = AttemptEvidence.Of(attempt);
        var (category, message, exit) = (attempt.Outcome, attempt.Failure) switch
        {
            (NativeAttemptOutcome.NotSent, NativeBindingException binding) => ("binding", BindingMessage(binding), ExitCodes.Invalid),
            (NativeAttemptOutcome.NotSent, OperationCanceledException) =>
                ("cancelled", $"{what} was cancelled before it was sent; nothing was sent.", ExitCodes.Cancelled),
            (NativeAttemptOutcome.NotSent, var failure) =>
                ("operational", $"{what} could not be sent ({Name(failure)}); nothing was sent.", ExitCodes.Failure),
            (NativeAttemptOutcome.Rejected, _) => ("rejected", $"{what} was rejected by the target.", ExitCodes.Failure),
            _ => ("unknown-outcome", $"{what} may have taken effect, but no acknowledgement was received" +
                (evidence.Cancelled ? " before it was cancelled" : "") + "; it was not resent.", ExitCodes.UnknownOutcome),
        };
        return new CliFailure(category, message, exit) { RunId = runId, Attempt = evidence, Native = NativeFailure.Of(attempt.Failure) };
    }

    private static string BindingMessage(NativeBindingException binding) => binding.Reason == NativeBindingProblem.Missing
        ? "No native binding is configured; run operations require the caller-supplied native 10.9.0 binding."
        : "A native binding does not match: the configuration and any run file must declare the supported native 10.9.0 source revision.";

    private static string Name(Exception? error) => error?.GetType().Name ?? "no detail";

    /// <summary>An SDK enum member as a record value: SourceUnavailable becomes source-unavailable.</summary>
    private static string Kebab(Enum value)
        => string.Concat(value.ToString().Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));

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
