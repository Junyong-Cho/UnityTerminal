param([switch]$SkipShellTests)
$ErrorActionPreference = 'Stop'
$editorDirectory = Join-Path $PSScriptRoot '../Editor'
$sources = @('TerminalBuffer.cs', 'AnsiParser.cs', 'PathCompletion.cs', 'ShellSession.cs') |
    ForEach-Object { Join-Path $editorDirectory $_ }
$sources += Join-Path $PSScriptRoot 'TerminalCoreTests.cs'
Add-Type -Path $sources -ErrorAction Stop
$fixtureDirectory = Join-Path $PSScriptRoot ('Fixtures-' + [Guid]::NewGuid().ToString('N'))
try {
    [UnityTerminal.Tests.TerminalCoreTests]::Run($fixtureDirectory, !$SkipShellTests)
}
finally {
    # Delete only the exact fixture directory created for this run, inside Tests~.
    $fixtureRoot = [IO.Path]::GetFullPath($fixtureDirectory)
    $allowedRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if (!$fixtureRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture path escaped the test directory.'
    }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
