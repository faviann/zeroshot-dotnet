# Independently runnable stock-native Windows x64 controller-pipe witness. Stock `zeroshot run --detach`
# starts a local portable controller; this harness owns that process, and the packed consumer only
# connects to the pipe path native records in its own controller.ready.json.
param([string] $Launch)
$ErrorActionPreference = 'Stop'
if ($Launch) {
    # Launcher mode: a scrubbed environment with only essential Windows settings, isolated
    # state/config/profile and a fake key. Native's detached controller discards these stdio pipes.
    $start = [Diagnostics.ProcessStartInfo]::new("$Launch/bin/zeroshot.exe")
    foreach ($argument in 'run', '--detach', '--title', 'Native controller witness', '--graph', "$Launch/controller-graph.json",
        '--runtime-config', "$Launch/controller-runtime.json", '--input', "$Launch/controller-input.json") { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = "$Launch/controller-source"
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    $start.Environment['PATH'] = (Get-Content -Raw "$Launch/launch-path.txt").Trim()
    foreach ($name in 'SystemRoot', 'WINDIR', 'COMSPEC', 'PATHEXT', 'TEMP', 'TMP') { $start.Environment[$name] = [Environment]::GetEnvironmentVariable($name) }
    $start.Environment['USERPROFILE'] = "$Launch/controller-home"
    $start.Environment['HOME'] = "$Launch/controller-home"
    $start.Environment['OPENAI_API_KEY'] = 'controlled-not-a-real-key'
    $start.Environment['ZEROSHOT_STATE_DIR'] = "$Launch/cs"
    $start.Environment['ZEROSHOT_CONFIG_DIR'] = "$Launch/controller-config"
    try {
        $run = [Diagnostics.Process]::Start($start)
        $errors = $run.StandardError.ReadToEndAsync()
        Set-Content "$Launch/controller-receipt.json" $run.StandardOutput.ReadToEnd()
        Set-Content "$Launch/controller-native.log" $errors.Result
        $run.WaitForExit()
        Set-Content "$Launch/controller-exit.txt" $run.ExitCode
    }
    catch { Set-Content "$Launch/controller-native.log" $_; Set-Content "$Launch/controller-exit.txt" 'launcher-failed' }
    exit
}
$PSNativeCommandUseErrorActionPreference = $true
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Windows x64 is required.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$witness = Join-Path ([IO.Path]::GetTempPath()) ("zsw-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$nativeVersion = '10.10.0'
$sourceRevision = '3ee1192cec359a0b997f464e703a936e8b67d63c'
$archive = 'zeroshot-v10.10.0-x86_64-pc-windows-msvc.tar.gz'
$archiveSha256 = '74a857efde74a678e2dd3c956f672a13f671bafe0992f42b891a3ab904a65f9c'
$executableSha256 = 'd8f4d6bb61aaab2083486e5048e70720048042ffe264a459eed0879c2aaa5eb7'
New-Item -ItemType Directory -Path $witness, "$witness/bin", "$witness/feed", "$witness/packages", "$witness/controller-consumer",
    "$witness/controller-source", "$witness/controller-fake", "$witness/controller-home", "$witness/controller-config" | Out-Null
$provenance = "$witness/provenance.txt"
function Hash([string] $path) { "$((Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant())  $path" }
$controller = $null
try {
    # A caller may cache the unmodified release archive; its digest is always checked.
    if ($env:ZEROSHOT_WITNESS_ARCHIVE) { Copy-Item $env:ZEROSHOT_WITNESS_ARCHIVE "$witness/$archive" }
    else { Invoke-WebRequest "https://github.com/the-open-engine/zeroshot/releases/download/v$nativeVersion/$archive" -OutFile "$witness/$archive" }
    if ((Get-FileHash -Algorithm SHA256 "$witness/$archive").Hash -ne $archiveSha256) { throw 'Native archive digest mismatch.' }
    # Local runs allocate their runtime with the bundled restic.
    tar -xzf "$witness/$archive" -C "$witness/bin" zeroshot.exe restic.exe
    if ((Get-FileHash -Algorithm SHA256 "$witness/bin/zeroshot.exe").Hash -ne $executableSha256) { throw 'Native executable digest mismatch.' }
    @(
        "nativeVersion=$nativeVersion", "sourceRevision=$sourceRevision",
        "release=https://github.com/the-open-engine/zeroshot/releases/tag/v$nativeVersion", "archiveSha256=$archiveSha256",
        (Hash "$witness/bin/zeroshot.exe"), (Hash "$witness/bin/restic.exe"), (& "$witness/bin/zeroshot.exe" --version),
        "os=$([Runtime.InteropServices.RuntimeInformation]::OSDescription) arch=$([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) dotnet=$(dotnet --version)"
    ) | Set-Content $provenance

    # Pack once and restore the consumer outside the repository from that feed with an isolated cache.
    dotnet pack "$repo/src/Zeroshot.Sdk/Zeroshot.Sdk.csproj" --configuration Release --output "$witness/feed"
    Copy-Item "$repo/examples/ControllerConsumer/*.cs*" "$witness/controller-consumer/"
    # Build before starting the run so a cold build cannot outlast the provider's 60 s gate.
    # Sources live in a consumer-local config; only the packed feed and nuget.org are used.
    Set-Content "$witness/controller-consumer/NuGet.Config" @"
<configuration><packageSources><clear /><add key="feed" value="$witness\feed" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@
    dotnet restore "$witness/controller-consumer/ControllerConsumer.csproj" --packages "$witness/packages"
    dotnet build "$witness/controller-consumer/ControllerConsumer.csproj" -c Release --no-restore

    # Local runs require a GitHub origin for source identity; nothing is fetched from it.
    $source = "$witness/controller-source"
    git -C $source init --initial-branch=main | Out-File "$witness/controller-git.log"
    Set-Content "$source/README.md" 'Controlled controller source'
    git -C $source add README.md
    git -C $source -c user.name=Witness -c user.email=witness@example.invalid commit -m 'Controlled controller source' | Out-File -Append "$witness/controller-git.log"
    git -C $source remote add origin https://github.com/fixture/controller.git

    # The same test-owned Codex JSONL producer as the Linux witness, behind a .cmd shim native resolves on PATH.
    Copy-Item "$repo/tools/native-witness/controller-provider.py" "$witness/controller-fake/"
    $python = (Get-Command python).Source
    Set-Content "$witness/controller-fake/codex.cmd" "@`"$python`" `"%~dp0controller-provider.py`" %*" -Encoding ascii
    Copy-Item "$repo/tools/native-witness/windows/controller-graph.json", "$repo/tools/native-witness/windows/controller-runtime.json" $witness
    Set-Content "$witness/controller-input.json" 'null' -Encoding ascii
    @((Hash "$witness/controller-graph.json"), (Hash "$witness/controller-runtime.json"), (Hash "$witness/controller-fake/controller-provider.py"),
        'controllerAsset=attachment worker graph/runtime; stock local run --detach portable controller; controlled Codex JSONL producer first on PATH; no provider service') |
        Add-Content $provenance

    # GitHub Actions confines each step to a Windows Job without breakaway, which native's detach refuses.
    # WMI starts the launcher outside that Job; the launcher scrubs the environment and starts native.
    $state = "$witness/cs"
    Set-Content "$witness/launch-path.txt" "$witness/controller-fake;$witness/bin;$(Split-Path (Get-Command git).Source);$env:SystemRoot\System32;$env:SystemRoot"
    $pwsh = (Get-Process -Id $PID).Path
    $launcher = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$pwsh`" -NoProfile -NonInteractive -File `"$PSCommandPath`" -Launch `"$witness`""; CurrentDirectory = $witness }
    if ($launcher.ReturnValue -ne 0) { throw "WMI could not start the launcher ($($launcher.ReturnValue))." }
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while (Get-Process -Id $launcher.ProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The native launcher did not finish.' }
        Start-Sleep -Milliseconds 200
    }
    $receipt = Get-Content -Raw "$witness/controller-receipt.json"
    $exitCode = Get-Content -Raw "$witness/controller-exit.txt"
    if ($exitCode.Trim() -ne '0') { Get-Content "$witness/controller-native.log"; throw "zeroshot run --detach failed with $exitCode." }
    $runId = ($receipt | ConvertFrom-Json).runId

    # Native's own readiness record names the pipe; the harness never derives it.
    $ready = Get-Content -Raw "$state/runs/$runId/controller.ready.json" | ConvertFrom-Json
    if ($ready.runId -ne $runId -or -not $ready.socket.StartsWith('\\.\pipe\')) { throw 'Native readiness does not name this run''s pipe.' }
    $controller = Get-Process -Id $ready.pid -ErrorAction SilentlyContinue
    Add-Content $provenance "controllerRun=$runId pipe=$($ready.socket) pid=$($ready.pid)"
    dotnet run --project "$witness/controller-consumer/ControllerConsumer.csproj" -c Release --no-build --no-restore -- `
        $ready.socket $witness | Out-File -Encoding utf8 "$witness/controller.json"
    'PASS: stock native Windows x64 local-run controller over an owned, security-validated named pipe and a borrowed pipe stream: initialize, empty get, one-run list/status, foreign-run NOT_FOUND status and rejected force, resume/discard INVALID_PHASE rejections, stream reuse after connection disposal, watch/log delivery through controller exit' |
        Tee-Object "$witness/result.txt"
    Get-Content "$witness/controller.json"
    Get-Content $provenance
}
finally {
    # Release a waiting provider gate, then stop a controller that outlived its run.
    New-Item -ItemType File -Force "$witness/controller-release" | Out-Null
    if ($controller -and -not $controller.WaitForExit(30000)) { Stop-Process -Id $controller.Id -Force }
    "Witness artifacts: $witness"
}
