# Builds the phone's file server for each Android CPU into build\psftp-<arch>.gz, which the
# Windows app embeds. Needs Go (https://go.dev). Run before building the Windows app.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force $out | Out-Null
$env:CGO_ENABLED = "0"; $env:GOOS = "linux"
foreach ($t in @(@{ a = "arm64" }, @{ a = "arm"; arm = "7" }, @{ a = "amd64" }, @{ a = "386" })) {
    $env:GOARCH = $t.a; $env:GOARM = $t.arm
    $bin = Join-Path $out "psftp-$($t.a)"
    go build -trimpath -ldflags "-s -w -buildid=" -o $bin .
    if ($LASTEXITCODE -ne 0) { throw "go build failed for $($t.a)" }
    $in = [IO.File]::OpenRead($bin)
    $gzFile = [IO.File]::Create("$bin.gz")
    $gz = New-Object IO.Compression.GZipStream($gzFile, [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($gz); $gz.Dispose(); $in.Dispose()
    Remove-Item $bin
    "{0,-6} {1,8:N0} bytes" -f $t.a, (Get-Item "$bin.gz").Length
}
