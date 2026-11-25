param([switch]$CheckNewline)
$ErrorActionPreference = 'Stop'
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellCatalog.cs', 'ConPtySession.cs', 'TerminalScreen.cs', 'TerminalInput.cs') | ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot 'GenericCliTests.cs'
Add-Type -Path $sources -ErrorAction Stop
if ($CheckNewline) { [UnityTerminal.Tests.GenericCliTests]::ClaudeNewline($PWD.Path) }
else { [UnityTerminal.Tests.GenericCliTests]::ClaudeStartup($PWD.Path) }
