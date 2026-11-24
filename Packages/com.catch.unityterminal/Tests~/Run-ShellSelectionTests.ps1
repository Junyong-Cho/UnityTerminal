$ErrorActionPreference = 'Stop'
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellCatalog.cs', 'ShellSession.cs', 'ConPtySession.cs', 'TerminalScreen.cs') | ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot 'ShellSelectionTests.cs'
Add-Type -Path $sources -ErrorAction Stop
[UnityTerminal.Tests.ShellSelectionTests]::Run($PWD.Path)
