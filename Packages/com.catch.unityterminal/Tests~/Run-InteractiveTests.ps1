param([switch]$SkipNativeTests, [switch]$CodexSmokeTest)
$ErrorActionPreference = 'Stop'
$editorDirectory = Join-Path $PSScriptRoot '../Editor'
$sources = @('TerminalScreen.cs', 'TerminalInput.cs', 'ConPtySession.cs', 'ShellCatalog.cs') | ForEach-Object { Join-Path $editorDirectory $_ }
$sources += Join-Path $PSScriptRoot 'InteractiveTerminalTests.cs'
Add-Type -Path $sources -ErrorAction Stop
$fixture = Join-Path $PSScriptRoot ('NativeFixtures-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $probe = Join-Path $fixture 'ConsoleProbe.exe'
    if (!$SkipNativeTests) {
        Add-Type -Path (Join-Path $PSScriptRoot 'ConsoleProbe.cs') -OutputAssembly $probe -OutputType ConsoleApplication -ErrorAction Stop
    }
    [UnityTerminal.Tests.InteractiveTerminalTests]::Run($probe, !$SkipNativeTests, $CodexSmokeTest)
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if (!$resolvedFixture.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped Tests~.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
