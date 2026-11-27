$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PWD ('Temp/TerminalHostTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $hostFile = Join-Path $fixture 'TerminalSessionHost.exe'
    & (Join-Path $PSScriptRoot '../Host~/Build-Host.ps1') -OutputPath $hostFile
    $probe = Join-Path $fixture 'ConsoleProbe.exe'
    Add-Type -Path (Join-Path $PSScriptRoot 'ConsoleProbe.cs') -OutputAssembly $probe -OutputType ConsoleApplication
    $editor = Join-Path $PSScriptRoot '../Editor'
    $sources = @('ShellCatalog.cs', 'ShellSession.cs', 'ConPtySession.cs', 'TerminalScreen.cs', 'TerminalBuffer.cs', 'AnsiParser.cs', 'TerminalHostProtocol.cs', 'TerminalHostConnection.cs') |
        ForEach-Object { Join-Path $editor $_ }
    $sources += Join-Path $PSScriptRoot 'HostPersistenceTests.cs'
    Add-Type -Path $sources -ReferencedAssemblies System.dll,System.Core.dll,System.IO.Compression.dll
    [UnityTerminal.Tests.HostPersistenceTests]::Run($hostFile, $probe, $PWD.Path)
    # In-process host checks run in their own PowerShell: they compile the host source as a second assembly.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Run-HostDiagnosticsTests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Host diagnostics tests failed' }
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath((Join-Path $PWD 'Temp')).TrimEnd('\') + '\'
    if (!$resolvedFixture.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped the project Temp directory.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
