# 유니티에서 사용할 수 있는 터미널

`https://github.com/Junyong-Cho/UnityTerminal.git?path=/Packages/com.catch.unityterminal`

Unity Package Manager의 **Add package from git URL**에 위 주소를 입력하고, **Window → Terminal**에서 엽니다. 현재 셸 실행은 Windows의 PowerShell, CMD, WSL을 지원합니다.


실제 기능은 `Packages/com.catch.unityterminal/Editor`에 있습니다.

| 파일 | 역할 |
| --- | --- |
| `UnityTerminalWindow.cs` | Unity 창, 입력, 히스토리, 설정, 프레임별 출력 처리 |
| `ShellSession.cs` | 셸 프로세스, UTF-8 입출력, 세션별 출력 큐, 프로세스 트리 종료 |
| `TerminalBuffer.cs` | 출력 줄과 스타일 저장, 버퍼 제한, Rich Text 캐시 |
| `AnsiParser.cs` | 스트림별 ANSI 상태 유지, 색상과 스타일, CRLF 청크 분할 처리 |
| `PathCompletion.cs` | 커서 위치와 따옴표를 고려한 경로 완성, 프롬프트 경로 인식 |

출력 큐는 최대 256개 청크로 제한하며, 가득 차면 읽기 스레드가 소비를 기다립니다. 창은 업데이트마다 최대 64 KiB 또는 약 6 ms의 출력을 처리합니다. 표시 버퍼는 최대 2,000줄, 전체 262,144자, 한 줄 16,384자를 보관합니다. Tab 후보는 최대 256개입니다.

프로젝트 루트에서 다음 명령으로 Unity UI 없이 핵심 코드의 회귀 테스트를 실행할 수 있습니다. Windows PowerShell과 기본 Windows 셸을 사용하며 별도 테스트 패키지는 필요하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./Packages/com.catch.unityterminal/Tests~/Run-CoreTests.ps1
```

파서·버퍼·자동완성만 검사하려면 `-SkipShellTests`를 추가합니다. 전체 검사는 테스트용 셸과 자식 프로세스를 생성하고 종료하므로 `taskkill`을 사용할 수 있는 환경이 필요합니다. 테스트 파일은 `Tests~` 안에 생성한 임시 디렉터리에만 저장하고 검사 후 삭제합니다.

실제 IMGUI 입력 회귀 검사는 `Tests~/TerminalInputRegressionProbe.cs`를 프로젝트의 `Assets/Editor`에 복사한 뒤, 빈 파일 `Temp/RunTerminalInputProbe`를 만들고 Unity의 **Assets → Refresh**를 실행합니다. 임시 창에서 Tab 자동완성, 후보 순환, 이어 쓰기, 후보가 없는 Tab의 입력 영역과 선택 상태를 검사합니다. 결과는 `Temp/TerminalInputProbe.result.txt`에 저장하며 셸은 실행하지 않습니다. 검사 후 `Assets/Editor`에 복사한 스크립트를 삭제합니다.

프롬프트와 꺾쇠 표시 검사는 `Tests~/TerminalLiteralTextProbe.cs`를 `Assets/Editor`에 복사하고 `Temp/RunLiteralTextProbe` 파일을 만든 뒤 **Assets → Refresh**를 실행합니다. 실제 Unity 폰트 렌더러에서 원문과 ANSI 서식 출력의 표시 크기를 비교하며 결과는 `Temp/LiteralTextProbe.result.txt`에 저장합니다. 검사 후 복사한 스크립트를 삭제합니다.

WSL 경로 완성은 Windows에서 접근 가능한 `/mnt/<드라이브>/...` 경로를 기준으로 하며, 현재 경로 추적은 기본 프롬프트 형식에 의존합니다.
