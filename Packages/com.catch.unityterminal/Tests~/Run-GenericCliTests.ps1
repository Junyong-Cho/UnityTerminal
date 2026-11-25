$ErrorActionPreference = 'Stop'
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellCatalog.cs', 'ConPtySession.cs', 'TerminalScreen.cs') | ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot 'GenericCliTests.cs'
Add-Type -Path $sources -ErrorAction Stop
[UnityTerminal.Tests.GenericCliTests]::ClaudeStartup($PWD.Path)
