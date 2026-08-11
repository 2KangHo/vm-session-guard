# VM Session Guard 1.4.1

Windows VM에서 승인된 장시간 작업이 낮은 CPU 사용률 때문에 유휴 세션으로 판정되는 상황을 완화하는 콘솔 유틸리티입니다. 전체 CPU 사용률이 설정 임계값 미만으로 일정 시간 유지될 때 Windows `SetThreadExecutionState`를 한 번 호출해 시스템/디스플레이 유휴 타이머를 갱신합니다. 옵션을 명시한 경우에만 Scroll Lock 두 번 토글 또는 마우스 1픽셀 이동 후 즉시 복귀를 추가로 수행합니다.

## 다운로드

Releases 페이지에서 최신 `VM-Session-Guard-*-win-x64.zip`을 받아 압축을 푸세요. SHA-256 체크섬 파일도 같은 릴리스에 첨부되어 있습니다.

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
  "CpuThresholdPercent": 12.5,
  "LowCpuDurationSeconds": 90,
  "CheckIntervalSeconds": 10,
  "StartCpuFloorImmediately": true,
  "Fallbacks": [
    "MousePixel",
    "ScrollLock",
    "CpuFloor"
  ],
  "LogFile": "logs/VmSessionGuard.log",
  "LogCpuSamples": false
}
```

- `CpuThresholdPercent`: 전체 CPU 임계값, 0–100. 기본 12.5. `CpuFloor` 사용 시 1–25 범위가 허용됩니다.
- `LowCpuDurationSeconds`: 임계값 미만이 지속되어야 하는 시간, 1–86400초. 기본 90.
- `CheckIntervalSeconds`: CPU 확인 주기, 1–3600초. 기본 10.
- `StartCpuFloorImmediately`: `CpuFloor`가 선택된 경우 시작 직후 CPU floor와 입력 fallback을 실행할지 여부. 기본 `true`. `false`이면 낮은 CPU 상태가 `LowCpuDurationSeconds` 동안 지속된 뒤 시작합니다.
- `Fallbacks`: `None`, `ScrollLock`, `MousePixel`, `CpuFloor` 중 하나 이상을 배열로 지정합니다. 여러 모드를 지정하면 한 번의 keep-alive에서 배열 순서대로 모두 실행됩니다. 배포 설정의 기본값은 `["MousePixel", "ScrollLock", "CpuFloor"]`입니다.
- `Fallback`: 기존 단일 모드 설정과의 호환을 위해 계속 지원하지만, 새 설정에서는 `Fallbacks`를 사용하세요. `Fallback`과 `Fallbacks`를 동시에 지정할 수 없습니다.
- `LogFile`: 로그 파일의 기본 이름이며 절대 경로 또는 EXE 기준 상대 경로를 사용할 수 있습니다. 환경 변수(예: `%LOCALAPPDATA%`) 사용 가능. 실행할 때 `-yyyyMMdd-HHmmss-fff-pPID`가 확장자 앞에 붙어 실행마다 새 로그 파일이 생성됩니다.
- `LogCpuSamples`: 모든 CPU 샘플을 기록할지 여부. 기본 `false`; 디버깅 시에만 `true` 권장.

낮은 CPU 상태가 계속되면 `LowCpuDurationSeconds` 간격으로 keep-alive를 다시 수행합니다. `CpuFloor`가 활성화되면 CPU가 임계값 이상으로 회복되어도 입력 fallback을 같은 간격으로 계속 실행합니다. `StartCpuFloorImmediately`가 `true`이면 시작 직후 첫 keep-alive를 실행해 유휴 타이머가 먼저 만료되는 것을 방지합니다. `Fallbacks`에 여러 모드가 있으면 각 keep-alive 시 모든 모드가 순서대로 적용됩니다. `None`은 다른 모드와 함께 사용할 수 없습니다.

### fallback 주의사항

- `None`: 사용자 입력을 만들지 않고 `SetThreadExecutionState`만 호출합니다. 다른 모드와 조합할 수 없으며, 로컬 Windows 절전 방지에는 도움이 되지만 VM/VDI 서버 측 유휴 정책에는 반영되지 않을 수 있습니다.
- `ScrollLock`: 스캔 코드 기반 `SendInput`으로 Scroll Lock 키 down/up을 두 번, 간격을 두고 보내 원래 토글 상태로 복원합니다. 일부 앱이 키 이벤트를 감지할 수 있습니다.
- `MousePixel`: 입력 coalescing을 방지한 상대 이동 +4픽셀과 -4픽셀을 간격을 두고 전송합니다. 포인터는 원래 위치로 돌아오지만 일부 앱이 마우스 이동을 감지할 수 있습니다.
- `CpuFloor`: 기본적으로 시작 즉시 정상 우선순위 작업 스레드를 시작하고, 각 스레드에 최소 duty cycle을 적용해 전체 CPU 사용량이 `CpuThresholdPercent` 이상이 되도록 시도합니다. 이 모드에서는 임계값을 1–25%로 제한합니다. `StartCpuFloorImmediately=false`이면 기존처럼 저사용 지속시간 이후에 시작합니다.

fallback은 `SetThreadExecutionState`만으로 사내 세션 유휴 정책이 갱신되지 않고, 관리자가 해당 입력 방식을 승인한 경우에만 사용하세요. 잠금 화면을 해제하거나 로그인 입력을 자동화하지 않습니다.

### CPU floor 설정 예시

```json
{
  "CpuThresholdPercent": 12.5,
  "LowCpuDurationSeconds": 90,
  "CheckIntervalSeconds": 10,
  "Fallbacks": [
    "MousePixel",
    "ScrollLock",
    "CpuFloor"
  ],
  "LogFile": "logs/VmSessionGuard.log",
  "LogCpuSamples": false
}
```

위 예시는 시작 즉시 `CpuFloor`로 CPU 부하를 유지하면서 `MousePixel`과 `ScrollLock` 입력을 모두 보내고, 이후에도 90초 간격으로 세 가지 fallback을 반복하는 조합입니다. `CpuFloor`는 실제 계산 작업이 아니라 CPU 시간을 의도적으로 소비합니다. 각 작업 스레드는 정상 우선순위로 실행되며 최대 duty cycle을 50%로 제한합니다. 프로세서를 최대 32개까지만 사용하므로 논리 프로세서가 매우 많은 VM에서는 목표에 도달하지 못할 수 있으며 이 경우 로그에 경고합니다. CPU·전력·공유 VM 비용에 영향을 줄 수 있으므로 **IT/보안/인프라 관리자가 명시적으로 승인한 경우에만** 사용하세요.

## 로그

기본 로그는 EXE 폴더 아래 `logs\VmSessionGuard-yyyyMMdd-HHmmss-fff-pPID.log` 형식으로 실행마다 새로 생성됩니다. 시작 로그에는 적용된 `fallbacks` 목록이 기록되고, keep-alive 로그에는 각 모드의 성공/실패 결과가 기록됩니다. 파일 크기 제한이나 자동 삭제는 없으므로 조직의 보존 정책에 맞춰 주기적으로 보관/삭제하세요.

keep-alive 로그의 `localInputIdle`은 게스트 Windows의 마지막 입력 유휴 시간을 `전송 전->전송 후` 형식으로 기록합니다. `MousePixel` 또는 `ScrollLock` 후에도 값이 줄지 않으면 게스트가 합성 입력을 받지 않는 것이고, 값이 줄었는데도 세션이 종료되면 RDP/VDI 브로커의 서버 측 유휴·최대 세션 정책일 가능성이 높습니다. 후자의 정책은 게스트 프로그램으로 해제할 수 없으므로 관리자에게 해당 정책의 예외, 작업용 세션 유형, 또는 승인된 작업 스케줄러/서비스 사용을 요청해야 합니다.

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
- `CpuFloor` 모드는 게스트 CPU 사용량이 목표 이상이 되도록 시도하지만, 다른 프로세스·하이퍼바이저 스케줄링·CPU 제한 정책에 따라 측정값이 달라질 수 있습니다. 로그의 `CPU floor status`에서 실제 측정값과 worker duty를 확인하세요.
- `SetThreadExecutionState`는 호출 스레드가 시스템/디스플레이 유휴 타이머를 갱신하도록 요청할 뿐, 조직의 서버 측 세션 만료·최대 접속 시간·강제 로그오프 정책을 변경하지 않습니다.
- 보안 소프트웨어가 서명되지 않은 사내 도구 실행을 차단할 수 있습니다. 배포 전 조직의 코드 서명 및 보안 검사 절차를 따르세요.
- 중요 작업은 이 도구 하나에만 의존하지 말고 체크포인트, 재시작 가능한 작업 설계, 작업 스케줄러/서비스 등 조직이 승인한 실행 방식을 우선 검토하세요.

## 라이선스

이 프로젝트는 [MIT License](LICENSE)로 배포됩니다.
