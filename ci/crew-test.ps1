# Builds Capri's crew on its own with stand-ins and runs a real terminal job through Windows PowerShell.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$out = Join-Path $root 'out\crew-test'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe = Join-Path $out 'CrewTest.exe'
& $compiler /nologo /langversion:5 /target:exe "/out:$exe" /reference:System.Runtime.Serialization.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $root 'app\Crew.cs') (Join-Path $PSScriptRoot 'crew-test\Stubs.cs') (Join-Path $PSScriptRoot 'crew-test\CrewTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Crew test compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw "Crew test failed ($LASTEXITCODE checks)." }
