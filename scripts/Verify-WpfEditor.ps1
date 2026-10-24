param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDirectory = Join-Path $repositoryRoot 'src/SkyrimModPacker.UI'
$projectFile = Join-Path $projectDirectory 'SkyrimModPacker.UI.csproj'
$checkDirectory = Join-Path $repositoryRoot ('artifacts/audit/wpf-editor/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($checkDirectory) | Out-Null
$codeBehind = @(Get-ChildItem -LiteralPath $projectDirectory -Recurse -Filter '*.xaml.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

# Use fresh generated-output directories: an existing .g.cs must not hide a broken editor build.
foreach ($visualStudio in @('false', 'true')) {
    $intermediate = Join-Path $checkDirectory ('vs-' + $visualStudio)
    $arguments = @('msbuild', $projectFile, '-t:Compile', "-p:Configuration=$Configuration",
        '-p:DesignTimeBuild=true', '-p:BuildingProject=false', "-p:BuildingInsideVisualStudio=$visualStudio",
        '-p:BuildProjectReferences=false', '-p:SkipCompilerExecution=true', '-p:ProvideCommandLineArgs=true',
        "-p:IntermediateOutputPath=$intermediate\", '-getItem:Compile', '-verbosity:quiet')
    $result = & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Editor compilation failed (BuildingInsideVisualStudio=$visualStudio)." }
    $json = $result -join [Environment]::NewLine
    $json | Set-Content -LiteralPath (Join-Path $checkDirectory "vs-$visualStudio.json") -Encoding UTF8
    $report = $json | ConvertFrom-Json
    $inputs = @($report.Items.Compile)
    $duplicates = @($inputs | Group-Object FullPath | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw 'Duplicate compiler inputs in editor build.' }
    foreach ($file in $codeBehind) {
        $baseName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileNameWithoutExtension($file.Name))
        $generated = @($inputs | Where-Object {
            [IO.Path]::GetFileName($_.FullPath) -in @("$baseName.g.cs", "$baseName.g.i.cs")
        })
        if ($generated.Count -ne 1) { throw "Missing or duplicate generated partial class for $($file.Name)." }
        $path = $generated[0].FullPath
        if (-not $path.StartsWith($intermediate + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "A cached generated file was used for $($file.Name)."
        }
        if ((Get-Content -LiteralPath $path -Raw) -notmatch '\bvoid\s+InitializeComponent\s*\(') {
            throw "InitializeComponent is missing for $($file.Name)."
        }
    }
    Write-Output "PASS: $($codeBehind.Count) XAML partial classes; no duplicate inputs; BuildingInsideVisualStudio=$visualStudio."
}
Write-Output "Reports: $checkDirectory"
