[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [string]$NsisCompiler
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Otimizador\Otimizador.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A versão do projeto deve estar no formato X.Y.Z.' }

if (!$NsisCompiler) {
    $NsisCompiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
    if (!$NsisCompiler) {
        $NsisCompiler = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'
    }
}
if (!(Test-Path -LiteralPath $NsisCompiler -PathType Leaf)) {
    throw 'Instale o NSIS ou informe o caminho de makensis.exe em -NsisCompiler.'
}

$output = Join-Path $root "artifacts\v$version"
$payload = Join-Path $output 'programa'
$installer = Join-Path $output "Otimizador-$version-windows-x64-setup.exe"

Push-Location -LiteralPath $root
try {
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $payload
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish terminou com o código $LASTEXITCODE." }

    Copy-Item -LiteralPath (Join-Path $root 'installer\LEIA-ME.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')

    $notices = @((Get-Content -Raw -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt')))
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -Filter '*.txt' | Sort-Object Name) {
        $notices += $file.Name, (Get-Content -Raw -LiteralPath $file.FullName)
    }
    $notices -join ([Environment]::NewLine * 2) | Set-Content -LiteralPath (Join-Path $payload 'LICENCAS-DE-TERCEIROS.txt') -Encoding utf8

    & $NsisCompiler /INPUTCHARSET UTF8 "/DAPP_VERSION=$version" "/DPAYLOAD_DIR=$payload" "/DSETUP_FILE=$installer" (Join-Path $PSScriptRoot 'Otimizador.nsi')
    if ($LASTEXITCODE -ne 0) { throw "makensis terminou com o código $LASTEXITCODE." }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $installer)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output $installer
} finally {
    Pop-Location
}
