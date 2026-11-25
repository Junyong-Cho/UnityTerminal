param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$editor = Join-Path $PSScriptRoot '../Editor'
$sources = @('ShellSession.cs', 'ShellCatalog.cs', 'ConPtySession.cs', 'TerminalScreen.cs', 'TerminalBuffer.cs', 'AnsiParser.cs', 'TerminalHostProtocol.cs') |
    ForEach-Object { Join-Path $editor $_ }
$sources += Join-Path $PSScriptRoot 'TerminalSessionHost.cs'
$directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$lockName = 'Local\UnityTerminalBuild-' + [IO.Path]::GetFileName($directory)
$buildMutex = New-Object Threading.Mutex($false, $lockName)
$locked = $false
try {
    try { $locked = $buildMutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Terminal host build timed out.' }
    if (Test-Path -LiteralPath $OutputPath) { exit 0 }
    $temporary = Join-Path $directory ('host-' + [Guid]::NewGuid().ToString('N') + '.exe')
    Add-Type -Path $sources -OutputAssembly $temporary -OutputType ConsoleApplication -ReferencedAssemblies System.dll,System.Core.dll,System.IO.Compression.dll -ErrorAction Stop
    Move-Item -LiteralPath $temporary -Destination $OutputPath
}
finally {
    if ($locked) { $buildMutex.ReleaseMutex() }
    $buildMutex.Dispose()
}
