# VM Session Guard 1.0.0

Windows VM에서 승인된 장시간 작업이 낮은 CPU 사용률 때문에 유휴 세션으로 판정되는 상황을 완화하는 콘솔 유틸리티입니다. 전체 CPU 사용률이 설정 임계값 미만으로 일정 시간 유지될 때 Windows `SetThreadExecutionState`를 한 번 호출해 시스템/디스플레이 유휴 타이머를 갱신합니다. 옵션을 명시한 경우에만 Scroll Lock 두 번 토글 또는 마우스 1픽셀 이동 후 즉시 복귀를 추가로 수행합니다.

## 다운로드

[최신 Windows x64 배포 패키지 다운로드](https://github.com/2KangHo/vm-session-guard/releases/latest)

Releases 페이지에서 `VM-Session-Guard-1.0.0-win-x64.zip`을 받아 압축을 푸세요. SHA-256 체크섬 파일도 같은 릴리스에 첨부되어 있습니다.

> **중요 — 사내 정책 및 관리자 승인**
>
> 이 프로그램은 기술적으로 유휴 정책을 우회하거나 완화할 수 있습니다. 설치·실행·시작프로그램 등록 전에 반드시 회사의 보안 정책, VM 이용 정책, IT/보안 관리자의 승인을 확인하세요. 승인이 없거나 정책에서 금지된 환경에서는 사용하지 마세요. 도메인/VDI 서버가 강제로 연결을 종료하는 정책은 이 프로그램으로 막을 수 없습니다.

## 구성품

- `VmSessionGuard.exe`: .NET 8 포함 Windows x64 단일 실행 파일
- `VmSessionGuard.json`: 설정 파일
- `install-startup.ps1`, `uninstall-startup.ps1`: 현재 사용자 시작프로그램 등록/해제
- `src/`: 전체 소스와 프로젝트 파일

관리자 권한은 필요하지 않습니다. Windows 10/11 또는 Windows Server 2016 이상 x64를 대상으로 합니다.

## 실행과 종료

1. ZIP을 쓰기 가능한 고정 폴더에 풉니다. 예: `%LOCALAPPDATA%\VmSessionGuard`
2. `VmSessionGuard.json`을 검토합니다.
3. PowerShell 또는 명령 프롬프트에서 `VmSessionGuard.exe`를 실행합니다.
4. 종료하려면 콘솔에서 `Ctrl+C`를 누르거나 콘솔 창을 닫습니다. 작업 관리자에서 `VmSessionGuard.exe`를 종료해도 됩니다.

다른 설정 파일을 쓰려면:

```powershell
.\VmSessionGuard.exe --config "C:\path\custom.json"
```

## 설정

```json
{
  "CpuThresholdPercent": 10.0,
  "LowCpuDurationSeconds": 60,
  "CheckIntervalSeconds": 5,
  "Fallback": "None",
  "LogFile": "logs/VmSessionGuard.log",
  "LogCpuSamples": false
}
```

- `CpuThresholdPercent`: 전체 CPU 임계값, 0–100. 기본 10.
- `LowCpuDurationSeconds`: 임계값 미만이 지속되어야 하는 시간, 1–86400초. 기본 60.
- `CheckIntervalSeconds`: CPU 확인 주기, 1–3600초. 기본 5.
- `Fallback`: `None`, `ScrollLock`, `MousePixel` 중 하나. 기본 및 권장값은 `None`.
- `LogFile`: 절대 경로 또는 EXE 기준 상대 경로. 환경 변수(예: `%LOCALAPPDATA%`) 사용 가능.
- `LogCpuSamples`: 모든 CPU 샘플을 기록할지 여부. 기본 `false`; 디버깅 시에만 `true` 권장.

낮은 CPU 상태가 계속되면 `LowCpuDurationSeconds` 간격으로 keep-alive를 다시 수행합니다. 첫 CPU 샘플은 비교 기준을 만드는 데 사용되므로 실제 첫 동작에는 체크 주기만큼의 추가 시간이 걸릴 수 있습니다.

### fallback 주의사항

- `None`: 사용자 입력을 만들지 않고 `SetThreadExecutionState`만 호출합니다. 먼저 이 설정을 사용하세요.
- `ScrollLock`: `SendInput`으로 Scroll Lock 키 down/up을 두 번 보내 원래 토글 상태로 복원합니다. 일부 앱이 키 이벤트를 감지할 수 있습니다.
- `MousePixel`: 상대 이동 +1픽셀과 -1픽셀을 연속 전송합니다. 포인터는 원래 위치로 돌아오지만 일부 앱이 마우스 이동을 감지할 수 있습니다.

fallback은 `SetThreadExecutionState`만으로 사내 세션 유휴 정책이 갱신되지 않고, 관리자가 해당 입력 방식을 승인한 경우에만 사용하세요. 잠금 화면을 해제하거나 로그인 입력을 자동화하지 않습니다.

## 로그

기본 로그는 EXE 폴더 아래 `logs\VmSessionGuard.log`에 append 방식으로 기록됩니다. 시작/종료, keep-alive 결과, API 오류가 기록됩니다. 파일 크기 제한이나 자동 삭제는 없으므로 조직의 보존 정책에 맞춰 주기적으로 보관/삭제하세요.

## 현재 사용자 시작프로그램 등록

관리자 승인을 받은 뒤, 배포 폴더의 PowerShell에서 다음을 실행합니다:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-startup.ps1
```

현재 사용자 Startup 폴더에 바로가기를 만들며 관리자 권한이 필요하지 않습니다. 해제:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall-startup.ps1
```

회사에서 PowerShell 실행 정책 또는 시작프로그램을 관리한다면 IT 관리자가 승인한 배포 도구를 사용하세요. 실행 중 콘솔 창이 표시되는 것은 의도된 동작이며, 상태와 종료 방법을 투명하게 유지하기 위해 콘솔 앱을 선택했습니다.

## 빌드

.NET 8 SDK가 설치된 환경에서:

```powershell
dotnet publish .\src\VmSessionGuard.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

외부 NuGet 패키지는 사용하지 않습니다. 단일 EXE에는 .NET 런타임이 포함됩니다. Windows ARM64 또는 x86이 필요하면 각각 `win-arm64`, `win-x86`으로 별도 빌드하세요.

## 동작 및 한계

- CPU 사용률은 `GetSystemTimes` 두 샘플의 차이로 계산하며 관리자 권한이나 성능 카운터 접근 권한이 필요 없습니다.
- `SetThreadExecutionState`는 호출 스레드가 시스템/디스플레이 유휴 타이머를 갱신하도록 요청할 뿐, 조직의 서버 측 세션 만료·최대 접속 시간·강제 로그오프 정책을 변경하지 않습니다.
- 보안 소프트웨어가 서명되지 않은 사내 도구 실행을 차단할 수 있습니다. 배포 전 조직의 코드 서명 및 보안 검사 절차를 따르세요.
- 중요 작업은 이 도구 하나에만 의존하지 말고 체크포인트, 재시작 가능한 작업 설계, 작업 스케줄러/서비스 등 조직이 승인한 실행 방식을 우선 검토하세요.
