# Builds Capri's crew and memory on their own with stand-ins, runs a real terminal job through Windows PowerShell, then tests her journal and orbits.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$out = Join-Path $root 'out\crew-test'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$crew = Join-Path $root 'app\Crew.cs'
$memory = Join-Path $root 'app\Memory.cs'
$stubs = Join-Path $PSScriptRoot 'crew-test\Stubs.cs'
foreach ($test in @('CrewTest', 'MemTest')) {
    $exe = Join-Path $out ($test + '.exe')
    $source = Join-Path $PSScriptRoot ('crew-test\' + $test + '.cs')
    & $compiler /nologo /langversion:5 /target:exe "/out:$exe" /reference:System.Runtime.Serialization.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $crew $memory $stubs $source
    if ($LASTEXITCODE -ne 0) { throw "$test compilation failed." }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "$test failed ($LASTEXITCODE checks)." }
}
