# 과제 스케줄러 (Gantt Scheduler)

과제 진척 대시보드와 간트 일정 관리를 하나로 합친 사내 웹.

- **대시보드** — 진행·지연 현황, 다가오는 일정, 진행 과제 간트(목표/실행), 유형별·법인별 현황
- **과제** — 유형별 기본 단계, 단계 목표일/완료일, 단계 아래 세부 작업(목표·실적 기간, 진행률, 담당)
- **일정 관리** — 간트에서 막대를 끌어 일정 변경, 기준선 확정·비교, 작업 의존 관계와 임계 작업, 근무일(주말·공휴일 제외) 계산
- **단계 체크리스트·첨부 파일** — 체크 항목이 남으면 단계를 완료할 수 없다. 자료는 과제별로 올리고 이미지·PDF 는 바로 미리보기
- **내 할 일** — 내가 담당인 작업·단계를 지연/이번 주/예정으로 모아 보기
- **분석** — 단계별 리드타임 통계, 주간 보고 자동 생성(Excel·PDF), 전체 변경 이력 조회
- **이력** — 목표일 변경 사유, 댓글, 상태 변경을 과제별로 한곳에 기록
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

## 운영 배포 (IIS + SQL Server)

1. `dotnet publish src/GanttScheduler -c Release -o publish`
2. `publish` 폴더를 IIS 사이트(또는 응용 프로그램)로 등록. ASP.NET Core Hosting Bundle(.NET 10) 필요.
3. `appsettings.Production.json` 에 SQL Server 연결 문자열을 넣는다.
   ```json
   {
     "Database": { "Provider": "SqlServer" },
     "ConnectionStrings": { "SqlServer": "Server=...;Database=...;Trusted_Connection=True;TrustServerCertificate=True" }
   }
   ```
4. 앱이 시작하면서 `pm` 스키마에 테이블을 만들고(마이그레이션 자동 적용) 기준 정보 기본값을 넣는다. DB 자체는 미리 있어야 하고, 앱 풀 계정에 테이블 생성 권한이 필요하다.
5. 처음 접속해 이름을 등록한 사람이 관리자가 된다. 관리 화면에서 법인 등 계층과 조직(그룹 → 파트)부터 등록한다.
6. 첨부 파일은 `Storage:Path` 폴더에 저장된다. 배포할 때 덮어쓰이지 않도록 사이트 폴더 밖의 경로를 지정하고, 앱 풀 계정에 쓰기 권한을 준다. 백업 대상에도 넣는다.
7. PDF 내보내기는 서버의 한글 글꼴 파일(TTF)을 쓴다. Windows 는 맑은 고딕(`malgun.ttf`)을 자동으로 찾는다. 없으면 `Pdf:Font` 에 경로를 지정한다.

## 설정 (`appsettings.json`)

| 키 | 기본값 | 설명 |
|---|---|---|
| `Database:Provider` | `Sqlite` (Production 은 `SqlServer`) | 사용할 DB |
| `Database:SqlitePath` | `App_Data/gantt.db` | SQLite 파일 위치 |
| `ConnectionStrings:SqlServer` | localhost | SQL Server 연결 문자열 |
| `Seed:Demo` | `false` (Development 는 `true`) | 과제가 하나도 없을 때 샘플 과제를 넣는다 |
| `Storage:Path` | `App_Data/uploads` | 첨부 파일 저장 폴더 (파일당 30MB, 문서·이미지·압축 파일만) |
| `Pdf:Font`, `Pdf:FontBold` | 맑은 고딕 / 나눔고딕 자동 탐색 | PDF 에 쓸 한글 TTF 글꼴 파일 경로 |
| `TrustForwardedHeaders` | `false` | 리버스 프록시 뒤에서 실제 접속 IP 사용 |

## 테스트

```
dotnet test
```

모델(엔티티)을 바꿨을 때의 마이그레이션 추가 방법과 CI 구성은 [CLAUDE.md](CLAUDE.md) 참고.
