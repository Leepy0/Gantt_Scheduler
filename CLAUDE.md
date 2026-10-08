# 과제 스케줄러 — 작업 안내

과제 진척 대시보드 + 간트 일정 관리 사내 웹. ASP.NET Core Razor Pages(.NET 10), EF Core, 바닐라 JS.
설계와 규칙은 `docs/DESIGN.md` 를 먼저 읽는다.

## 작업 규칙

- `main` 브랜치에서 바로 작업한다.
- 화면 문구·코드 주석·커밋 메시지는 한국어. 주석은 "왜"를 적는다.
- 외부 CDN·웹폰트를 쓰지 않는다(사내망). JS 는 빌드 단계 없는 일반 스크립트(`wwwroot/js`).
- 화면에는 이모지를 쓰지 않는다. 아이콘은 `Pages/Shared/_Icons.cshtml` 의 SVG 스프라이트.
- 색·간격·글자 크기는 `wwwroot/css/site.css` 맨 위 토큰만 쓴다. 빨강은 지연, 노랑은 임박·초과 완료 전용.
- 계산 규칙(상태·진행률·예상 종료)은 `Domain/Rules.cs` 에만 둔다. JS 에서 다시 계산하지 않는다.
- 변경은 `Services/ProjectService.cs` 를 거치고, 반드시 활동 이력(Activity)을 남긴다. 규칙 위반은 `RuleException` 으로 던진다(화면에 메시지 그대로 표시).

## 구조

```
src/GanttScheduler/
  Domain/      엔티티, 규칙(Rules), 화면용 모델(Views) — DB 접근 없음
  Data/        AppDb(EF 모델), Seeder(기준 정보·샘플), Migrations(SQL Server)
  Services/    ProjectService, Dashboard, MasterService(관리), Identity(IP 사용자), ExcelExporter
  Pages/       Index(대시보드), Projects/{Index,Edit,Detail}, Admin/Index, Welcome
  wwwroot/     css/site.css, js/{app,gantt,dashboard,detail,edit,admin}.js
tests/GanttScheduler.Tests/   규칙 단위 테스트, DB 서비스 테스트, HTTP 통합 테스트
e2e/run.cjs                   화면 점검(Playwright): 데스크톱·모바일 흐름 + 캡처
tools/shadow/                 NuGet 없이 빌드하기 위한 가짜 EF (아래 참고)
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
   - 가짜 EF 는 실제 SQL 을 만들지 않는다. 쿼리 번역·제약 조건 문제는 여기서 안 잡힌다. EF API 를 새로 쓰면 FakeEf.cs 에도 같은 시그니처를 추가해야 한다.
2. **CI** — push 하면 GitHub Actions 가 진짜 EF 로 빌드·테스트(SQLite + SQL Server)·화면 점검을 돌리고 결과를 `preview` 브랜치에 올린다. Actions 로그·아티팩트는 이 환경에서 내려받을 수 없으므로 결과는 브랜치로 확인한다.
   ```
   gh run list -R Leepy0/Gantt_Scheduler --limit 1        # 진행 상태
   git fetch origin preview && git show origin/preview:README.md
   git show origin/preview:logs/build.log | tail -40       # logs/{build,test-sqlite,test-sqlserver,e2e,app}.log
   ```

## 모델을 바꿀 때

`Domain/Entities.cs` 나 `Data/AppDb.cs` 를 바꾸면 SQL Server 마이그레이션을 추가해야 한다. 빠뜨리면 CI 의 SQL Server 테스트가 "pending model changes" 로 실패한다.

- PC: `dotnet ef migrations add <이름> --project src/GanttScheduler --output-dir Data/Migrations`
- 클라우드 세션: `gh workflow run migration.yml -R Leepy0/Gantt_Scheduler --ref main -f name=<이름>` → 워크플로가 main 에 커밋하므로 `git pull` 후 이어서 작업. 이 커밋은 CI 를 자동으로 돌리지 않으니 `gh workflow run ci.yml -R Leepy0/Gantt_Scheduler --ref main` 으로 따로 실행한다.
- SQLite(미리보기) DB 는 마이그레이션을 쓰지 않는다. `App_Data/gantt.db` 를 지우면 새로 만들어진다.
