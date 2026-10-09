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
- 변경은 `Services/ProjectService.cs` 를 거치고, 반드시 활동 이력(Activity)을 남긴다. 규칙 위반은 `RuleException` 으로 던진다(화면에 메시지 그대로 표시).
- 과제·단계·작업을 고칠 때는 화면이 보낸 `Version` 을 확인하고(`CheckVersion`) 바꾼 엔티티의 `Version` 을 올린다. 어긋나면 `ConflictException` → 409.

## 구조

```
src/GanttScheduler/
  Domain/      엔티티, 규칙(Rules), 화면용 모델(Views) — DB 접근 없음
  Data/        AppDb(EF 모델), Seeder(기준 정보·샘플), Migrations(SQL Server)
  Services/    ProjectService, Dashboard, Insights(리드타임·주간 보고·변경 이력·내 할 일), AttachmentService,
               MasterService(관리), Identity(IP 사용자), ExcelExporter, PdfExporter,
               Updates·UpdateService(새 버전 확인·받기·적용 판단), UpdateApplier(파일 교체 도우미 모드)
  Pages/       Index(대시보드), My/Index, Projects/{Index,Edit,Detail,Files}, Insights/{LeadTime,Weekly,History},
               Admin/{Index,Update}, Welcome
  wwwroot/     css/site.css, js/{app,gantt,dashboard,detail,edit,admin,org,update}.js
tests/GanttScheduler.Tests/   규칙 단위 테스트, DB 서비스 테스트, HTTP 통합 테스트
e2e/run.cjs                   화면 점검(Playwright): 데스크톱·모바일 흐름 + 캡처
tools/shadow/                 NuGet 없이 빌드하기 위한 가짜 EF·가짜 내보내기 (아래 참고)
tools/release/                pack.py(zip·SHA-256·매니페스트), selftest.sh(실제 서버로 업데이트 적용·되돌리기 시험)
```

## 검증 방법

### PC (NuGet 접근 가능)

```
dotnet test                                     # SQLite 로 전체 테스트
dotnet run --project src/GanttScheduler         # http://localhost:5080 (샘플 데이터)
cd e2e && npm install && node run.cjs           # 실행 중인 앱에 화면 점검 (새로 띄운 샘플 데이터 기준)
```

### Claude 클라우드 세션 (nuget.org 차단)

이 환경에서는 `dotnet restore` 가 안 된다. 두 단계로 확인한다.

1. **로컬 섀도 빌드** — `tools/shadow/build.sh run` 이 패키지 참조 없이 가짜 EF(`tools/shadow/FakeEf.cs`, 메모리 저장)로 앱을 빌드해 띄운다. Razor·C# 컴파일 오류와 화면 동작을 빠르게 볼 수 있다.
   - .NET SDK 가 없으면 `apt-get install -y dotnet-sdk-10.0`
   - 화면 점검: `NODE_PATH=/opt/npm-tools/node_modules OUT_DIR=<폴더> node e2e/run.cjs`
   - 화면 점검에 `FAKE_EXPORT=1` 을 붙인다(섀도 빌드의 Excel·PDF 는 `FakeExcel.cs` 의 빈 껍데기라 형식 검사를 건너뛴다). `ExcelExporter.cs`·`PdfExporter.cs` 는 섀도 빌드에서 컴파일되지 않으므로 고친 뒤에는 CI 로 확인하고, 공개 메서드를 바꾸면 `FakeExcel.cs` 도 맞춘다.
   - 가짜 EF 는 실제 SQL 을 만들지 않는다. 쿼리 번역·제약 조건 문제는 여기서 안 잡힌다. EF API 를 새로 쓰면 FakeEf.cs 에도 같은 시그니처를 추가해야 한다.
2. **CI** — push 하면 GitHub Actions 가 진짜 EF 로 빌드·테스트(SQLite + SQL Server)·화면 점검을 돌리고 결과를 `preview` 브랜치에 올린다. Actions 로그·아티팩트는 이 환경에서 내려받을 수 없으므로 결과는 브랜치로 확인한다.
   ```
   gh run list -R Leepy0/Gantt_Scheduler --limit 1        # 진행 상태
   git fetch origin preview && git show origin/preview:README.md
   git show origin/preview:logs/build.log | tail -40       # logs/{build,test-sqlite,test-sqlserver,e2e,app}.log
   git show origin/preview:exports/과제일정.pdf > /tmp/x.pdf # 화면 점검이 내려받은 Excel·PDF 견본 (exports/)
   ```

## 모델을 바꿀 때

`Domain/Entities.cs` 나 `Data/AppDb.cs` 를 바꾸면 SQL Server 마이그레이션을 추가해야 한다. 빠뜨리면 CI 의 SQL Server 테스트가 "pending model changes" 로 실패한다.

- PC: `dotnet ef migrations add <이름> --project src/GanttScheduler --output-dir Data/Migrations`
- 클라우드 세션: `gh workflow run migration.yml -R Leepy0/Gantt_Scheduler --ref main -f name=<이름>` → 워크플로가 main 에 커밋하므로 `git pull` 후 이어서 작업. 이 커밋은 CI 를 자동으로 돌리지 않으니 `gh workflow run ci.yml -R Leepy0/Gantt_Scheduler --ref main` 으로 따로 실행한다.
- SQLite(미리보기) DB 는 마이그레이션을 쓰지 않는다. 모델이 바뀌면 시작할 때 자동으로 새로 만든다(스키마 해시 비교). 직접 지우려면 `App_Data/gantt.db`.


## 배포와 자동 업데이트

설계는 `docs/DESIGN.md` 8장. 서버는 릴리스의 `GanttScheduler.json`(매니페스트)으로 새 버전을 알고 zip 을 받아 스스로 바뀐다.

- 정식 배포: `GanttScheduler.csproj` 의 `<Version>` 을 올려 커밋 → `gh workflow run release.yml -R Leepy0/Gantt_Scheduler --ref main -f release=true`. 이 환경에서 태그 push 는 막혀 있으므로(403) 워크플로가 태그를 만들게 한다. 버전을 올리지 않으면 서버가 새 버전으로 보지 않는다.
- main push 는 `dev` prerelease 만 덮어쓴다(자동 업데이트 대상 아님). 시험용 빌드를 Latest 로 올리지 않는다.
- 확인: `gh release view v<버전> -R Leepy0/Gantt_Scheduler --json assets` 에 zip · `.sha256` · `GanttScheduler.json` 세 개가 있어야 한다.
- 매니페스트·패키지 형식은 `tools/release/pack.py` 한 곳에서 만든다. 항목을 바꾸면 `Services/Updates.cs` 의 `ParseManifest` 와 `UpdateService.Stage` 를 같이 본다.
- `state.json`(App_Data/updates)은 옛 버전이 쓴 것을 새 버전이 읽는다. 항목은 추가만 하고 뜻을 바꾸지 않는다. 도우미 인자(`--apply-update …`)도 마찬가지 — 되돌리기는 이전 버전의 도우미가 실행한다.
- 교체 로직을 고치면 자체 시험을 돌린다.
  - 로컬(섀도): `tools/shadow/build.sh && PROJECT=/tmp/gantt-shadow MEMORY_DB=1 tools/release/selftest.sh`
  - CI: `update-selftest.yml` 이 Windows·Linux 에서 런타임 포함 빌드로 돌린다. 실패 원인은 주석(annotation)으로 남는다: `gh api repos/Leepy0/Gantt_Scheduler/check-runs/<jobId>/annotations`
- 업데이트 화면을 여러 상태로 보려면 `-p:BuildChannel=release -p:Version=…` 로 게시한 빌드를 `Update__Source=<폴더>` 와 함께 띄운다(로컬 빌드 채널은 적용 단추가 막혀 있다).
