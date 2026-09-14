param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo 'bin\clipboard-verification'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$project = Join-Path $work 'ClipboardProbe.csproj'
$app = [System.Security.SecurityElement]::Escape((Join-Path $repo 'src\IntraDrop\IntraDrop.csproj'))
$source = [System.Security.SecurityElement]::Escape((Join-Path $repo 'tests\IntraDrop.Tests\Fixtures\ClipboardCompatibilityProbe.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType><TargetFrameworks>net8.0-windows;net48</TargetFrameworks>
    <UseWindowsForms>true</UseWindowsForms><LangVersion>12.0</LangVersion><ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>IntraDrop.Tests</AssemblyName>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$app" /><Compile Include="$source" /></ItemGroup>
  <ItemGroup Condition="'`$(TargetFramework)' == 'net48'"><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8
if (!$SkipBuild) {
    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Clipboard probe build failed' }
}
foreach ($pair in @(@('net48','net8.0-windows'), @('net8.0-windows','net48'))) {
    $root = Join-Path $work ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $pair[0] + '-to-' + $pair[1])
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $listener = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $receiverPath = Join-Path $work ('bin\Release\' + $pair[1] + '\IntraDrop.Tests.exe')
    $senderPath = Join-Path $work ('bin\Release\' + $pair[0] + '\IntraDrop.Tests.exe')
    $receiver = Start-Process -FilePath $receiverPath -ArgumentList @('receive', ('"' + $root + '"'), $port) -PassThru -WindowStyle Hidden
    try {
        $deadline = (Get-Date).AddSeconds(15)
        while (!(Test-Path -LiteralPath (Join-Path $root 'ready'))) {
            if ($receiver.HasExited -or (Get-Date) -gt $deadline) { throw "Receiver did not start: $root" }
            Start-Sleep -Milliseconds 100
        }
        $sender = Start-Process -FilePath $senderPath -ArgumentList @('send', ('"' + $root + '"'), $port) -PassThru -WindowStyle Hidden
        try {
            if (!$sender.WaitForExit(45000)) { throw "Sender timeout: $root" }
            if ($sender.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $root 'success'))) { throw "Clipboard check failed: $root" }
            $receipts = Get-Content -LiteralPath (Join-Path $root 'receipts.txt')
            if (($receipts -join ',') -ne 'text,png,files') { throw "Unexpected receipts: $root" }
            Write-Output ($pair[0] + ' -> ' + $pair[1] + ': ' + (Get-Content -LiteralPath (Join-Path $root 'success')))
        }
        finally { if (!$sender.HasExited) { $sender.Kill() } }
    }
    finally {
        'done' | Set-Content -LiteralPath (Join-Path $root 'stop')
        if (!$receiver.WaitForExit(5000)) { $receiver.Kill() }
    }
}
