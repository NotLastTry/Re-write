# Запуск автономного замера PerfCapture несколько раз подряд.
#
# Пример:
#   pwsh -File run_capture.ps1 -Exe "..\..\..\..\Re-write-perf\Builds\Perf\Re-write-perf.exe" `
#        -OutRoot "captures\before" -Mode batch -Runs 3
#
# -Mode batch    — без окна и рендера: стабильные CPU-метрики генерации;
# -Mode windowed — с окном 1600x900: FPS, GPU, draw calls и скриншоты сцены.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [ValidateSet("batch", "windowed")][string]$Mode = "batch",
    [int]$Runs = 1,
    [string]$Prefix = "run"
)

$ErrorActionPreference = "Stop"
$exePath = (Resolve-Path $Exe).Path
$rootPath = (Resolve-Path $OutRoot -ErrorAction SilentlyContinue)
if (-not $rootPath) {
    New-Item -ItemType Directory -Force $OutRoot | Out-Null
    $rootPath = (Resolve-Path $OutRoot).Path
}

for ($i = 1; $i -le $Runs; $i++) {
    $out = Join-Path $rootPath ("{0}{1}" -f $Prefix, $i)
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null

    $arguments = @("-perfCapture", "-perfOut", $out, "-logFile", (Join-Path $out "player.log"))
    if ($Mode -eq "batch") {
        $arguments = @("-batchmode") + $arguments
    }
    else {
        $arguments += @("-screen-width", "1600", "-screen-height", "900", "-screen-fullscreen", "0")
    }

    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru -Wait
    $summary = Join-Path $out "summary.txt"
    Write-Host ("[{0}{1}] exit={2}" -f $Prefix, $i, $process.ExitCode)
    if (Test-Path $summary) {
        Get-Content $summary -TotalCount 12 | Select-Object -Skip 6 | ForEach-Object { "    $_" }
    }
}
