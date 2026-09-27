param([string]$InnoCompiler, [string]$WebViewBootstrapper, [switch]$SkipWebBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$buildRoot = Join-Path $projectRoot 'build'
$payload = Join-Path $buildRoot 'App'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
Push-Location $projectRoot
try {
    if (-not $SkipWebBuild) {
        Push-Location (Join-Path $projectRoot 'Chat')
        try {
            & pnpm install --frozen-lockfile
            if ($LASTEXITCODE) { throw 'Chat dependency installation failed.' }
            & pnpm run build:island
            if ($LASTEXITCODE) { throw 'Chat build failed.' }
        } finally { Pop-Location }
    }
    & dotnet publish IslandUI/IslandUI.csproj -c Release -r win-x64 --self-contained true -o $payload -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Native application build failed.' }
    & node Packaging/stage-runtime.mjs $payload
    if ($LASTEXITCODE) { throw 'Runtime staging failed.' }
    if ($InnoCompiler -and $WebViewBootstrapper) {
        $signature = Get-AuthenticodeSignature -LiteralPath $WebViewBootstrapper
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft*') {
            throw 'The WebView2 bootstrapper must have a valid Microsoft signature.'
        }
        & $InnoCompiler "/DPayload=$payload" "/DBootstrapper=$WebViewBootstrapper" "/DOutput=$buildRoot" Packaging/IslandUI.iss
        if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
    }
} finally { Pop-Location }
