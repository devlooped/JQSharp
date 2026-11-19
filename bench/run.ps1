#!/usr/bin/env pwsh
# Runs the cross-language benchmark (bench/README.md) and writes bench/results/results.{json,md}.
param(
    # The implementations to run (folders of the repository with a bench.json); dotnet always runs.
    [string[]] $Impl = @('dotnet', 'rust', 'go', 'java', 'typescript', 'python'),
    # The cases to run (names of bench/cases.json); all by default.
    [string[]] $Case = @(),
    [int] $WarmupMs = 1000,
    [int] $MeasureMs = 3000,
    # Do not run the build commands of bench.json.
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

# `pwsh -File` gives a list as one text: a,b,c.
$Impl = @($Impl | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$bench = $PSScriptRoot
$root = Split-Path $bench -Parent
$results = Join-Path $bench 'results'
New-Item -ItemType Directory -Force $results | Out-Null

# A JDK that is not on the PATH: the ones IDEs download to ~/.jdks.
if (-not $env:JAVA_HOME -and -not (Get-Command java -ErrorAction SilentlyContinue)) {
    $jdk = Get-ChildItem (Join-Path $HOME '.jdks') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($jdk) {
        $env:JAVA_HOME = $jdk.FullName
        $env:PATH = (Join-Path $jdk.FullName 'bin') + [IO.Path]::PathSeparator + $env:PATH
    }
}

$cases = Get-Content (Join-Path $bench 'cases.json') -Raw | ConvertFrom-Json
if ($Case.Count -gt 0) { $cases = @($cases | Where-Object { $Case -contains $_.name }) }
if ($cases.Count -eq 0) { throw 'No case to run.' }

$names = @('dotnet') + @($Impl | Where-Object { $_ -ne 'dotnet' })
$impls = foreach ($name in $names) {
    $file = Join-Path $root "$name/bench.json"
    if (Test-Path $file) {
        $config = Get-Content $file -Raw | ConvertFrom-Json
        [pscustomobject]@{ Name = $config.name; Folder = Join-Path $root $name; Build = $config.build; Run = $config.run }
    }
    else {
        Write-Warning "$name has no bench.json: skipped."
    }
}

function Invoke-In([string] $folder, [string[]] $command) {
    Push-Location $folder
    try {
        $exe, $rest = $command
        $output = & $exe @rest
        if ($LASTEXITCODE -ne 0) { throw "'$($command -join ' ')' failed with exit code $LASTEXITCODE in $folder." }
        return $output
    }
    finally { Pop-Location }
}

$measurements = [System.Collections.Generic.List[object]]::new()
foreach ($port in $impls) {
    if (-not $NoBuild) {
        foreach ($command in $port.Build) {
            Write-Host "[$($port.Name)] $($command -join ' ')" -ForegroundColor DarkGray
            Invoke-In $port.Folder ([string[]]$command) | Out-Null
        }
    }

    foreach ($item in $cases) {
        $program = (Resolve-Path (Join-Path $bench $item.program)).Path
        $inputFile = (Resolve-Path (Join-Path $bench $item.input)).Path
        $expected = Join-Path $bench "cases/$($item.name).expected"
        $actual = Join-Path $results "$($port.Name)-$($item.name).out"
        Remove-Item $actual -ErrorAction SilentlyContinue

        $arguments = [string[]]$port.Run + @($program, $inputFile, "$($item.take)", "$WarmupMs", "$MeasureMs", $actual)
        $line = Invoke-In $port.Folder $arguments | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 1

        if (-not [System.Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($expected), [IO.File]::ReadAllBytes($actual))) {
            throw "[$($port.Name)] $($item.name): the outputs differ from $expected (see $actual)."
        }

        $result = $line | ConvertFrom-Json
        $measurements.Add([pscustomobject]@{
            impl = $port.Name; runtime = $result.runtime; case = $item.name
            compile_ns = [double]$result.compile_ns; eval_ns = [double]$result.eval_ns; text_ns = [double]$result.text_ns
        })
        Write-Host ("[{0}] {1,-15} compile {2,12:N0} ns   eval {3,14:N0} ns   text {4,14:N0} ns" -f $port.Name, $item.name, $result.compile_ns, $result.eval_ns, $result.text_ns)
    }
}

$measurements | ConvertTo-Json | Set-Content (Join-Path $results 'results.json') -Encoding utf8

function Format-Time([double] $ns) {
    if ($ns -ge 1e9) { return '{0:N2} s' -f ($ns / 1e9) }
    if ($ns -ge 1e6) { return '{0:N2} ms' -f ($ns / 1e6) }
    if ($ns -ge 1e3) { return '{0:N2} µs' -f ($ns / 1e3) }
    return '{0:N0} ns' -f $ns
}

function Format-Ratio([double] $ratio) {
    if ($ratio -ge 100) { return '{0:N0}×' -f $ratio }
    if ($ratio -ge 10) { return '{0:N1}×' -f $ratio }
    return '{0:N2}×' -f $ratio
}

$others = @($impls | Where-Object { $_.Name -ne 'dotnet' })
$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine('# jqlite: time relative to .NET')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Each cell is the time of the implementation divided by the time of .NET on the same case: 1.00× is as fast as .NET, 2.00× takes twice as long. Measured with ``bench/run.ps1 -WarmupMs $WarmupMs -MeasureMs $MeasureMs`` (see ``bench/README.md``).")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Implementation | Runtime |')
[void]$markdown.AppendLine('| --- | --- |')
foreach ($port in $impls) {
    $runtime = ($measurements | Where-Object impl -eq $port.Name | Select-Object -First 1).runtime
    [void]$markdown.AppendLine("| $($port.Name) | $runtime |")
}

$sections = [ordered]@{
    eval_ns    = 'Evaluate: a compiled program on a value already read'
    text_ns    = 'Text to text: read the input, evaluate, write the outputs'
    compile_ns = 'Compile: parse and bind the program'
}
foreach ($metric in $sections.Keys) {
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine("## $($sections[$metric])")
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('| Case | .NET (baseline) | ' + (($others | ForEach-Object Name) -join ' | ') + ' |')
    [void]$markdown.AppendLine('| --- | ---: | ' + (($others | ForEach-Object { '---:' }) -join ' | ') + ' |')
    $logs = @{}
    foreach ($item in $cases) {
        $base = ($measurements | Where-Object { $_.impl -eq 'dotnet' -and $_.case -eq $item.name }).$metric
        $cells = foreach ($port in $others) {
            $value = ($measurements | Where-Object { $_.impl -eq $port.Name -and $_.case -eq $item.name }).$metric
            $ratio = $value / $base
            $logs[$port.Name] = @($logs[$port.Name]) + [Math]::Log($ratio)
            Format-Ratio $ratio
        }
        [void]$markdown.AppendLine("| $($item.name) | 1.00× ($(Format-Time $base)) | " + ($cells -join ' | ') + ' |')
    }

    $means = foreach ($port in $others) {
        $values = @($logs[$port.Name] | Where-Object { $null -ne $_ })
        '**' + (Format-Ratio ([Math]::Exp(($values | Measure-Object -Sum).Sum / $values.Count))) + '**'
    }
    [void]$markdown.AppendLine('| **geometric mean** | **1.00×** | ' + ($means -join ' | ') + ' |')
}

$markdown.ToString() | Set-Content (Join-Path $results 'results.md') -Encoding utf8
Write-Host "`n$($markdown.ToString())"
