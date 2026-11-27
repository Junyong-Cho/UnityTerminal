$ErrorActionPreference = 'Stop'
# Compiles the host source in-process (separate from Run-HostTests' assembly) and checks Capture Diagnostics answers.
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellSession.cs', 'ShellCatalog.cs', 'ConPtySession.cs', 'TerminalScreen.cs', 'TerminalBuffer.cs', 'AnsiParser.cs', 'TerminalHostProtocol.cs') |
    ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot '../Host~/TerminalSessionHost.cs'
$sources += Join-Path $PSScriptRoot 'HostDiagnosticsTests.cs'
Add-Type -Path $sources -ReferencedAssemblies System.dll,System.Core.dll,System.IO.Compression.dll
[UnityTerminal.Tests.HostDiagnosticsTests]::Run($PWD.Path)
[UnityTerminal.Tests.HostDiagnosticsTests]::ConPtyProgress()
