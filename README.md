# 유니티에서 사용할 수 있는 터미널

`https://github.com/Junyong-Cho/UnityTerminal.git?path=/Packages/com.catch.unityterminal`

Unity Package Manager의 **Add package from git URL**에 위 주소를 입력하고, **Window → Terminal**에서 엽니다. 현재 셸 실행은 Windows의 PowerShell, CMD, WSL을 지원합니다.

셸 메뉴에서 **Windows PowerShell 5.1**과 **PowerShell 7**을 구분해 선택할 수 있습니다. 창을 열 때 PowerShell 7을 확인하며, 실제로 실행 가능한 7 버전이 있을 때만 선택지가 표시됩니다. 저장된 선택이 없으면 **7을 우선 사용하고, 없으면 5.1로 시작**합니다. 직접 선택한 셸은 유지하며, 저장된 7 버전을 찾지 못하면 해당 실행에서는 5.1로 복귀합니다. 일반 명령과 Codex 실행 모두 선택한 버전을 사용합니다. 설치 상태가 바뀌면 터미널 창을 다시 열어 갱신합니다.

Codex CLI가 설치되어 선택한 셸에서 실행 가능하다면, 기존 명령 입력란에 `codex`를 입력합니다. 같은 창이 대화형 화면으로 전환되고 종료 후 원래 명령 입력란으로 돌아옵니다. `codex resume`, `codex login`, `codex --help`도 같은 방식으로 실행합니다. 로그인과 승인은 Codex 화면에서 직접 진행합니다. 대화형 실행에는 Windows 10 1809 이상이 필요합니다.

대화형 화면은 기본 셸 화면과 분리됩니다. 실행 중에는 기존 셸 프로세스와 기본 출력 버퍼를 유지하며, 종료하거나 `Stop`을 누르면 기존 로그·스크롤·입력 커서·선택 상태로 복귀합니다. Codex 화면의 출력과 정상 종료 메시지는 기본 로그에 합치지 않습니다. 입력란에서 Enter로 실행한 `codex` 명령은 소비되므로 복귀 시 빈 프롬프트가 표시됩니다.

대화형 화면은 방향키, Tab, Enter, Esc, Ctrl+C, Ctrl+V 붙여넣기, Ctrl+Shift+C 복사와 창 크기 변경을 전달합니다. 마우스로 텍스트를 드래그해 선택하거나 우클릭 메뉴로 복사·붙여넣기할 수 있습니다. `Stop`은 현재 대화형 프로세스와 자식 프로세스를 종료합니다. 일반 명령 입력 모드의 Tab 경로 완성과 히스토리는 유지됩니다.

Codex가 C# 코드를 수정하는 중에 세션이 끊기지 않도록, 대화형 실행 중에는 Unity 스크립트 재로드를 대기시킵니다. 변경한 C# 코드의 재로드는 Codex를 종료하거나 `Stop`을 누른 뒤 적용됩니다.

실제 기능은 `Packages/com.catch.unityterminal/Editor`에 있습니다.

| 파일 | 역할 |
| --- | --- |
| `UnityTerminalWindow.cs` | Unity 창, 입력, 히스토리, 설정, 프레임별 출력 처리 |
| `ShellSession.cs` | 셸 프로세스, UTF-8 입출력, 세션별 출력 큐, 프로세스 트리 종료 |
| `ShellCatalog.cs` | 셸 선택 목록, PowerShell 7 비동기 탐색과 버전 확인, 공통 실행 파일 결정 |
| `TerminalBuffer.cs` | 출력 줄과 스타일 저장, 버퍼 제한, Rich Text 캐시 |
| `AnsiParser.cs` | 스트림별 ANSI 상태 유지, 색상과 스타일, CRLF 청크 분할 처리 |
| `PathCompletion.cs` | 커서 위치와 따옴표를 고려한 경로 완성, 프롬프트 경로 인식 |
| `ConPtySession.cs` | Windows 가상 콘솔, 비동기 입출력 큐, 크기 변경, 프로세스 정리 |
| `TerminalScreen.cs` | 고정 셀 VT 화면, 커서 이동, 대체 화면, 색상, 터미널 질의 응답 |
| `TerminalInput.cs` | Codex 명령 인식과 터미널 키·붙여넣기 인코딩 |
| `InteractiveTerminalView.cs` | 대화형 화면 표시, 입력·IME 커밋, 선택과 복사 |

출력 큐는 최대 256개 청크로 제한하며, 가득 차면 읽기 스레드가 소비를 기다립니다. 창은 업데이트마다 최대 64 KiB 또는 약 6 ms의 출력을 처리합니다. 표시 버퍼는 최대 2,000줄, 전체 262,144자, 한 줄 16,384자를 보관합니다. Tab 후보는 최대 256개입니다.

프로젝트 루트에서 다음 명령으로 Unity UI 없이 핵심 코드의 회귀 테스트를 실행할 수 있습니다. Windows PowerShell과 기본 Windows 셸을 사용하며 별도 테스트 패키지는 필요하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-CoreTests.ps1
```

파서·버퍼·자동완성만 검사하려면 `-SkipShellTests`를 추가합니다. 전체 검사는 테스트용 셸과 자식 프로세스를 생성하고 종료하므로 `taskkill`을 사용할 수 있는 환경이 필요합니다. 테스트 파일은 `Tests~` 안에 생성한 임시 디렉터리에만 저장하고 검사 후 삭제합니다.

실제 IMGUI 입력 회귀 검사는 `Tests~/TerminalInputRegressionProbe.cs`를 프로젝트의 `Assets/Editor`에 복사한 뒤, 빈 파일 `Temp/RunTerminalInputProbe`를 만들고 Unity의 **Assets → Refresh**를 실행합니다. 임시 창에서 Tab 자동완성, 후보 순환, 이어 쓰기, 후보가 없는 Tab의 입력 영역과 선택 상태를 검사합니다. 결과는 `Temp/TerminalInputProbe.result.txt`에 저장하며 셸은 실행하지 않습니다. 검사 후 `Assets/Editor`에 복사한 스크립트를 삭제합니다.

프롬프트와 꺾쇠 표시 검사는 `Tests~/TerminalLiteralTextProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunLiteralTextProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 실제 Unity 폰트 렌더러에서 원문과 ANSI 서식 출력의 표시 크기를 비교하며 결과는 `Temp/LiteralTextProbe.result.txt`에 저장합니다. 검사 후 복사한 스크립트를 삭제합니다.

대화형 화면과 Windows ConPTY 검사는 다음 명령으로 실행합니다. `-SkipNativeTests`는 화면·키 인코딩만 검사하며, `-CodexSmokeTest`는 설치된 Codex의 시작 화면까지 확인합니다. 모델에 작업 요청을 보내거나 로그인·프로젝트 신뢰 승인을 대신 진행하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-InteractiveTests.ps1
```

실제 Unity 창의 Codex 회귀 검사는 `Tests~/InteractiveTerminalGuiProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunInteractiveGuiProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 결과는 `Temp/InteractiveGuiProbe.result.txt`에 저장합니다. 읽기 전용 Codex 프롬프트에 문자열을 입력하지만 제출하지 않습니다. `Stop`과 자연 종료 후 기존 셸·로그·스크롤·입력 상태 복원, Enter로 실행한 명령의 소비도 검사합니다. 검사 후 복사한 스크립트를 삭제합니다.

PowerShell 7이 설치된 환경에서는 `Tests~/Run-ShellSelectionTests.ps1`로 5.1과 7의 일반/ConPTY 실행 및 한글 입출력을 검사합니다. Unity의 Codex 회귀 검사도 7로 실행하려면 `Temp/RunInteractiveGuiProbe` 파일 내용에 `PowerShell7`을 기록합니다.

대화형 명령은 현재 폴더에서 별도 셸 프로세스로 실행하므로, 일반 입력 모드 셸에서 정의한 임시 변수·함수는 전달되지 않습니다. 대화형 자동 전환 대상은 `codex` 명령입니다. 다른 명령은 기존 표준 스트림 모드를 사용합니다. WSL 경로 완성은 Windows에서 접근 가능한 `/mnt/<드라이브>/...` 경로를 기준으로 하며, 일반 입력 모드의 현재 경로 추적은 기본 프롬프트 형식에 의존합니다.
