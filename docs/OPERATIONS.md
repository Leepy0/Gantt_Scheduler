# 운영: 백업·복원·서버 이전·상태 확인

설치·업데이트는 [README](../README.md), 설계는 [DESIGN 8장](DESIGN.md). 이 문서는 문제가 생겼을 때 데이터를 되살리는 방법이다.

## 1. 무엇을 백업하나

| 대상 | 위치 | 없으면 |
|---|---|---|
| DB | SQL Server(운영) 또는 `App_Data/gantt.db`(SQLite) | 과제·일정·이력 전부 |
| 첨부 파일 | `Storage:Path` (기본 `App_Data/uploads/<과제 번호>/`) | DB 만 복원하면 첨부 기록은 있는데 파일이 없어 내려받기가 404 가 된다 |
| 암호화 키 | `Storage:KeyPath` (기본 `App_Data/keys`) | 서버를 옮긴 직후 열려 있던 화면의 저장이 한 번 거절된다(새로 고치면 된다). 데이터에는 영향이 없다 |
| 운영 설정 | 프로그램 폴더의 `appsettings.Production.json` | 연결 문자열·포트·백업 설정을 다시 적어야 한다 |

업데이트는 `App_Data` 와 `appsettings.Production.json` 을 건드리지 않는다. 그래도 **DB 구조가 바뀌는 업데이트는 프로그램만 되돌릴 수 없으므로** 백업이 유일한 복구 수단이다. 과제 삭제도 되돌릴 수 없다.

## 2. 앱이 만드는 백업

관리 > 업데이트 화면의 **DB 백업** 카드(관리자). 한 번에 두 파일을 같은 이름으로 만든다.

| 파일 | 내용 | 어디에 |
|---|---|---|
| `gantt-<한국 시각>.bak` (SQL Server) | `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM, INIT` | `Backup:Path` — **SQL Server 컴퓨터 기준** 경로. 비우면 SQL Server 의 기본 백업 폴더 |
| `gantt-<한국 시각>.db` (SQLite) | `VACUUM INTO` 로 만든 일관된 사본 | `Backup:Path`, 비우면 `Backup:FilesPath` |
| `gantt-<한국 시각>.files.zip` | `uploads/`(첨부), `keys/`(암호화 키), `appsettings.Production.json`, `backup.json`(이름·시각·버전·DB 구조·DB 백업 파일) | `Backup:FilesPath` (기본 `App_Data/backups`, 앱 서버) |

- **언제**: '지금 백업' 단추, DB 구조가 바뀌는 업데이트를 적용하기 직전(`Backup:BeforeSchemaUpdate`, 기본 켬 — 백업에 실패하면 적용하지 않는다), 야간 자동 백업(`Backup:Nightly`, 기본 꺼짐, `Backup:NightlyAt` 한국 시간 — 그 시각에 서버가 꺼져 있었으면 4시간 안에 켜졌을 때만 따라잡고, 실패한 회차는 다시 하지 않는다).
- **COPY_ONLY** 라 DBA 의 정기 백업(전체·차등·로그 체인)을 깨지 않는다. 압축 옵션은 쓰지 않는다(Express 판은 지원하지 않는다).
- **권한**: DB 계정에 백업 권한(db_owner 또는 db_backupoperator)이 있어야 하고, 백업 파일은 SQL Server 서비스 계정이 쓴다 — 그 계정에 `Backup:Path` 폴더의 쓰기 권한을 준다. 파일 묶음은 서버 계정(`NT SERVICE\GanttScheduler`)이 쓴다.
- **원격 SQL Server**: `Backup:Path` 는 그 컴퓨터의 경로다. 이 서버에서 파일 크기를 확인하고 오래된 백업을 지우려면 두 컴퓨터가 함께 쓰는 **UNC 경로**(`\\서버\공유\gantt`)로 지정한다. UNC 가 아니면 백업은 되지만 크기 확인·정리는 하지 않는다(그쪽에서 정리한다).
- **보관**: 성공한 백업을 `Backup:Keep`(기본 7)개 남기고 오래된 것을 지운다. 목록은 `Backup:FilesPath/backups.json`.
- 파일 묶음에는 연결 문자열(`appsettings.Production.json`)이 들어 있다. 백업 폴더를 다른 곳에 복사할 때는 접근을 제한한다.
- DBA 가 따로 DB 를 백업하는 서버도 **첨부 폴더는 함께 백업**해야 한다(야간 백업을 켜거나 `App_Data/uploads` 를 파일 백업 대상에 넣는다).

## 3. 복원

순서: **서비스 중지 → DB 복원 → 파일 묶음 복원 → 서비스 시작 → 확인.** DB 와 파일 묶음은 같은 이름(같은 시각)의 짝을 쓴다.

### 3.1 서비스 중지

```
sc stop GanttScheduler
```

### 3.2 DB 복원 — SQL Server

SSMS: 데이터베이스 › 우클릭 › 작업 › 복원 › 데이터베이스 › 장치에서 `.bak` 선택 › 옵션 "기존 데이터베이스 덮어쓰기(WITH REPLACE)". 또는 sqlcmd:

```
sqlcmd -S <서버> -E -Q "ALTER DATABASE [GanttScheduler] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [GanttScheduler] FROM DISK = N'D:\Backup\gantt-20261011-023000.bak' WITH REPLACE, CHECKSUM; ALTER DATABASE [GanttScheduler] SET MULTI_USER;"
```

- 다른 서버로 옮기면서 파일 위치가 다르면 `RESTORE FILELISTONLY FROM DISK = N'…'` 로 논리 이름을 보고 `WITH MOVE N'<논리 이름>' TO N'<새 경로>.mdf', MOVE N'<논리 이름>_log' TO N'<새 경로>.ldf'` 를 붙인다.
- 복원한 DB 의 사용자(서비스 계정 로그인)가 끊겼으면 README 의 `CREATE USER … FOR LOGIN …` / `ALTER ROLE db_owner ADD MEMBER …` 를 다시 실행한다.

### 3.3 DB 복원 — SQLite

서비스를 멈춘 상태에서 `App_Data/gantt.db` 와 옆의 `gantt.db-wal`·`gantt.db-shm` 을 다른 곳으로 옮기고, 백업 파일을 `App_Data/gantt.db` 로 복사한다. `gantt.db.schema` 는 그대로 둔다(구조가 같은 버전으로 복원하는 경우).

### 3.4 파일 묶음 복원

`gantt-<시각>.files.zip` 을 풀어:

- `uploads/` 안의 과제 번호 폴더 → `Storage:Path`(기본 `App_Data/uploads`)에 덮어쓴다.
- `keys/` → `Storage:KeyPath`(기본 `App_Data/keys`). 같은 서버면 덮어쓴다. **다른 서버로 옮길 때**는 키가 Windows 서비스의 DPAPI(그 컴퓨터 전용)로 암호화돼 있어 새 서버에서 읽을 수 없다 — 복사하지 않아도 된다(새 키가 만들어지고, 열려 있던 화면만 새로 고치면 된다).
- `appsettings.Production.json` → 프로그램 폴더. 새 서버면 연결 문자열·경로를 그 서버에 맞게 고친다.
- `backup.json` 의 `version`·`schema` 로 어느 버전에서 만든 백업인지 본다.

### 3.5 구조가 바뀐 업데이트를 되돌리는 경우

1. 업데이트 화면의 적용 이력이나 진단 정보에서 **적용 전 백업** 이름을 확인한다(되돌리기 단추가 막혀 있으면 안내에 "복원할 백업" 이 보인다).
2. 서비스 중지 → 3.2/3.3 으로 그 백업의 DB 를 복원 → 3.4 로 첨부를 복원.
3. [배포 레포의 Releases](https://github.com/Leepy0/Gantt_Scheduler_Release/releases) 에서 **이전 버전 zip** 을 받아 프로그램 폴더에 덮어쓴다(`App_Data`, `appsettings.Production.json` 은 그대로 둔다).
4. 서비스 시작. 새 버전은 다시 자동으로 적용되지 않게 업데이트 화면에서 자동 적용을 끄거나, 고친 다음 버전을 기다린다.

## 4. 서버 이전

1. 새 서버에 README "운영 배포" 1~3 단계로 같은 버전을 설치한다(서비스는 아직 시작하지 않거나 바로 멈춘다).
2. 옛 서버에서 '지금 백업' → `.bak`(또는 `.db`)과 `.files.zip` 을 새 서버로 옮긴다.
3. 3.2~3.4 로 복원한다. 새 SQL Server 면 서비스 계정 로그인을 다시 만든다.
4. 서비스 시작 → 아래 5 의 확인. 사용자는 IP 로 식별하므로 서버 주소가 바뀌어도 그대로 쓴다(서버 이름이 바뀌면 사용자에게 새 주소를 알린다).
5. 옛 서버의 서비스는 `uninstall-service.cmd` 로 지운다(폴더와 데이터는 남는다).

## 5. 복원·이전 뒤 확인

- **관리 > 서버 상태**: DB 연결·DB 구조(적용된 마이그레이션이 프로그램과 같은지)·폴더 쓰기 권한·PDF 글꼴이 모두 정상인지, **첨부 파일** 줄에 "파일이 없는 첨부" 가 0개인지 본다. 0개가 아니면 파일 묶음을 복원하지 않았거나 `Storage:Path` 가 다른 곳을 가리킨다.
- `http://<서버>/health/ready` 가 200 과 `"status":"ok"` 를 준다(DB·디스크). 감시 도구는 이 주소를 본다(실패하면 503). `/health` 는 프로그램이 떠 있는지만 본다.
- 최근 과제 하나를 열어 일정·활동·첨부 미리보기가 되는지 본다.

## 6. 로그와 오류 번호

- **파일 로그**: `Logs:Path`(기본 `App_Data/logs`)에 한국 날짜별 `yyyy-MM-dd.log`. `Logs:KeepDays`(기본 30)일이 지난 파일은 지운다. 서버 상태 화면에서 오늘 로그를 내려받을 수 있다(연결 문자열의 계정·비밀번호는 가린다).
- **최근 경고·오류**: 서버 상태 화면에 최근 200건(서버를 다시 시작하면 비워진다).
- **오류 번호**: 사용자가 본 "오류 번호 0HN…:0000002A" 는 그 요청의 번호다. 로그 파일에서 그 번호를 찾으면 같은 요청의 오류가 `(요청 <번호> <주소>)` 와 함께 있다.
- **느린 요청**: `Logs:SlowRequestMs`(기본 1000)ms 를 넘긴 요청은 경고로 남는다("느린 요청 1520ms: GET /Projects …"). 어떤 화면을 먼저 빠르게 할지 실제 기록으로 정한다.
- **업데이트가 실패하면**: 업데이트 화면의 **진단 정보**(교체 기록 `apply.log` 끝 60줄, 시작 오류 `startup-error.log`, 상태 요약)를 보거나 내려받아 전달한다.
