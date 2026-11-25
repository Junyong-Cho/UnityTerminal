param([string]$Distribution = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellCatalog.cs', 'ShellSession.cs', 'ConPtySession.cs', 'TerminalScreen.cs', 'TerminalInput.cs') | ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot 'WslTerminalTests.cs'
Add-Type -Path $sources -ErrorAction Stop
[UnityTerminal.Tests.WslTerminalTests]::Run($PWD.Path, $Distribution)
