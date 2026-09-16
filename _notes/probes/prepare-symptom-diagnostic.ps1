param([Parameter(Mandatory=$true)][string]$Evidence)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repo '_notes/symptom-project'
$source = 'C:/VRC-File/kaguya'
$utf8 = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force -Path $project,(Join-Path $project 'Assets'),(Join-Path $project 'Packages'),$Evidence | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $project 'ProjectSettings'))) {
    Copy-Item -LiteralPath (Join-Path $source 'ProjectSettings') -Destination (Join-Path $project 'ProjectSettings') -Recurse
}
# Separate project, package copies, Library, output directory and lock.
Copy-Item -LiteralPath (Join-Path $repo '_clean-proj/Packages/manifest.json') -Destination (Join-Path $project 'Packages/manifest.json') -Force
foreach ($package in @('jp.lilxyzw.shadercore','jp.lilxyzw.liltoon','com.vrchat.base','com.vrchat.avatars','nadena.dev.ndmf','nadena.dev.modular-avatar')) {
    $dest = Join-Path $project ('Packages/' + $package)
    if (!(Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath (Join-Path $source ('Packages/' + $package)) -Destination $dest -Recurse }
}
foreach ($pair in @(@('NonToon','com.catandling.nontoon'),@('nontoon-converter','com.catandling.nontoon-converter'))) {
    $dest = [IO.Path]::GetFullPath((Join-Path $project ('Packages/' + $pair[1])))
    if (!$dest.StartsWith($project + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe destination' }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $repo $pair[0]) -Destination $dest -Recurse
}
$avatar = Join-Path $project 'Assets/SymptomKaguya'
if (!(Test-Path -LiteralPath $avatar)) { Copy-Item -LiteralPath (Join-Path $source ('Assets/' + [char]0x8f89 + [char]0x591c + '/kaguya')) -Destination $avatar -Recurse }
foreach ($probe in @('NTSymptomDiagnostic.cs','NTConverterProbe.cs')) {
    $dest = Join-Path $project ('Packages/com.catandling.nontoon-converter/Editor/' + $probe)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $probe) -Destination $dest -Force
    if (!(Test-Path -LiteralPath $dest) -or (Get-FileHash -LiteralPath $dest).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $probe)).Hash) { throw "Probe deployment failed: $probe" }
    Write-Output "DEPLOY_CHECK $probe exists and SHA256 matches"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NTSymptomGeometry.shader') -Destination (Join-Path $project 'Assets/NTSymptomGeometry.shader') -Force
$settings = Join-Path $project 'ProjectSettings/ProjectSettings.asset'
[IO.File]::WriteAllText($settings,([IO.File]::ReadAllText($settings) -replace 'm_ActiveColorSpace: 0','m_ActiveColorSpace: 1'),$utf8)
# Freeze input hashes, including the current Shade copy; never write to repository implementation.
$rows = foreach ($base in @('Packages/com.catandling.nontoon','Packages/com.catandling.nontoon-converter','Packages/jp.lilxyzw.shadercore','Packages/jp.lilxyzw.liltoon','Assets/SymptomKaguya')) {
    Get-ChildItem -LiteralPath (Join-Path $project $base) -File -Recurse | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash + '  ' + $_.FullName.Substring($project.Length + 1) }
}
[IO.File]::WriteAllLines((Join-Path $Evidence 'input-sha256.txt'),$rows,$utf8)
Write-Output "Prepared isolated diagnostic project: $project"
