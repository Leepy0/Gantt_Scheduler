# 과제 스케줄러 (Gantt Scheduler)

과제 진척 대시보드와 간트 일정 관리를 하나로 합친 사내 웹.

- **대시보드** — 진행·지연 현황, 다가오는 일정, 진행 과제 간트(목표/실행), 유형별·법인별 현황
- **과제** — 유형별 기본 단계, 단계 목표일/완료일, 단계 아래 세부 작업(목표·실적 기간, 진행률, 담당)
- **일정 관리** — 간트에서 막대를 끌어 일정 변경, 기준선 확정·비교, 작업 의존 관계와 임계 작업, 근무일(주말·공휴일 제외) 계산
- **단계 체크리스트·첨부 파일** — 체크 항목이 남으면 단계를 완료할 수 없다. 자료는 과제별로 올리고 이미지·PDF 는 바로 미리보기
- **내 할 일** — 내가 담당인 작업·단계를 지연/이번 주/예정으로 모아 보기
- **분석** — 단계별 리드타임 통계, 주간 보고 자동 생성(Excel·PDF), 전체 변경 이력 조회
- **이력** — 목표일 변경 사유, 댓글, 상태 변경을 과제별로 한곳에 기록
- **자동 업데이트** — 서버가 새 버전을 확인해 받아 두고, 아무도 쓰지 않는 시간에 스스로 바꾼다. 열려 있는 화면에는 새로 고침 안내가 뜬다
- **관리** — 법인·제품군·라인·층, 유형·단계·체크 항목 템플릿, 기기·용기 종류, 협력사, 조직(그룹 → 파트), 사용자, 공휴일

ASP.NET Core Razor Pages (.NET 10) · EF Core · SQL Server(운영) / SQLite(미리보기·테스트)

설계 내용은 [docs/DESIGN.md](docs/DESIGN.md).

## 실행

```
dotnet run --project src/GanttScheduler
```

http://localhost:5080 — 처음 접속하면 이름을 등록한다. 개발 환경에서는 SQLite(`src/GanttScheduler/App_Data/gantt.db`)에 샘플 과제가 들어간다. 초기화하려면 `App_Data` 폴더를 지우고 다시 실행. 모델이 바뀐 버전을 받으면 SQLite DB 는 자동으로 새로 만들어진다(기존 미리보기 데이터는 사라진다).

같은 Wi-Fi 의 휴대폰에서 보려면 `dotnet run --project src/GanttScheduler --launch-profile lan` 으로 띄우고 `http://<PC IP>:5080` 으로 접속한다(Windows 방화벽에서 5080 허용 필요).

## 휴대폰에서 확인

1. **화면 캡처** — main 에 push 할 때마다 CI 가 데스크톱·모바일 크기로 주요 화면을 캡처해 [`preview` 브랜치](../../tree/preview)에 올린다. 빌드·테스트 결과와 내보내기 파일(Excel·PDF) 견본도 같이 올라간다.
2. **직접 조작** — GitHub Codespaces. 레포 화면에서 Code → Codespaces → Create codespace on main. 앱이 자동으로 뜨고 5080 포트 주소가 열린다(샘플 데이터, SQLite). 다시 띄우거나 데이터를 초기화하려면 터미널에서 `tools/preview.sh start` / `tools/preview.sh reset`.

## 운영 배포 (Windows 서비스 + SQL Server)

처음 한 번만 직접 설치하고, 그 뒤로는 서버가 스스로 업데이트한다. IIS 나 .NET 을 따로 설치하지 않는다.

1. [Releases](../../releases/latest) 에서 `GanttScheduler-win-x64.zip` 을 받아 서버의 폴더(예: `D:\GanttScheduler`)에 푼다.
2. `appsettings.Production.json` 에 SQL Server 연결 문자열을 넣는다. DB 는 미리 만들어 둔다(테이블은 앱이 만든다).
   ```json
   {
     "Database": { "Provider": "SqlServer" },
     "ConnectionStrings": { "SqlServer": "Server=...;Database=GanttScheduler;User Id=...;Password=...;TrustServerCertificate=True" }
   }
   ```
3. `install-service.cmd` 를 실행한다(권한 확인 창에서 "예"). 서비스 등록 → 권한 설정 → 방화벽 허용 → 시작까지 하고, 시작에 실패하면 원인을 보여 준다.
   - 기본 포트는 80. 다른 프로그램이 쓰고 있으면 `install-service.cmd --port 8080` 처럼 지정한다.
   - 포트를 바꾸거나 폴더를 옮겼을 때도 같은 명령을 다시 실행하면 된다.
4. `http://<서버 이름>/` 으로 접속한다. 처음 이름을 등록한 사람이 관리자가 된다. 관리 화면에서 법인 등 계층과 조직(그룹 → 파트)부터 등록한다.

알아둘 것:

- **서비스 계정**: 서비스는 전용 계정 `NT SERVICE\GanttScheduler` 로 돈다(SYSTEM 권한을 쓰지 않는다). 설치 명령이 이 계정에 프로그램 폴더 수정 권한과 자기 서비스를 시작할 권한을 준다. 둘 다 자동 업데이트에 필요하다.
- **SQL Server 를 Windows 인증으로** 쓰려면(`Trusted_Connection=True`) 그 계정의 로그인을 만들어야 한다. 같은 서버의 SQL Server 면 아래처럼, 다른 서버면 `도메인\서버이름$`(컴퓨터 계정)으로 만든다.
  ```sql
  CREATE LOGIN [NT SERVICE\GanttScheduler] FROM WINDOWS;
  USE GanttScheduler;
  CREATE USER [NT SERVICE\GanttScheduler] FOR LOGIN [NT SERVICE\GanttScheduler];
  ALTER ROLE db_owner ADD MEMBER [NT SERVICE\GanttScheduler];
  ```
- **데이터**는 프로그램 폴더의 `App_Data` 에 있다(첨부 파일, 업데이트 파일, 암호화 키). 백업 대상에 넣는다. 업데이트는 `App_Data` 를 건드리지 않는다. `Storage:Path` 등을 폴더 밖으로 옮기면 서비스 계정에 그 폴더의 수정 권한을 따로 준다.
- **PDF 내보내기**는 서버의 한글 글꼴 파일(TTF)을 쓴다. Windows 는 맑은 고딕(`malgun.ttf`)을 자동으로 찾는다. 없으면 `Pdf:Font` 에 경로를 지정한다.
- **문제가 생기면**: 시작 실패 원인은 `App_Data/startup-error.log`, 실행 중 경고·오류는 Windows 이벤트 뷰어(응용 프로그램 로그), 업데이트 교체 기록은 `App_Data/updates/apply.log`.
- **제거**: `uninstall-service.cmd`. 서비스와 방화벽 규칙만 지우고 폴더와 데이터는 그대로 둔다.
- HTTPS 가 필요하면 `appsettings.Production.json` 의 `Kestrel:Endpoints` 에 인증서와 주소를 설정한다(이 경우 `--port` 대신 그 설정이 쓰인다).

<details>
<summary>IIS 로 운영하는 경우</summary>

같은 zip 을 IIS 사이트 폴더에 풀어 등록해도 동작한다(ASP.NET Core Hosting Bundle 의 IIS 모듈 필요). 다만 서비스 방식과 달리 실제 IIS 에서 업데이트 교체를 시험하지 못했다.

- 앱 풀을 **항상 실행**으로 바꿔야 한다: 시작 모드 `AlwaysRunning`, 유휴 시간 제한 `0`. 기본 설정은 20분간 요청이 없으면 앱을 내리는데, 그러면 야간 자동 확인·자동 적용이 돌지 않는다.
- 앱 풀 계정에 사이트 폴더 수정 권한을 준다: `icacls "D:\Sites\GanttScheduler" /grant "IIS AppPool\<앱 풀 이름>:(OI)(CI)M"`
- 교체하는 동안 IIS 가 "업데이트 중" 화면을 대신 보여 주고, 다음 요청 때 새 버전을 띄운다.

</details>

## 업데이트

관리 > **업데이트** 화면에서 한다. 관리자만 바꿀 수 있다.

| 방법 | 동작 | 필요한 것 |
|---|---|---|
| 자동 | 6시간마다 새 버전을 확인해 받아 두고(SHA-256 검증), 설정한 시간대(기본 02~05시, 한국 시간)에 30분 동안 요청이 없으면 적용 | 서버가 `Update:Source` 를 읽을 수 있어야 한다 |
| 직접 | "지금 확인" → "받기" → "지금 적용" | 위와 같음 |
| 파일로 | 릴리스의 zip 을 브라우저로 받아 업데이트 화면에 올린 뒤 "지금 적용" | 없음 (서버가 인터넷에 못 나가도 된다) |

- **적용**하면 서버가 잠깐(보통 1분 이내) 멈췄다가 서비스가 새 버전으로 다시 시작된다. 열려 있던 화면은 다음 확인 때(1분 이내) **새로 고침 안내**를 띄운다. 입력 중인 내용을 지우지 않도록 자동으로 새로 고치지는 않는다. 멈춘 사이에 저장을 누르면 "연결하지 못했습니다"가 뜨고 입력한 내용은 그대로 남는다.
- **DB 구조가 바뀌는 업데이트**는 자동으로 적용하지 않는다. 화면에 표시되며, DB 를 백업한 뒤 관리자가 직접 누른다.
- **되돌리기**: 직전 버전 파일을 한 세대 보관한다. DB 구조가 바뀌지 않은 업데이트만 프로그램을 되돌릴 수 있다. 되돌린 버전은 자동으로 다시 적용하지 않는다.
- 바뀌는 것은 배포 패키지에 든 파일뿐이다. `App_Data`, 서버에서 고친 `appsettings.Production.json`·`web.config`, 폴더에 직접 넣어 둔 파일은 그대로 둔다.
- 문제가 생기면 서버의 `App_Data/updates/apply.log` 를 본다.

`Update:Source` 에 쓸 수 있는 값:

| 값 | 뜻 |
|---|---|
| `github:Leepy0/Gantt_Scheduler` (기본) | GitHub 릴리스. 사내망에서 막힌 `api.github.com` 대신 `github.com` 주소(`releases/latest/download/GanttScheduler.json`)로 확인한다. **레포가 공개여야** 서버가 읽을 수 있다 |
| `https://사내서버/경로` | 그 주소 아래의 `GanttScheduler.json` 과 zip |
| `\\서버\공유\폴더` 또는 `D:\updates` | 그 폴더의 `GanttScheduler.json` 과 zip. 릴리스의 두 파일을 그대로 복사해 넣으면 된다 |

### 새 버전 내보내기

1. `src/GanttScheduler/GanttScheduler.csproj` 의 `<Version>` 을 올려 커밋한다. 올리지 않으면 서버가 새 버전으로 보지 않는다.
2. GitHub › Actions › **배포 패키지** › Run workflow › "정식 배포" 체크. 테스트를 통과하면 `vX.Y.Z` 릴리스(zip, `.sha256`, `GanttScheduler.json`)가 만들어진다.

main 에 push 할 때마다 만들어지는 `dev` 릴리스는 시험용이며 자동 업데이트 대상이 아니다.

## 설정 (`appsettings.json`)

| 키 | 기본값 | 설명 |
|---|---|---|
| `Database:Provider` | `Sqlite` (Production 은 `SqlServer`) | 사용할 DB |
| `Database:SqlitePath` | `App_Data/gantt.db` | SQLite 파일 위치 |
| `ConnectionStrings:SqlServer` | localhost | SQL Server 연결 문자열 |
| `Seed:Demo` | `false` (Development 는 `true`) | 과제가 하나도 없을 때 샘플 과제를 넣는다 |
| `Storage:Path` | `App_Data/uploads` | 첨부 파일 저장 폴더 (파일당 30MB, 문서·이미지·압축 파일만) |
| `Pdf:Font`, `Pdf:FontBold` | 맑은 고딕 / 나눔고딕 자동 탐색 | PDF 에 쓸 한글 TTF 글꼴 파일 경로 |
| `Storage:KeyPath` | `App_Data/keys` | 위조 방지 토큰의 암호화 키. 지우지 않으면 서버가 다시 시작돼도 열려 있던 화면의 저장이 그대로 된다 |
| `Update:Source` | `github:Leepy0/Gantt_Scheduler` | 새 버전을 받아 올 곳 (위 "업데이트" 참고). 비우면 파일로 올리는 방식만 쓴다 |
| `Update:Path` | `App_Data/updates` | 받아 둔 새 버전, 되돌리기용 이전 버전, 상태·기록 |
| `Update:CheckHours` | `6` | 새 버전 확인 주기(시간) |
| `TrustForwardedHeaders` | `false` | 리버스 프록시 뒤에서 실제 접속 IP 사용 |

## 테스트

```
dotnet test
```

모델(엔티티)을 바꿨을 때의 마이그레이션 추가 방법과 CI 구성은 [CLAUDE.md](CLAUDE.md) 참고.
