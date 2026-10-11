# 과제 스케줄러 — 작업 안내

과제 진척 대시보드 + 간트 일정 관리 사내 웹. ASP.NET Core Razor Pages(.NET 10), EF Core, 바닐라 JS.
설계와 규칙은 `docs/DESIGN.md` 를 먼저 읽는다.

## 작업 규칙

- `main` 브랜치에서 바로 작업한다.
- 화면 문구·코드 주석·커밋 메시지는 한국어. 주석은 "왜"를 적는다.
- 외부 CDN·웹폰트를 쓰지 않는다(사내망). JS 는 빌드 단계 없는 일반 스크립트(`wwwroot/js`).
- 화면에는 이모지를 쓰지 않는다. 아이콘은 `Pages/Shared/_Icons.cshtml` 의 SVG 스프라이트.
- 색·간격·글자 크기는 `wwwroot/css/site.css` 맨 위 토큰만 쓴다. 빨강은 지연, 노랑은 임박·초과 완료 전용.
- 계산 규칙(상태·진행률·예상 종료·여유/임계)은 `Domain/Rules.cs`, 근무일 계산은 `Domain/WorkCalendar.cs` 에만 둔다. JS 에서 다시 계산하지 않는다.
- 변경은 `Services/ProjectService.cs` 를 거치고, 반드시 활동 이력(Activity)을 남긴다. 활동은 `NewActivity`(→ `ActivityLog`)로만 만든다 — 직접 `new Activity` 하면 알림이 빠진다. 규칙 위반은 `RuleException` 으로 던진다(화면에 메시지 그대로 표시).
- 과제·단계·작업을 고칠 때는 화면이 보낸 `Version` 을 확인하고(`CheckVersion`) 바꾼 엔티티의 `Version` 을 올린다. 어긋나면 `ConflictException` → 409.

## 구조

```
src/GanttScheduler/
  Domain/      엔티티, 규칙(Rules), 화면용 모델(Views) — DB 접근 없음
  Data/        AppDb(EF 모델), Seeder(기준 정보·샘플), Migrations(SQL Server)
  Services/    ProjectService(+ .Schedule: 단계 일정 표·등록 초안, .Plan: 저장 전 미리보기·재계획, .Links: 과제 간 단계 연결, .Collab: 새 소식·관심 과제·댓글 수정, .Trash: 휴지통 목록·복원·영구 삭제, .Baselines: 기준선 차수 이력, .Progress: 빠른 갱신, .Risk: 조기 경보 조회, .Analytics: 사유 분류·진척 값 기록), Trash(TrashService·TrashWorker 자동 정리), Dashboard, Insights(리드타임·기간 보고·변경 이력·내 할 일), Analytics(추세·변경 분석·부하·법인 × 기기 — 집계는 Reporting.Analytics), Snapshots(일별 진척 스냅샷 SnapshotWorker), AttachmentService,
               MasterService(관리), Identity(IP 사용자), ExcelExporter, PdfExporter,
               Updates·UpdateService(새 버전 확인·받기·적용 판단), UpdateKey(배포 파일 서명 확인),
               UpdateApplier(파일 교체 도우미 모드 — 새 버전 시작 확인·자동 되돌리기 포함),
               ServiceInstaller(Windows 서비스 등록·제거 명령), WindowsServiceSupport(서비스 호스팅),
               BackupService(DB 백업·파일 묶음·야간 백업), OpsChecks(서버 점검·첫 설정·첨부 대조·관리자 알림 OpsAlerts),
               FileLog(파일 로그·최근 경고), RequestDiagnostics(느린 요청·오류 번호), PdfFonts(PDF 글꼴 탐색),
               Notifications(ActivityLog — 활동 기록과 알림 계산, 저장 훅), Notify(메일 요약 NotifyWorker·SMTP),
               Board(TV 상황판·데이터 버전), ClosedDates(과제 종료일 채우기), MasterChanges(관리 변경 이력), UserAdminService(사용자 병합·이관·사용 현황)
  Pages/       Index(대시보드), My/{Index,Notifications}, Projects/{Index,Edit,Detail,Files,Import}, Insights/{LeadTime,Weekly,Trend,Changes,Load,History},
               Admin/{Index,Update,Status,Trash}, Board(상황판), Help(도움말), Welcome
  wwwroot/     css/site.css, js/{app,gantt,dashboard,detail,schedule,plan-preview,collab,notifications,trash,quick,my,edit,stage-draft,admin,org,update,status,works-paste,location-picker,import,board,admin-people}.js
tests/GanttScheduler.Tests/   규칙 단위 테스트, DB 서비스 테스트, HTTP 통합 테스트
e2e/run.cjs                   화면 점검(Playwright): 데스크톱·모바일 흐름 + 캡처
tools/shadow/                 NuGet 없이 빌드하기 위한 가짜 EF·가짜 내보내기 (아래 참고)
tools/release/                pack.py(zip·SHA-256·매니페스트·서명), keygen.sh(서명 키 만들기 — 사용자가 한 번),
                              selftest.sh(실제 서버로 업데이트 적용·되돌리기·시작 실패 자동 되돌림·서명 시험)
Directory.Packages.props      패키지 버전(정확한 값으로 고정). packages.lock.json 과 함께 커밋, CI 는 잠금 모드 복원
```

## 검증 방법

### PC (NuGet 접근 가능)

```
dotnet test                                     # SQLite 로 전체 테스트
dotnet run --project src/GanttScheduler         # http://localhost:5080 (샘플 데이터)
cd e2e && npm install && node run.cjs           # 실행 중인 앱에 화면 점검 (새로 띄운 샘플 데이터 기준)
```

### Claude 클라우드 세션

세션마다 네트워크 정책이 다를 수 있다(nuget.org 가 열려 있기도, 막혀 있기도 하다). **먼저 `dotnet restore` 를 시도한다.**

- **복원이 되면 진짜 EF 로 PC 와 같게 확인한다** — `dotnet build`(경고 0) → `dotnet test`(SQLite) → 앱을 띄워 화면 점검.
  ```
  dotnet restore && dotnet build -v q && dotnet test --no-build
  cd src/GanttScheduler && rm -rf App_Data && ASPNETCORE_ENVIRONMENT=Development dotnet bin/Debug/net10.0/GanttScheduler.dll --urls http://localhost:5080 &
  NODE_PATH=/opt/node22/lib/node_modules OUT_DIR=<폴더> node e2e/run.cjs      # 새로 띄운 샘플 DB 기준
  ```
  - PDF 내보내기 테스트·화면 점검에는 한글 글꼴이 필요하다: `apt-get install -y fonts-nanum`
  - 일부 단계만: `ONLY='업데이트|모바일 대시보드' node e2e/run.cjs`(등록 단계는 항상 돈다). 실패하면 `screens/fail-NN.png`·주소·화면 문구가 `report.md` 에 남는다. `APP_LOG=<앱 로그>` 를 주면 경고·오류 줄 요약도 넣는다.
  - SQL Server 테스트만 CI 에 맡긴다(아래 2).
- **복원이 안 되면** 두 단계로 확인한다.

1. **로컬 섀도 빌드** — `tools/shadow/build.sh run` 이 패키지 참조 없이 가짜 EF(`tools/shadow/FakeEf.cs`, 메모리 저장)로 앱을 빌드해 띄운다. Razor·C# 컴파일 오류와 화면 동작을 빠르게 볼 수 있다. 복원이 되는 세션에서도 섀도 빌드가 깨지지 않았는지 `tools/shadow/build.sh` 로 확인한다.
   - .NET SDK 가 없으면 `apt-get install -y dotnet-sdk-10.0`
   - 화면 점검: `NODE_PATH=/opt/npm-tools/node_modules OUT_DIR=<폴더> node e2e/run.cjs` (node 경로는 세션마다 다를 수 있다: `/opt/node22/lib/node_modules`)
   - 화면 점검에 `FAKE_EXPORT=1` 을 붙인다(섀도 빌드의 Excel·PDF 는 `FakeExcel.cs` 의 빈 껍데기라 형식 검사를 건너뛴다). `ExcelExporter.cs`·`PdfExporter.cs` 는 섀도 빌드에서 컴파일되지 않으므로 고친 뒤에는 CI 로 확인하고, 공개 메서드를 바꾸면 `FakeExcel.cs` 도 맞춘다.
   - 서비스 호스팅 패키지를 쓰는 코드는 `Services/WindowsServiceSupport.cs` 한 파일에만 둔다(섀도 빌드는 `FakeService.cs` 로 바꾼다). `ServiceInstaller.cs` 는 기본 라이브러리만 써서 섀도에서도 컴파일된다.
   - 가짜 EF 는 실제 SQL 을 만들지 않는다. 쿼리 번역·제약 조건 문제는 여기서 안 잡힌다. EF API 를 새로 쓰면 FakeEf.cs 에도 같은 시그니처를 추가해야 한다.
2. **CI** — push 하면 GitHub Actions 가 진짜 EF 로 빌드·테스트(SQLite + SQL Server — DB 를 쓰는 테스트만)·화면 점검을 돌리고 결과를 `preview` 브랜치에 올린다(실패한 단계의 화면·앱 로그 경고 요약 포함). Actions 로그·아티팩트는 이 환경에서 내려받을 수 없으므로 결과는 브랜치로 확인한다.
   ```
   gh run list -R Leepy0/Gantt_Scheduler --limit 1        # 진행 상태
   git fetch origin preview && git show origin/preview:README.md
   git show origin/preview:logs/build.log | tail -40       # logs/{build,test-sqlite,test-sqlserver,e2e,app}.log
   git show origin/preview:exports/과제일정.pdf > /tmp/x.pdf # 화면 점검이 내려받은 Excel·PDF 견본 (exports/)
   ```

## 모델을 바꿀 때

`Domain/Entities.cs` 나 `Data/AppDb.cs` 를 바꾸면 SQL Server 마이그레이션을 추가해야 한다. 빠뜨리면 CI 의 SQL Server 테스트가 "pending model changes" 로 실패한다.

- PC: `dotnet ef migrations add <이름> --project src/GanttScheduler --output-dir Data/Migrations`
- 클라우드 세션: `gh workflow run migration.yml -R Leepy0/Gantt_Scheduler --ref main -f name=<이름>` → 워크플로가 main 에 커밋하므로 `git pull` 후 이어서 작업. 워크플로가 끝에서 CI 를 실행하고, 적용될 SQL(idempotent)을 실행 요약(Summary)에 남긴다.
- SQLite(미리보기) DB 는 마이그레이션을 쓰지 않는다. 모델이 바뀌면 시작할 때 자동으로 새로 만든다(스키마 해시 비교). 직접 지우려면 `App_Data/gantt.db`.


## 배포와 자동 업데이트

설계는 `docs/DESIGN.md` 8장. 운영 형태는 Windows 서비스(`GanttScheduler.exe install`, 전용 계정 `NT SERVICE\<이름>`). 서버는 릴리스의 `GanttScheduler.json`(매니페스트)으로 새 버전을 알고 zip 을 받아 스스로 바뀐다.

- 요청을 받기 전에 해야 하는 일은 `Program.cs` 본문이 아니라 `StartupWork`(호스트 시작 단계)에 넣는다. 본문에서 오래 걸리면 서비스 시작이 30초 제한에 걸린다.
- 백업·복원·서버 이전·로그는 `docs/OPERATIONS.md`. 서버를 점검하는 새 항목은 `OpsChecks` 한 곳에 `CheckResult` 로 더한다(서버 상태 화면·`/health/ready`·관리자 칩이 같이 쓴다). 백업 목록(`backups.json`)도 state.json 처럼 항목은 추가만 한다.

- 서버가 보는 곳은 공개 레포 `Leepy0/Gantt_Scheduler_Release` 의 릴리스다(이 레포는 비공개라 서버가 읽지 못한다). `appsettings.json` 의 `Update:Source` 와 `release.yml` 의 `RELEASE_REPO` 가 같은 레포를 가리켜야 한다.
- 정식 배포: `GanttScheduler.csproj` 의 `<Version>` 을 올려 커밋·push → **그 커밋의 CI 가 통과한 것을 확인한 뒤** `gh workflow run release.yml -R Leepy0/Gantt_Scheduler --ref main -f release=true`. 워크플로도 첫 단계에서 그 커밋의 CI 결과를 확인한다(실행 중이면 기다리고, 실패·없음이면 태그를 만들기 전에 멈춘다). 이 환경에서 태그 push 는 막혀 있으므로(403) 워크플로가 태그를 만들게 한다. 버전을 올리지 않으면 서버가 새 버전으로 보지 않는다.
- 서명 키: 저장소에 공개키 `src/GanttScheduler/update-key.pub` 가 있으면 정식 배포는 시크릿 `RELEASE_SIGNING_KEY`(개인키)로 매니페스트에 서명한다(`GanttScheduler.json.sig`). 공개키가 있는데 시크릿이 없거나 짝이 맞지 않으면 정식 배포가 멈춘다. 키는 사용자가 `tools/release/keygen.sh` 로 만들고 시크릿을 등록한다(README "서명 키") — 이 환경에서 키를 만들거나 시크릿을 등록하지 않는다. 공개키가 든 빌드는 서명된 업데이트만 받으므로, 키를 바꿀 때는 README 의 순서(`--rotate`)를 따른다.
- 워크플로는 이 레포에 릴리스를 만든 뒤 시크릿 `RELEASE_REPO_TOKEN` 으로 공개 레포에도 만든다. 공개 레포의 릴리스는 이 방법으로만 만들 수 있다 — 이 환경에서는 릴리스를 직접 만들 수 없고(API 403: "not permitted for this session type") 시크릿도 읽거나 쓸 수 없다(Actions secrets API 차단). 돌아가는 길을 만들지 않는다.
- "공개 배포 레포에 올리기" 단계가 실패했으면(시크릿 없음·만료) 주석(annotation)에 이유가 있다. 사용자가 GitHub 화면에서 토큰을 등록한 뒤(README "새 버전 내보내기") 실패한 실행을 다시 돌린다: `gh run rerun <runId> --failed -R Leepy0/Gantt_Scheduler` 또는 Actions 화면의 Re-run failed jobs. main 이 그 뒤로 움직였어도 다시 실행은 원래 커밋으로 돈다(새로 dispatch 하면 "태그가 다른 커밋에 있다"로 막힌다).
- main push 는 CI 가 성공한 뒤에 이 레포의 `dev` prerelease 만 덮어쓴다(자동 업데이트 대상 아님, 정식 배포와 실행 그룹이 달라 서로 취소하지 않는다). 시험용 빌드를 Latest 로 올리지 않는다.
- 패키지 버전은 `Directory.Packages.props` 에 고정한다. 바꾸면 `dotnet restore --force-evaluate` 로 `packages.lock.json` 을 다시 만들어 함께 커밋한다(CI·배포·자체 시험은 잠금 모드라 어긋나면 실패한다). 새 런타임 대상으로 게시하려면 csproj 의 `RuntimeIdentifiers` 에도 넣는다.
- 확인: 서버와 같은 방법으로 — `curl -sL https://github.com/Leepy0/Gantt_Scheduler_Release/releases/latest/download/GanttScheduler.json` 의 버전·sha256·size 가 새 릴리스와 같아야 한다.
- 공개 레포에는 배포 파일과 변경 내용(커밋 메시지)이 그대로 나간다. 비밀 값이나 사내 고유 정보를 코드·샘플 데이터·커밋 메시지에 넣지 않는다.
- 매니페스트·패키지 형식은 `tools/release/pack.py` 한 곳에서 만든다. 항목을 바꾸면 `Services/Updates.cs` 의 `ParseManifest` 와 `UpdateService.Stage` 를 같이 본다.
- `state.json`(App_Data/updates)은 옛 버전이 쓴 것을 새 버전이 읽는다. 항목은 추가만 하고 뜻을 바꾸지 않는다. 도우미 인자(`--apply-update …`)도 마찬가지 — 되돌리기는 이전 버전의 도우미가 실행한다. 새 버전의 도우미는 다시 띄운 새 버전이 `started.json` 을 남기는지 확인하고, 시작되지 않으면(DB 구조가 같을 때) 이전 버전으로 되돌린다 — 새 서버가 시작할 때 할 일을 바꾸면 이 표시가 `ApplicationStarted` 에서 계속 써지는지 본다.
- 교체 로직을 고치면 자체 시험을 돌린다.
  - 로컬(진짜 빌드, 복원이 될 때): `PORT=<빈 포트> tools/release/selftest.sh` — Linux 직접 실행 형태. openssl 이 있으면 시험용 키로 서명까지 확인한다
  - 로컬(섀도): `tools/shadow/build.sh && PROJECT=/tmp/gantt-shadow MEMORY_DB=1 tools/release/selftest.sh`
  - CI: `update-selftest.yml` 이 런타임 포함 빌드로 세 가지(Windows 서비스 등록 = 운영 형태, Windows 직접 실행, Linux 직접 실행)를 돌린다. 서비스 등록 명령(`install`)·계정 권한·`sc start` 재시작은 여기서만 실제로 확인된다. 실패 원인은 주석(annotation)으로 남는다: `gh api repos/Leepy0/Gantt_Scheduler/check-runs/<jobId>/annotations`
- 업데이트 화면을 여러 상태로 보려면 `-p:BuildChannel=release -p:Version=…` 로 게시한 빌드를 `Update__Source=<폴더>` 와 함께 띄운다(로컬 빌드 채널은 적용 단추가 막혀 있다).
