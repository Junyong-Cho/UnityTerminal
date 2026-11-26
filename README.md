# 유니티에서 사용할 수 있는 터미널

`https://github.com/Junyong-Cho/UnityTerminal.git?path=/Packages/com.catch.unityterminal`

Unity Package Manager의 **Add package from git URL**에 위 주소를 입력하고, **Window → Terminal**에서 엽니다. 셸 메뉴는 Windows PowerShell 5.1과 PowerShell 7을 제공합니다.

**검증 환경:** Unity 6000.3.28f1, Windows 11. `package.json`에 적힌 최소 버전(2020.3)은 사용 API가 존재하는지만 확인했으며, 한글 입력과 재컴파일 후 세션 복원 같은 동작은 해당 버전에서 아직 검사하지 않았습니다.

셸 메뉴에서 **Windows PowerShell 5.1**과 **PowerShell 7**을 구분해 선택할 수 있습니다. 창을 열 때 PowerShell 7을 확인하며, 실제로 실행 가능한 7 버전이 있을 때만 선택지가 표시됩니다. 저장된 선택이 없으면 **7을 우선 사용하고, 없으면 5.1로 시작**합니다. 직접 선택한 셸은 유지하며, 저장된 7 버전을 찾지 못하면 해당 실행에서는 5.1로 복귀합니다. 일반 명령과 Codex 실행 모두 선택한 버전을 사용합니다. 설치 상태가 바뀌면 터미널 창을 다시 열어 갱신합니다.

CMD·WSL 전용 메뉴는 제공하지 않습니다. 이전에 저장한 선택이 CMD 또는 WSL이면 설치된 PowerShell 7, 없으면 5.1로 시작합니다. WSL과 배포판이 설치되어 있다면 PowerShell 입력란에서 `wsl` 또는 `wsl -d Ubuntu`를 실행합니다. 같은 창이 Linux 대화형 화면으로 전환되며 `exit`하면 기존 PowerShell 화면으로 돌아옵니다. Linux의 경로·Tab 완성·히스토리는 Linux 셸이 처리합니다. `wsl --list`, `wsl --status`, `wsl uname -a` 같은 조회·단발성 명령은 기본 PowerShell 화면에 결과를 표시합니다. `wsl -e bash`, `wsl -e codex`처럼 알려진 셸이나 Codex를 직접 실행하는 경우에도 대화형 화면을 사용합니다. 변수·파이프라인·리디렉션을 포함한 PowerShell 식은 기본 셸에서 그대로 처리합니다.

일반 입력란과 대화형 입력란은 포커스가 있을 때 한글 IME를 활성화하며, 포커스를 잃으면 이전 IME 설정을 복원합니다. 조합 중의 방향키·Backspace·Tab·Enter는 조합 편집에 사용하고, 조합을 확정하는 Enter는 명령 제출로 전달하지 않습니다. 조합 확정 후 Enter를 한 번 더 누르면 명령을 제출합니다.

일반 PowerShell 입력란에서 단독으로 입력한 `clear`·`cls`·`Clear-Host`는 Unity 출력 기록을 지웁니다. 콘솔 핸들이 없는 리디렉션 셸에서 `Clear-Host`가 커서 위치를 바꾸며 발생시키는 예외를 피하도록 처리하며, 실행 중인 셸과 변수는 유지합니다. WSL 같은 대화형 화면 안의 명령은 해당 앱이 처리합니다.

Codex CLI가 설치되어 선택한 셸에서 실행 가능하다면, 기존 명령 입력란에 `codex`를 입력합니다. 같은 창이 대화형 화면으로 전환되고 종료 후 원래 명령 입력란으로 돌아옵니다. `codex resume`, `codex login`, `codex --help`도 같은 방식으로 실행합니다. 로그인과 승인은 Codex 화면에서 직접 진행합니다. 대화형 실행에는 Windows 10 1809 이상이 필요합니다.

다른 프로그램도 입력란 옆의 **Interactive** 버튼이나 **Ctrl+Enter**로 대화형 실행할 수 있습니다. 예를 들어 설치된 `claude`, `python -i` 또는 인수를 포함한 실행 경로를 입력한 뒤 이 방법으로 실행하면 됩니다. 특정 프로그램 이름을 추가 등록할 필요가 없으며, 입력한 PowerShell 명령과 따옴표·인수를 그대로 전달합니다. 종료하거나 `Stop`을 누르면 기존 PowerShell로 돌아옵니다. 이 선택은 한 번의 실행에만 적용되므로 다음 명령은 다시 일반 **Enter**로 실행할 수 있습니다. 일반 Enter에서 Codex·WSL을 자동 전환하는 기존 동작도 유지됩니다.

기본 입력란에서는 **Shift+Enter**로 줄을 바꾸고, **Enter**로 작성한 명령 전체를 실행합니다. `if`·함수·반복문·here-string처럼 여러 줄로 이어지는 블록도 Enter 한 번으로 실행되며, 블록 안의 빈 줄은 그대로 유지됩니다. 블록에서 정의한 변수와 함수는 이후 명령에서도 사용할 수 있습니다. **Ctrl+Enter**는 여러 줄 내용도 대화형으로 실행합니다. 입력란 높이는 내용에 맞춰 늘어나며, 여러 줄을 작성하는 동안 위·아래 방향키는 줄 사이에서 커서를 이동합니다. 한 줄 입력의 위·아래 히스토리는 기존대로 동작합니다. 한글 조합을 확정하는 Enter는 줄바꿈이나 명령 실행을 겸하지 않습니다. 대화형 화면에서도 Shift+Enter와 일반 Enter를 구분해 앱에 전달하며, 실제 동작은 앱의 키 설정을 따릅니다.

대화형 화면은 기본 셸 화면과 분리됩니다. 실행 중에는 기존 셸 프로세스와 기본 출력 버퍼를 유지하며, 종료하거나 `Stop`을 누르면 기존 로그·스크롤·입력 커서·선택 상태로 복귀합니다. 대화형 화면의 출력과 정상 종료 메시지는 기본 로그에 합치지 않습니다. 입력란에서 실행한 대화형 명령은 소비되므로 복귀 시 빈 프롬프트가 표시됩니다.

대화형 화면은 방향키, Tab, Enter, Esc, Ctrl+C, Ctrl+V 붙여넣기, Ctrl+Shift+C 복사와 창 크기 변경을 전달합니다. 마우스로 텍스트를 드래그해 선택하거나 우클릭 메뉴로 복사·붙여넣기할 수 있습니다. 선택한 글은 앱이 출력을 갱신해도 유지되며, 선택이 있을 때 **Ctrl+C**는 복사하고 선택이 없으면 앱에 중단 신호를 보냅니다. 선택 중에는 선택 당시 화면을 표시하며, 새 입력이나 스크롤·다시 클릭으로 최신 화면으로 돌아갑니다. `Stop`은 현재 대화형 프로세스와 자식 프로세스를 종료합니다. 일반 명령 입력 모드의 Tab 경로 완성과 히스토리는 유지됩니다.

대화형 화면에서도 마우스 휠로 위·아래를 확인할 수 있습니다. WSL 셸처럼 일반 화면을 사용하는 앱에서는 저장된 출력 기록을 스크롤하며, `Shift+PageUp`·`Shift+PageDown`으로 페이지 이동, `Shift+Home`·`Shift+End`로 처음·최신 출력으로 이동합니다. 문자를 입력하면 최신 출력으로 돌아옵니다. Codex처럼 대체 화면을 사용하는 전체화면 앱에서는 휠을 `PageUp`·`PageDown`으로 전달하므로 앱이 관리하는 기록을 페이지 단위로 이동합니다. 작은 터치패드 움직임도 누적해서 처리합니다.

대화형 화면의 글자·선택 영역·커서·IME 위치는 같은 칸 좌표를 사용합니다. 글자를 가로로 늘리지 않고 고정폭 폰트로 표시하며, 동적 폰트와 표시 스타일은 Unity 재로드 후 다시 생성합니다. 폰트가 사라졌을 때 기본 가변폭 UI 폰트로 바뀌지 않도록 검사합니다. IME 조합 중에는 Unity의 조합 커서를 사용하고, 그 외에는 앱이 지정한 커서 모양·표시 상태·깜박임을 따릅니다. 세션 시작과 화면 크기 변경에서도 같은 칸 크기로 터미널 행·열 수를 계산합니다.

PowerShell과 Codex·WSL 세션은 별도의 숨김 호스트 프로세스에서 실행됩니다. Unity가 C# 코드를 컴파일하고 스크립트를 재로드하는 동안에도 실행과 출력 수집을 계속하며, 재로드 후 창이 같은 프로세스에 자동으로 다시 연결됩니다. 입력 중인 내용, 셸 변수, 화면·커서·스크롤 상태를 유지합니다. 대화형 앱을 종료하지 않아도 변경한 C# 코드가 적용됩니다.

처음 실행할 때 Windows 기본 제공 PowerShell과 .NET Framework 컴파일러로 호스트를 빌드하고 `Library/UnityTerminal/Host`에 캐시합니다. 별도 SDK 설치나 다운로드는 필요하지 않으며, 통신은 현재 Windows 사용자만 접근 가능한 로컬 명명된 파이프를 사용합니다. `Stop`은 현재 대화형 앱을 종료하고, `Restart`·셸 변경·터미널 창 닫기는 해당 창의 전체 세션을 종료합니다. Unity 종료 시에도 호스트와 프로세스 트리를 정리합니다. Unity를 완전히 종료한 뒤의 세션 복원은 지원하지 않습니다.

**호스트 빌드 제약:** 호스트는 `Host~/Build-Host.ps1`이 Windows PowerShell 5.1(`powershell.exe`)의 `Add-Type`으로 컴파일합니다. 터미널에서 PowerShell 7을 선택해도 빌드는 항상 5.1로 하며, 이 컴파일러는 **C# 5 문법까지만** 지원합니다. 따라서 호스트와 공유하는 `ShellSession.cs`, `ShellCatalog.cs`, `ConPtySession.cs`, `TerminalScreen.cs`, `TerminalBuffer.cs`, `AnsiParser.cs`, `TerminalHostProtocol.cs`와 `Host~/TerminalSessionHost.cs`에는 문자열 보간(`$"..."`), `?.`·`??=`, `nameof`, 식 본문 멤버, 패턴 매칭 같은 C# 6 이후 문법을 쓰면 안 됩니다. `async`/`await`는 사용할 수 있습니다. Unity 쪽 컴파일은 성공하고 호스트 빌드만 실패하므로, 공유 파일을 고친 뒤에는 터미널 창을 다시 열어 호스트가 새로 빌드되는지 확인합니다. 공유 파일을 추가하거나 빼면 `Build-Host.ps1`의 컴파일 목록과 `Editor/HostedTerminalSession.cs`의 캐시 해시 목록을 **함께** 수정합니다. 해시 목록에서 빠진 파일은 내용이 바뀌어도 캐시된 예전 호스트가 계속 쓰입니다.

실제 기능은 `Packages/com.catch.unityterminal/Editor`에 있습니다.

| 파일 | 역할 |
| --- | --- |
| `UnityTerminalWindow.cs` | Unity 창, 입력, 히스토리, 설정, 프레임별 출력 처리 |
| `HostedTerminalSession.cs` | 호스트 캐시와 셸·대화형 세션의 Unity 연결 |
| `TerminalHostConnection.cs` | 비동기 로컬 통신, 재연결, 미확인 입력의 중복 실행 방지 |
| `TerminalHostProtocol.cs` | 명령과 압축된 출력·VT 화면 상태의 직렬화 |
| `../Host~/TerminalSessionHost.cs` | Unity 재로드와 독립적인 프로세스 수명, 출력 수집과 파싱 |
| `ShellSession.cs` | 셸 프로세스, UTF-8 입출력, 세션별 출력 큐, 프로세스 트리 종료 |
| `ShellCatalog.cs` | 셸 선택 목록, PowerShell 7 비동기 탐색과 버전 확인, 공통 실행 파일 결정 |
| `TerminalBuffer.cs` | 출력 줄과 스타일 저장, 버퍼 제한, Rich Text 캐시 |
| `AnsiParser.cs` | 스트림별 ANSI 상태 유지, 색상과 스타일, CRLF 청크 분할 처리 |
| `PathCompletion.cs` | 커서 위치와 따옴표를 고려한 경로 완성, 프롬프트 경로 인식 |
| `ConPtySession.cs` | Windows 가상 콘솔, 비동기 입출력 큐, 크기 변경, 프로세스 정리 |
| `TerminalScreen.cs` | 고정 셀 VT 화면, 커서 이동, 대체 화면, 색상, 터미널 질의 응답 |
| `TerminalInput.cs` | Codex·WSL 대화형 명령 인식과 터미널 키·붙여넣기 인코딩 |
| `TerminalComposition.cs` | IME 조합 상태 추적과 조합 확정 키의 중복 전달 방지 |
| `InteractiveTerminalView.cs` | 대화형 화면 표시, 입력·IME 커밋, 선택과 복사 |

일반 출력 큐는 최대 256개 청크, ConPTY 출력 큐는 128개 청크로 제한하며, 가득 차면 읽기 스레드가 소비를 기다립니다. 호스트는 Unity 연결이 없는 동안에도 출력과 VT 화면을 갱신하며, 창에는 변경된 화면 상태를 압축해 전달합니다. 일반 표시 버퍼는 최대 2,000줄, 전체 262,144자, 한 줄 16,384자를 보관합니다. 대화형 화면의 스크롤 기록은 최대 512줄이며 Tab 후보는 최대 256개입니다.

프로젝트 루트에서 다음 명령으로 Unity UI 없이 핵심 코드의 회귀 테스트를 실행할 수 있습니다. Windows PowerShell과 기본 Windows 셸을 사용하며 별도 테스트 패키지는 필요하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-CoreTests.ps1
```

파서·버퍼·자동완성만 검사하려면 `-SkipShellTests`를 추가합니다. 전체 검사는 테스트용 셸과 자식 프로세스를 생성하고 종료하므로 `taskkill`을 사용할 수 있는 환경이 필요합니다. 테스트 파일은 `Tests~` 안에 생성한 임시 디렉터리에만 저장하고 검사 후 삭제합니다.

실제 IMGUI 입력 회귀 검사는 `Tests~/TerminalInputRegressionProbe.cs`를 프로젝트의 `Assets/Editor`에 복사한 뒤, 빈 파일 `Temp/RunTerminalInputProbe`를 만들고 Unity의 **Assets → Refresh**를 실행합니다. 임시 창에서 Tab 자동완성, 후보 순환, 이어 쓰기, 후보가 없는 Tab의 입력 영역과 선택 상태를 검사합니다. 결과는 `Temp/TerminalInputProbe.result.txt`에 저장하며 셸은 실행하지 않습니다. 검사 후 `Assets/Editor`에 복사한 스크립트를 삭제합니다.

한글 입력 검사는 `Tests~/TerminalImeRegressionProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalImeProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 기본 검사는 두 입력란의 한국어 문자 이벤트와 IME 활성화·복원을 확인합니다. 파일 내용이 `native`이면 Windows에 설치된 한국어 키보드로 실제 조합과 Enter 확정도 검사합니다. 격리된 임시 창에만 키를 입력하고 셸 명령은 실행하지 않습니다. 결과는 `Temp/TerminalImeProbe.result.txt`에 저장하며 검사 후 복사한 스크립트를 삭제합니다.

대화형 표시 검사는 `Tests~/TerminalLayoutRegressionProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalLayoutProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 실제 Unity 폰트와 입력 컨트롤로 긴 영문·한글 혼합 행의 끝, 커서와 IME 좌표, 마우스 칸 판정, 커서 모양, 글꼴 크기 변경과 창 크기 변경을 검사합니다. 결과는 `Temp/TerminalLayoutProbe.result.txt`에 저장하며 검사 후 복사한 스크립트를 삭제합니다.

폰트 재로드 검사는 `Tests~/TerminalFontReloadProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalFontReloadProbe`를 만든 뒤 실행합니다. 실제 스크립트 재로드를 한 번 요청합니다. 동적 폰트 소실과 캐시된 스타일의 폰트 소실을 복구하는지, 재로드 뒤에도 영문이 고정폭인지 검사한 후 공백 없는 `ls`와 한글 미리보기를 표시합니다. 확인 후 `Temp/TerminalFontReloadProbe.capture.done`을 만들면 검사 창이 닫히며 결과는 `Temp/TerminalFontReloadProbe.result.txt`에 저장됩니다.

프롬프트와 꺾쇠 표시 검사는 `Tests~/TerminalLiteralTextProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunLiteralTextProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 실제 Unity 폰트 렌더러에서 원문과 ANSI 서식 출력의 표시 크기를 비교하며 결과는 `Temp/LiteralTextProbe.result.txt`에 저장합니다. 검사 후 복사한 스크립트를 삭제합니다.

대화형 화면과 Windows ConPTY 검사는 다음 명령으로 실행합니다. `-SkipNativeTests`는 화면·키 인코딩만 검사하며, `-CodexSmokeTest`는 설치된 Codex의 시작 화면까지 확인합니다. 모델에 작업 요청을 보내거나 로그인·프로젝트 신뢰 승인을 대신 진행하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-InteractiveTests.ps1
```

실제 Unity 창의 Codex 회귀 검사는 `Tests~/InteractiveTerminalGuiProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunInteractiveGuiProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 결과는 `Temp/InteractiveGuiProbe.result.txt`에 저장합니다. 읽기 전용 Codex 프롬프트에 문자열을 입력하지만 제출하지 않습니다. `Stop`과 자연 종료 후 기존 셸·로그·스크롤·입력 상태 복원, Enter로 실행한 명령의 소비도 검사합니다. 검사 후 복사한 스크립트를 삭제합니다.

공통 대화형 실행 검사는 `Tests~/GenericInteractiveSessionProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunGenericInteractiveProbe`를 만든 뒤 **Assets → Refresh**로 시작합니다. 별도 검사 창에서 실제 Ctrl+Enter·버튼 클릭, 긴 명령과 따옴표, 일반 PowerShell 실행 복귀, 스크립트 재로드 뒤 동일 프로세스·한글 변수 유지를 확인합니다. 결과는 `Temp/GenericInteractiveProbe.result.txt`에 저장하며 검사 후 복사한 스크립트를 삭제합니다. 설치된 Claude CLI의 시작 화면만 확인하려면 `powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-GenericCliTests.ps1`를 실행합니다. 이 검사는 작업 프롬프트를 보내거나 로그인·신뢰 승인을 진행하지 않습니다.

여러 줄 편집 검사는 `Tests~/TerminalMultilineRegressionProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalMultilineProbe`를 만든 뒤 **Assets → Refresh**로 시작합니다. 커서·선택 영역에서의 개행, 입력란 높이, 여러 줄 이동·자동완성, 대화형 Shift+Enter 전달을 확인하고 결과를 `Temp/TerminalMultilineProbe.result.txt`에 저장합니다. `InteractiveTerminalGuiProbe`는 실제 Codex 입력란의 줄바꿈도 제출 없이 확인합니다. Claude 입력란이 준비된 환경에서는 `Run-GenericCliTests.ps1 -CheckNewline`으로 빈 입력란의 줄바꿈만 검사할 수 있으며, 초기 설정·인증이 필요하면 입력 검사를 생략합니다. 검사 후 복사한 스크립트를 삭제합니다.

대화형 드래그·복사 검사는 `Tests~/TerminalCopyRegressionProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalCopyProbe`를 만든 뒤 **Assets → Refresh**로 시작합니다. 별도 테스트 창에서 한글·여러 줄 드래그, 출력 갱신 후 선택 유지, 입력 커서 영역의 드래그, 복사 단축키와 선택이 없을 때의 중단 신호를 검사합니다. 실제 CLI에는 입력을 보내지 않으며 결과는 `Temp/TerminalCopyProbe.result.txt`에 저장합니다. 검사 후 복사한 스크립트를 삭제합니다.

PowerShell 7이 설치된 환경에서는 `Tests~/Run-ShellSelectionTests.ps1`로 5.1과 7의 일반/ConPTY 실행 및 한글 입출력을 검사합니다. Unity의 Codex 회귀 검사도 7로 실행하려면 `Temp/RunInteractiveGuiProbe` 파일 내용에 `PowerShell7`을 기록합니다.

세션 유지와 로컬 호스트 검사는 `powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-HostTests.ps1`로 실행합니다. 연결이 끊긴 동안의 출력 수집, 같은 프로세스로 재연결, 화면·셸 변수 유지, 미확인 입력의 중복 방지와 종료 시 자식 프로세스 정리를 확인합니다. 임시 실행 파일은 프로젝트 `Temp` 안에 생성하고 검사 후 삭제합니다.

스크롤 검사는 `Tests~/TerminalScrollProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunTerminalScrollProbe`를 만든 뒤 **Assets → Refresh**로 시작합니다. 별도 창이나 키보드 입력 없이 출력 기록의 이동 방향과 경계, 터치패드 누적, 키보드 이동과 전체화면 앱의 페이지 입력을 검사합니다. 결과는 `Temp/TerminalScrollProbe.result.txt`에 기록하며, 검사 후 복사한 스크립트를 삭제합니다.

실제 Unity 컴파일 검사는 `Tests~/PersistentSessionReloadProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunPersistentSessionProbe`를 만든 뒤 **Assets → Refresh**로 시작합니다. Codex와 Ubuntu WSL에서 임시 C# 파일을 두 번 컴파일하고 프로세스 ID, 입력 중인 한글, PowerShell·Linux 변수가 유지되는지 확인합니다. Codex 입력은 제출하지 않습니다. 결과는 `Temp/PersistentSessionProbe.result.txt`에 저장하며, 검사 후 복사한 스크립트와 `Assets/Editor/TerminalCompileMarker.cs`를 삭제합니다.

설치된 Ubuntu에서 실제 WSL 검증은 `powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-WslTests.ps1`로 실행합니다. 다른 배포판은 `-Distribution 이름`으로 지정합니다. PowerShell에서 배포판 목록 출력, Linux 진입·프로젝트 경로·Tab 완성·한글 전달, 종료 후 기존 PowerShell 유지 여부를 확인합니다. Unity 화면에서 Codex에 이어 WSL 진입·종료도 검사하려면 `Temp/RunInteractiveGuiProbe` 내용에 `PowerShell7+WSL` 또는 `PowerShell+WSL`을 기록합니다. 이 GUI 검사는 Ubuntu를 사용합니다.

대화형 명령은 현재 폴더에서 별도 PowerShell 프로세스로 실행하므로, 일반 입력 모드 셸에서 정의한 임시 변수·함수는 전달되지 않습니다. 대화형 자동 전환 대상은 `codex`와 WSL 대화형 진입 명령입니다. 그 외 명령은 일반 Enter로 표준 스트림 실행, Interactive 버튼이나 Ctrl+Enter로 대화형 실행을 선택합니다. WSL에서 Codex·Claude를 실행하려면 해당 Linux 배포판에도 각 CLI가 설치되어 있어야 합니다. 일반 입력 모드의 현재 경로 추적은 기본 PowerShell 프롬프트 형식에 의존합니다.
