#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('artifacts/test-runs/' + [guid]::NewGuid().ToString('N'))
$filter = 'Category=Headless&Priority=Critical'

Push-Location $root
try {
    foreach ($name in @('Core', 'IO', 'Windows')) {
        $project = "tests/UWSCSharp.$name.Tests"
        dotnet test $project -c $Configuration --no-build --filter $filter `
            --logger "trx;LogFileName=$name.trx" --results-directory $run
        if ($LASTEXITCODE -ne 0) { throw "$name のテストに失敗しました。" }

        [xml]$result = Get-Content -LiteralPath (Join-Path $run "$name.trx") -Raw
        if ([int]$result.TestRun.ResultSummary.Counters.executed -le 0) {
            throw "$name のテストが実行されませんでした。"
        }
    }
    Write-Host "テスト結果: $run"
}
finally {
    Pop-Location
}
