# 과제 스케줄러 (Gantt Scheduler)

과제 진척 대시보드와 간트 일정 관리를 하나로 합친 사내 웹.

- **대시보드** — 진행·지연 현황, 다가오는 일정, 진행 과제 간트(목표/실행), 유형별·법인별 현황
- **과제** — 유형별 기본 단계, 단계 목표일/완료일, 단계 아래 세부 작업(목표·실적 기간, 진행률, 담당). 등록할 때 단계별 기본 기간으로 목표일 초안을 보여 주고, 상세의 "단계 일정 편집"에서 모든 단계 날짜를 한 번에 고친다
- **입력 줄이기** — Excel 에서 복사한 작업 목록을 붙여넣어 한 번에 추가(미리 해석·검증한 표를 보고 확인), 작업 시트의 '저장 후 계속 추가', 유형마다 정해 둔 템플릿 작업을 등록할 때 함께 만들기, 복제할 때 위치·담당 지정, Excel 양식으로 과제·작업 가져오기(행·열마다 문제를 미리 보여 주고 하나라도 틀리면 아무것도 등록하지 않음)
- **일정 관리** — 간트에서 막대를 끌어 일정 변경(저장 전 미리보기), 기준선 확정·비교, 작업 의존 관계(끝나면 시작·같이 시작·같이 끝, 간격)와 임계 작업, 오늘 기준 예상 일정·재계획, 과제 간 단계 연결(어긋나면 경고), 근무일(주말·공휴일 제외) 계산
- **단계 체크리스트·첨부 파일** — 체크 항목이 남으면 단계를 완료할 수 없다. 자료는 과제별로 올리고 이미지·PDF 는 바로 미리보기
- **내 할 일** — 새 소식(내 작업·내 과제·관심 과제에 다른 사람이 남긴 활동, 자동으로 밀린 내 작업 포함)과 내가 담당인 작업·단계를 지연/이번 주/예정으로 모아 보기
- **알림** — 헤더의 종에 읽지 않은 알림 수. 내 작업이 바뀌거나(자동 밀기 포함) 내 과제·관심 과제에 활동이 생기거나 댓글에서 나를 알릴 사람으로 고르면 알림이 온다. 사내 메일 서버가 있으면 근무일 아침에 하루 요약 메일(읽지 않은 알림·지연·임박 작업)
- **분석** — 단계별 리드타임 통계, 주간 보고 자동 생성(Excel·PDF), 전체 변경 이력 조회
- **이력** — 목표일 변경 사유, 댓글, 상태 변경을 과제별로 한곳에 기록. 활동 탭은 종류·단계·검색으로 거르고, 마지막으로 본 뒤의 새 활동을 표시한다. 댓글은 정한 시간 안에 고치거나 지울 수 있고(원문·기록은 남는다) '@' 로 알릴 사람을 고른다
- **함께 쓰기** — 다른 사용자가 보고 있는 과제를 바꾸면 띠로 알린다. 동시에 고쳐 저장이 거절되면 바뀐 값을 보여 주고 같은 창에서 다시 저장한다. 작성 중인 내용은 닫기 전에 묻고 임시 저장한다. 상세·대시보드·내 할 일은 A4 가로로 인쇄된다
- **자동 업데이트** — 서버가 새 버전을 확인해 받아 두고, 아무도 쓰지 않는 시간에 스스로 바꾼다. 열려 있는 화면에는 새로 고침 안내가 뜬다
- **관리** — 법인·제품군·라인·층, 유형·단계·체크 항목·작업 템플릿(단계별 기본 기간 — 완료 과제의 과거 평균을 참고로 보임), 기기·용기 종류, 협력사, 조직(그룹 → 파트), 사용자, 공휴일(연도별, 정부 발표표를 여러 줄 붙여넣기 — 내장 목록은 확정된 해만 들어 있어 다음 해 공휴일은 발표 뒤 넣는다)

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

1. [배포 레포의 Releases](https://github.com/Leepy0/Gantt_Scheduler_Release/releases/latest) 에서 `GanttScheduler-win-x64.zip` 을 받아 서버의 폴더(예: `D:\GanttScheduler`)에 푼다.
2. `appsettings.Production.json` 에 SQL Server 연결 문자열을 넣는다. DB 는 미리 만들어 둔다(테이블은 앱이 만든다).
   ```json
   {
     "Database": { "Provider": "SqlServer" },
     "ConnectionStrings": { "SqlServer": "Server=...;Database=GanttScheduler;User Id=...;Password=...;TrustServerCertificate=True" }
   }
   ```
3. `install-service.cmd` 를 실행한다(권한 확인 창에서 "예"). 서비스 등록 → 권한 설정 → 방화벽 허용 → 시작까지 하고, 시작에 실패하면 원인을 보여 준다.
   - 기본 포트는 80. 다른 프로그램이 쓰고 있으면 `install-service.cmd --port 8080` 처럼 지정한다. 포트를 지정하려면 명령 프롬프트(cmd)를 열고 프로그램 폴더에서 실행한다(파일을 더블클릭하면 옵션을 줄 수 없다).
   - 인자를 잘못 적으면(`--port=8080` 은 받는다, `8080`·`-port 8080` 은 거절) 등록하기 전에 알려 준다.
   - 화면에 나온 내용은 `App_Data\install.log` 에도 남는다. 창이 바로 닫혀 결과를 못 봤으면 이 파일을 연다. 관리자 권한 창이 열리지 않으면(권한 확인이 막힌 PC) 시작 메뉴에서 명령 프롬프트를 '관리자 권한으로 실행'한 뒤 프로그램 폴더로 이동해(`cd /d D:\GanttScheduler`) 다시 실행한다.
   - 방화벽에서 사내 대역만 열려면 `--remoteip 10.0.0.0/8,192.168.0.0/16` 처럼 쉼표로 구분한 CIDR·주소(또는 `LocalSubnet`)를 붙인다. 앱에서도 막으려면 아래 `Access:AllowedNetworks` 에 같은 대역을 적는다. `LocalSubnet` 은 방화벽만 아는 이름이라 앱 설정에는 적을 수 없다(적으면 서버가 시작하지 않는다) — 서버가 속한 실제 대역을 `10.1.0.0/16` 처럼 적는다.
   - 주소는 앞자리 0 없는 십진수로 적는다(`192.168.1.10`). `192.168.001.010` 처럼 앞자리 0 을 붙이면 8진수로 읽혀 다른 주소가 되므로 거절한다(관리 화면의 IP 연결, `admin --ip` 도 같다).
   - 포트를 바꾸거나 폴더를 옮겼을 때도 같은 명령을 다시 실행하면 된다(`--remoteip` 도 다시 붙인다 — 빼면 모든 주소에 열린다).
4. **서버 PC 의 브라우저**에서 `http://localhost/` 로 접속해 이름을 등록한다. 관리자가 아직 없을 때는 서버 자신에서 등록한 사람만 관리자가 된다(다른 PC 에서 먼저 등록해도 일반 사용자). 서버 PC 에서도 `http://<서버 이름>/` 이나 IP 로 접속하면 다른 PC 로 본다. 관리 화면에서 법인 등 계층과 조직(그룹 → 파트)부터 등록한다. 다른 사람은 `http://<서버 이름>/` 으로 접속한다.

알아둘 것:

- **서비스 계정**: 서비스는 전용 계정 `NT SERVICE\GanttScheduler` 로 돈다(SYSTEM 권한을 쓰지 않는다). 설치 명령이 이 계정에 프로그램 폴더 수정 권한과 자기 서비스를 시작·중지할 권한을 준다. 모두 자동 업데이트(파일 교체, 다시 시작, 시작되지 않은 새 버전 되돌리기)에 필요하다. 예전에 설치한 서버는 `install-service.cmd` 를 다시 실행하면 중지 권한이 더해진다.
- **SQL Server 를 Windows 인증으로** 쓰려면(`Trusted_Connection=True`) 그 계정의 로그인을 만들어야 한다. 같은 서버의 SQL Server 면 아래처럼, 다른 서버면 `도메인\서버이름$`(컴퓨터 계정)으로 만든다.
  ```sql
  CREATE LOGIN [NT SERVICE\GanttScheduler] FROM WINDOWS;
  USE GanttScheduler;
  CREATE USER [NT SERVICE\GanttScheduler] FOR LOGIN [NT SERVICE\GanttScheduler];
  ALTER ROLE db_owner ADD MEMBER [NT SERVICE\GanttScheduler];
  ```
- **데이터**는 프로그램 폴더의 `App_Data` 에 있다(첨부 파일, 업데이트 파일, 암호화 키, 로그). 업데이트는 `App_Data` 를 건드리지 않는다. `Storage:Path` 등을 폴더 밖으로 옮기면 서비스 계정에 그 폴더의 수정 권한을 따로 준다.
- **백업**: 관리 > 업데이트의 **DB 백업**에서 '지금 백업'을 누르면 DB 백업과 첨부·암호화 키·운영 설정 묶음(zip)을 같은 이름으로 만든다. DB 구조가 바뀌는 업데이트는 적용 직전에 자동으로 백업한다. SQL Server 는 `appsettings.Production.json` 에 `Backup:Path`(SQL Server 컴퓨터 기준, DB 가 다른 컴퓨터면 UNC 경로)를 지정하고 SQL Server 서비스 계정에 그 폴더의 쓰기 권한을 준다. 복원·서버 이전 절차는 [docs/OPERATIONS.md](docs/OPERATIONS.md).
- **첫 설정·서버 상태**: 관리 > **서버 상태**(관리자)에서 DB 연결·DB 구조·디스크 여유·폴더 권한·PDF 글꼴·첨부 파일 대조·공휴일·마지막 백업을 점검한다. 처음 설치한 서버는 맨 위의 "첫 설정 점검표"(법인·조직·올해와 내년 공휴일·PDF 글꼴·백업 경로)를 따라 채운다. 문제가 있으면 관리자 화면 위쪽에 알림 칩이 뜬다. 감시 도구는 `/health/ready`(DB·디스크, 실패하면 503)를 본다.
- **PDF 내보내기**는 서버의 한글 글꼴 파일(TTF)을 쓴다. Windows 는 맑은 고딕(`malgun.ttf`)을 자동으로 찾는다. 없으면 `Pdf:Font` 에 경로를 지정한다.
- **문제가 생기면**: 관리 > 서버 상태의 "최근 경고·오류"와 오늘 로그 내려받기, 파일 로그 `App_Data/logs/yyyy-MM-dd.log`(한국 날짜, 30일 보존 — Windows 이벤트 뷰어에도 경고 이상이 남는다). 사용자가 본 **오류 번호**로 로그에서 그 요청을 찾는다. 업데이트·시작 실패는 업데이트 화면의 **진단 정보**(`App_Data/updates/apply.log` 끝부분, `App_Data/startup-error.log`, 상태 요약)를 보거나 내려받는다.
- **제거**: `uninstall-service.cmd`. 서비스와 방화벽 규칙만 지우고 폴더와 데이터는 그대로 둔다.
- HTTPS 가 필요하면 `appsettings.Production.json` 의 `Kestrel:Endpoints` 에 인증서와 주소를 설정한다(이 경우 `--port` 대신 그 설정이 쓰인다).

### 사용자 식별과 관리자 복구

로그인 없이 접속 IP 로 사람을 구분한다(설계는 [DESIGN 5장](docs/DESIGN.md)). 운영에서 알아둘 것:

- **관리자 이름에는 새 PC 를 연결할 수 없다.** 이름만 입력해 관리자 권한(업데이트 파일 올리기 → 서버에서 실행)을 얻지 못하게 하기 위해서다. PC 를 바꾼 관리자는 다른 관리자에게 관리 > 사용자 > (그 사람) > 접속 IP 에서 새 IP 를 연결해 달라고 한다.
- **관리자가 혼자이거나 아무도 없을 때**는 서버에서 명령으로 되살린다. 서버의 명령 프롬프트에서 프로그램 폴더로 가서:
  ```
  GanttScheduler.exe admin 홍길동 --ip 10.1.2.34
  ```
  그 사용자를 관리자로 만들고(없으면 새로 만들고, 사용 중지였으면 다시 사용), `--ip` 를 주면 그 IP 를 연결한다(다른 사람에 연결돼 있으면 옮긴다). 내 PC 의 IP 는 관리 화면의 안내나 등록 화면에 표시된다. 서버와 같은 설정 파일로 DB 를 연다 — SQL Server 를 Windows 인증으로 쓰면 **명령을 실행한 Windows 계정**에 DB 권한이 있어야 한다. DB 구조가 이 프로그램 버전과 맞지 않으면(업데이트 직후 아직 서비스를 시작하지 않은 경우 등) 아무것도 바꾸지 않고 안내만 한다. 다른 폴더의 설정을 쓰려면 `--contentRoot <폴더>`.
- **사용자 바꾸기**: 화면 오른쪽 위의 내 이름을 누르면 이 PC 를 다른 이름으로 연결한다(공용 PC, IP 를 넘겨받은 경우). 기존 이름을 입력하면 그 사람에 연결된다.
- **IP 재확인**: 30일(`Access:ReconfirmDays`) 넘게 쓰지 않은 IP 로 접속하면 "OOO 님이 맞습니까?"를 묻는다. DHCP 로 IP 가 바뀌는 환경에서 다른 사람으로 기록되는 것을 막는다. **관리자 계정**은 이 화면에서 "맞습니다"로 되살릴 수 없다(IP 를 넘겨받은 사람이 클릭 한 번에 관리자가 되지 않게). 본인이면 다른 관리자가 관리 > 사용자에서 그 IP 를 다시 연결하거나, 서버에서 `admin 이름 --ip 주소` 를 실행한다. 서버 자신(`http://localhost`)에서는 그대로 확인된다.
- **관리자 기기를 다른 이름으로 바꿀 때**: 관리자에 연결된 PC 에서 사용자 바꾸기로 다른 이름을 입력하면, 그 관리자가 이 PC 에서 돌아오는 방법(다른 관리자의 IP 연결 또는 admin 명령)을 보여 주고 확인을 받은 뒤에만 바꾼다.
- **사용자 정리**: 이름 오타로 같은 사람이 둘이 됐으면 관리 > 사용자 > (오타 이름) > '다른 사용자로 합치기'로 담당·작업·활동·첨부·IP 연결·알림을 모두 남길 이름으로 옮긴다(오타 이름은 지워지고 되돌릴 수 없다). 퇴사·이동한 사람은 '사용 중지'를 누르면 진행 과제·미완료 작업의 담당을 넘겨받을 사람을 고르는 시트가 뜬다. 사용자 목록에 '과제 n · 작업 n'(지금 맡은 일)과 마지막 접속일이 보인다.
- **관리 변경 이력·사용 현황**: 관리 > 변경 이력(관리자)에서 공휴일·템플릿·사용자·IP·업데이트 설정 변경을 누가 언제 했는지 종류·기간·사람으로 찾는다. 관리 > 사용 현황은 조직별 최근 30일 접속자·갱신 건수·갱신 없는 진행 과제 비율을 보인다(사람별 마지막 접속은 관리자만).
- **접속 허용 대역**: `Access:AllowedNetworks` 에 CIDR 목록을 적으면 그 밖에서 온 요청은 403 으로 거절한다(서버 자신은 항상 허용). 비워 두면 모두 받는다.
  ```json
  { "Access": { "AllowedNetworks": [ "10.0.0.0/8", "192.168.0.0/16" ] } }
  ```
- **리버스 프록시 뒤**(IIS ARR, nginx 등)에 둘 때는 `TrustForwardedHeaders: true` 로 실제 접속 IP(X-Forwarded-For)를 쓴다. 같은 서버(루프백)의 프록시가 보낸 값만 믿는다. 이 설정 없이 같은 서버의 프록시를 쓰면 모든 요청이 서버 자신(127.0.0.1)으로 보여 사람 구분·허용 대역·첫 관리자 제한이 모두 무력해진다.

### TV 상황판 PC 설정

회의실·사무실 화면에 `http://<서버>:<포트>/Board` 를 띄워 두면 진행·지연·위험·이번 주 목표·올해 완료와 지연·위험 과제, 이번 주 목표 단계, 법인별 현황을 큰 글씨로 보여 준다(설계는 [DESIGN 4장](docs/DESIGN.md) '상황판').

- **이름 등록 없이** 열린다. 읽기만 하는 화면이라 그 PC 를 사용자로 등록하지 않는다(담당자 목록에도 나타나지 않는다). 접속 허용 대역(`Access:AllowedNetworks`)은 그대로 적용되므로 상황판 PC 의 IP 가 대역 안에 있어야 한다.
- 브라우저를 **전체 화면**(F11, 또는 키오스크 모드)으로 둔다. 예: `msedge --kiosk http://서버:5080/Board --edge-kiosk-type=fullscreen`. 1920×1080 에 맞춰 그리고, 화면이 작으면 글자가 줄어든다. 목록이 넘치면 쪽을 나눠 12초마다 넘긴다.
- 1분마다 바뀐 것이 있는지만 묻고, 바뀌었을 때만 다시 그린다. 서버가 업데이트되면 저절로 새 화면을 받는다. 서버에 닿지 않으면 마지막 화면을 둔 채 '연결할 수 없음'을 띄우고 계속 다시 시도한다.
- 화면 보호기·절전(디스플레이 끄기)은 Windows 설정에서 끈다.

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
| 파일로 | 릴리스의 zip 을 브라우저로 받아 업데이트 화면에 올린 뒤 "지금 적용". 서명을 확인하는 서버는 `GanttScheduler.json`·`GanttScheduler.json.sig` 도 함께 골라 세 파일을 올린다 | 없음 (서버가 인터넷에 못 나가도 된다) |

- **적용**하면 서버가 잠깐(보통 1분 이내) 멈췄다가 서비스가 새 버전으로 다시 시작된다. 열려 있던 화면은 다음 확인 때(1분 이내) **새로 고침 안내**를 띄운다. 입력 중인 내용을 지우지 않도록 자동으로 새로 고치지는 않는다. 멈춘 사이에 저장을 누르면 "연결하지 못했습니다"가 뜨고 입력한 내용은 그대로 남는다.
- **DB 구조가 바뀌는 업데이트**는 자동으로 적용하지 않는다. 화면에 표시되며 관리자가 직접 누른다. 누르면 먼저 DB 를 백업하고(`Backup:BeforeSchemaUpdate`), 백업에 실패하면 적용하지 않고 이유를 보여 준다. 적용 이력에 그 백업 이름이 남는다.
- **되돌리기**: 직전 버전 파일을 한 세대 보관한다. DB 구조가 바뀌지 않은 업데이트만 프로그램을 되돌릴 수 있다. 되돌린 버전은 자동으로 다시 적용하지 않는다.
- **시작 확인과 자동 되돌리기**: 적용한 새 버전이 2분(`Update:VerifySeconds`) 안에 요청을 받기 시작하지 않거나 시작하다 멈추면, DB 구조가 같은 업데이트는 이전 버전으로 자동으로 되돌린다. 업데이트 화면에 이유(시작 오류 앞부분)와 **다시 시도**·**안내 닫기**가 보이고, 그 버전은 자동으로 다시 받거나 적용하지 않는다. DB 구조가 바뀌는 업데이트는 되돌리지 않으므로 DB 백업을 복원한 뒤 이전 버전 zip 으로 설치한다. IIS 로 운영하면 이 확인을 하지 않는다.
- **서명 확인**: 공개키(`update-key.pub`)가 든 버전부터는 릴리스의 서명이 맞는 배포 파일만 받는다. 화면의 "서명 검증" 줄에 켜짐(공개키 지문)·꺼짐이 보인다.
- 바뀌는 것은 배포 패키지에 든 파일뿐이다. `App_Data`, 서버에서 고친 `appsettings.Production.json`·`web.config`, 폴더에 직접 넣어 둔 파일은 그대로 둔다.
- 문제가 생기면 업데이트 화면 아래의 **진단 정보**를 연다(관리자). 서버의 `App_Data/updates/apply.log` 끝 60줄, `App_Data/startup-error.log`(서버가 시작하다 죽은 이유), 상태 요약을 보여 주고 텍스트 파일 하나로 내려받을 수 있다. 실패한 이력 줄의 '진단 정보' 단추로도 연다.

`Update:Source` 에 쓸 수 있는 값:

| 값 | 뜻 |
|---|---|
| `github:Leepy0/Gantt_Scheduler_Release` (기본) | 공개 배포 레포의 GitHub 릴리스. 사내망에서 막힌 `api.github.com` 대신 `github.com` 주소(`releases/latest/download/GanttScheduler.json`)로 확인한다 |
| `https://사내서버/경로` | 그 주소 아래의 `GanttScheduler.json` 과 zip |
| `\\서버\공유\폴더` 또는 `D:\updates` | 그 폴더의 `GanttScheduler.json` 과 zip. 릴리스의 두 파일을 그대로 복사해 넣으면 된다 |

### 새 버전 내보내기

소스는 이 레포(비공개)에, 배포 파일은 공개 레포 [Gantt_Scheduler_Release](https://github.com/Leepy0/Gantt_Scheduler_Release) 에 둔다. 서버는 로그인 없이 읽을 수 있는 공개 레포의 릴리스만 본다.

1. `src/GanttScheduler/GanttScheduler.csproj` 의 `<Version>` 을 올려 커밋한다. 올리지 않으면 서버가 새 버전으로 보지 않는다.
2. 그 커밋의 **CI 가 통과**했는지 확인한다(Actions › CI). 정식 배포는 CI 가 성공한 커밋만 내보낸다 — 실행 중이면 끝날 때까지 기다리고, 실패했거나 실행이 없으면 멈춘다.
3. GitHub › Actions › **배포 패키지** › Run workflow › "정식 배포" 체크. `vX.Y.Z` 릴리스(zip, `.sha256`, `GanttScheduler.json`, 서명 `GanttScheduler.json.sig`)를 두 레포에 만들고, 공개 주소에서 실제로 받아지는지(서명까지) 확인한다.

main 에 push 해도 진행 중인 정식 배포는 취소되지 않는다(테스트 빌드와 실행 그룹이 다르다).

워크플로가 공개 레포에 쓰려면 토큰이 필요하다. **처음 한 번** 등록한다.

1. GitHub 오른쪽 위 프로필 › Settings › Developer settings › Personal access tokens › **Fine-grained tokens** › Generate new token
   - Repository access: *Only select repositories* → `Gantt_Scheduler_Release`
   - Permissions › Repository permissions › **Contents: Read and write**
   - 만들어진 토큰(`github_pat_…`)을 복사한다.
2. 이 레포 › Settings › Secrets and variables › Actions › New repository secret
   - Name: `RELEASE_REPO_TOKEN`, Secret: 복사한 토큰

토큰이 없거나 만료되면 워크플로의 "공개 배포 레포에 올리기" 단계가 실패하고 이유를 알려 준다(서버에는 새 버전이 보이지 않는다). 토큰을 다시 등록한 뒤 Actions 에서 실패한 실행을 열어 **Re-run failed jobs** 를 누르면 같은 버전을 다시 만들어 올린다.

#### 서명 키 (배포 파일 출처 확인)

릴리스를 올리는 토큰이나 GitHub 계정이 털려도 서버가 남의 파일을 받지 않도록, 배포 정보(`GanttScheduler.json`)에 서명한다. **처음 한 번** PC(또는 openssl 이 있는 곳)에서 만든다.

1. `tools/release/keygen.sh` 실행 → 공개키 `src/GanttScheduler/update-key.pub` 가 생기고, 개인키가 화면에 나온다.
2. 이 레포 › Settings › Secrets and variables › Actions › New repository secret
   - Name: `RELEASE_SIGNING_KEY`, Secret: 화면의 개인키 전체(`-----BEGIN EC PRIVATE KEY-----` 부터 `END` 줄까지)
   - 개인키는 저장소·메신저·메일에 두지 않는다. 시크릿에만 둔다.
3. `update-key.pub` 를 커밋하고 정식 배포한다. 이 버전이 설치된 서버부터는 서명이 맞는 업데이트만 받는다(업데이트 화면 "서명 검증: 켜짐").

- 공개키가 저장소에 있는데 시크릿이 없으면 정식 배포가 태그를 만들기 전에 멈추고 이유를 알린다. 시크릿이 공개키와 짝이 아니어도 멈춘다.
- 키 바꾸기: `tools/release/keygen.sh --rotate` → 새 공개키가 기존 공개키 뒤에 붙는다. ① 시크릿은 그대로(옛 키) 둔 채 정식 배포 → 서버가 새 공개키가 든 버전을 받는다 ② 시크릿을 새 개인키로 바꾼다 ③ 모든 서버가 올라온 뒤 `update-key.pub` 에서 옛 키를 지운다.
- 개인키를 잃어버리면 서명된 업데이트를 더 만들 수 없다. 새 키를 만들고, 각 서버에는 새 공개키가 든 버전의 zip 을 직접 설치한다(서비스를 멈추고 파일을 덮어쓴 뒤 시작).

공개되는 것은 배포 파일(실행 파일)과 릴리스의 변경 내용(커밋 메시지)이다. .NET 실행 파일은 도구로 소스에 가깝게 되돌려 볼 수 있으므로, 비밀 값(연결 문자열·토큰)은 코드나 `appsettings.json` 에 넣지 않고 서버의 `appsettings.Production.json` 에만 둔다.

main 에 push 해 CI 가 통과할 때마다 만들어지는 `dev` 릴리스는 이 레포에만 올라가는 시험용이며 자동 업데이트 대상이 아니다.

패키지 버전은 `Directory.Packages.props` 에 고정돼 있고 CI·배포는 잠금 파일(`packages.lock.json`)대로만 복원한다. 패키지를 올리는 일은 Dependabot PR(주 1회)로 하고 PR 화면에서 CI(빌드·테스트·화면 점검, 업데이트 관련 패키지면 업데이트 자체 시험도)가 통과한 것만 합친다. 직접 올릴 때는 `Directory.Packages.props` 를 고친 뒤 `dotnet restore --force-evaluate` 로 잠금 파일을 다시 만들어 함께 커밋한다.

## 설정 (`appsettings.json`)

| 키 | 기본값 | 설명 |
|---|---|---|
| `Database:Provider` | `Sqlite` (Production 은 `SqlServer`) | 사용할 DB |
| `Database:SqlitePath` | `App_Data/gantt.db` | SQLite 파일 위치 |
| `ConnectionStrings:SqlServer` | localhost | SQL Server 연결 문자열 |
| `Seed:Demo` | `false` (Development 는 `true`) | 과제가 하나도 없을 때 샘플 과제를 넣는다 |
| `Storage:Path` | `App_Data/uploads` | 첨부 파일 저장 폴더 (파일당 30MB, 문서·이미지·압축 파일만) |
| `Storage:MaxMB` | `30` | 첨부 파일 하나의 상한(MB, 1~500). 화면 안내·서버 검사가 이 값을 쓴다 |
| `Storage:ProjectMaxMB` | (없음) | 과제 하나의 첨부 합계 상한(MB). 정하면 합계가 넘는 올리기를 거절한다. 자료 탭에 과제 첨부 합계가 보인다 |
| `Pdf:Font`, `Pdf:FontBold` | 맑은 고딕 / 나눔고딕 자동 탐색 | PDF 에 쓸 한글 TTF 글꼴 파일 경로 |
| `Storage:KeyPath` | `App_Data/keys` | 위조 방지 토큰의 암호화 키. 지우지 않으면 서버가 다시 시작돼도 열려 있던 화면의 저장이 그대로 된다 |
| `Update:Source` | `github:Leepy0/Gantt_Scheduler_Release` | 새 버전을 받아 올 곳 (위 "업데이트" 참고). 비우면 파일로 올리는 방식만 쓴다 |
| `Update:Path` | `App_Data/updates` | 받아 둔 새 버전, 되돌리기용 이전 버전, 상태·기록 |
| `Update:CheckHours` | `6` | 새 버전 확인 주기(시간) |
| `Update:VerifySeconds` | `120` | 업데이트 뒤 새 버전이 이 시간 안에 요청을 받기 시작하지 않으면 이전 버전으로 되돌린다. `0` 이면 확인하지 않는다 |
| `Backup:Path` | 없음 | DB 백업 파일 폴더. SQL Server 는 **SQL Server 컴퓨터 기준**(절대 경로, DB 가 다른 컴퓨터면 UNC `\\서버\공유`). 비우면 SQL Server 의 기본 백업 폴더(이 서버에서 크기 확인·정리는 못 한다). SQLite 는 비우면 `Backup:FilesPath` |
| `Backup:FilesPath` | `App_Data/backups` | 첨부·암호화 키·운영 설정 묶음(zip)과 백업 목록(`backups.json`) |
| `Backup:Keep` | `7` | 성공한 백업을 이만큼 남기고 오래된 것을 지운다(이 서버에서 열 수 있는 파일만) |
| `Backup:BeforeSchemaUpdate` | `true` | DB 구조가 바뀌는 업데이트를 적용하기 전에 백업하고, 실패하면 적용하지 않는다 |
| `Backup:Nightly`, `Backup:NightlyAt` | `false`, `01:00` | 야간 자동 백업과 시각(한국 시간). DBA 가 DB 를 따로 백업하면 꺼 두되 첨부 폴더는 백업 대상에 넣는다 |
| `Backup:TimeoutMinutes` | `60` | 큰 DB 의 백업 명령 시간 제한 |
| `Logs:Path` | `App_Data/logs` | 파일 로그 폴더 (한국 날짜별 `yyyy-MM-dd.log`) |
| `Logs:KeepDays` | `30` | 이 일수가 지난 로그 파일은 지운다 |
| `Logs:SlowRequestMs` | `1000` | 이보다 오래 걸린 요청을 경고로 남긴다(`0` 이면 끔) |
| `TrustForwardedHeaders` | `false` (Development 는 `true`) | 리버스 프록시 뒤에서 실제 접속 IP(X-Forwarded-For) 사용. 같은 서버의 프록시만 믿는다. 개발 환경은 화면 점검이 다른 PC(다른 사용자)를 흉내 내도록 켜 둔다 |
| `Access:FirstAdminLocalOnly` | `true` | 관리자가 없을 때 서버 자신(루프백)에서 등록한 사람만 관리자가 된다. `false` 면 처음 등록한 사람 |
| `Access:ReconfirmDays` | `30` | 이 일수 넘게 쓰지 않은 IP 는 "OOO 님이 맞습니까?"를 다시 묻는다. `0` 이면 묻지 않는다 |
| `Access:AddressKeepDays` | `365` | 이 일수 넘게 접속이 없던 IP 연결은 하루 한 번 지운다(관리 > 변경 이력에 남는다). 그 PC 에서 다시 접속하면 이름을 새로 등록한다. `0` 이면 지우지 않는다 |
| `Access:AllowedNetworks` | 없음(모두 허용) | 접속을 받을 대역(CIDR·주소 목록). 밖에서 온 요청은 403. 환경 변수로는 `Access__AllowedNetworks=10.0.0.0/8,192.168.0.0/16`. 잘못 쓰면 서버가 시작하지 않고 이유를 남긴다 |
| `Comments:EditHours` | `24` | 댓글을 남긴 사람이 고치거나 지울 수 있는 시간. `0` 이면 고칠 수 없다 |
| `Notify:Enabled` | `false` | 하루 한 번 메일 요약(읽지 않은 알림 + 내 작업의 지연·임박)을 보낸다. 꺼져 있어도 화면 내 알림(종)은 동작한다 |
| `Notify:DailyHour` | `8` | 요약을 보내는 시각(한국 시간, 0~23). 근무일에만, 그 시각부터 4시간 안에 서버가 떠 있을 때 보낸다(주말·공휴일의 알림은 다음 근무일에 모인다) |
| `Notify:BaseUrl` | 없음 | 메일에 넣을 이 서버의 주소(예: `http://gantt-server:5080/`). 비우면 링크 없이 보낸다 |
| `Notify:Smtp:Host`, `Port`, `From` | 없음, `25`, 없음 | 사내 SMTP 릴레이와 보내는 주소. Host·From 이 있어야 보낸다 |
| `Notify:Smtp:EnableSsl`, `User`, `Password` | `false`, 없음, 없음 | 릴레이가 TLS·계정을 요구할 때만. 계정이 없으면 익명으로 보낸다(사내 릴레이는 대개 IP 로 허용) |
| `Notify:StatePath` | `App_Data/notify.json` | 마지막 요약 기록(같은 날 두 번 보내지 않게) |
| `Trash:KeepDays` | `30` | 지운 과제를 휴지통에 보관하는 날 수. 지나면 하루 한 번 첨부·활동 이력과 함께 영구 삭제한다(영구 삭제 기록은 관리 > 휴지통과 로그에 남는다). `0` 이면 자동으로 지우지 않고 관리 > 휴지통에서 직접만 |
| `Snapshot:Enabled` | `true` | 하루 한 번 과제의 진척 스냅샷(분석 > 추세·기간 보고의 전주 대비)을 남긴다. 끄면 그날부터 추세가 비고, 지난 날은 나중에 채울 수 없다 |
| `Snapshot:Hour` | `18` | 한국 시각 이 시 이후에 그날 스냅샷을 한 번 더 남긴다(그날 마지막 모습으로 덮어쓴다). 서버를 처음 띄운 날은 시작 직후에도 남긴다 |

받는 주소는 관리 > 사용자에서 관리자가 사용자마다 넣는다(본인이 바꿀 수 없다 — IP 로 식별하므로 남의 알림을 가로챌 수 있어서). 같은 시트의 "시험 메일"로 메일 서버 설정을 확인한다(요약이 꺼져 있어도 보낸다). 요약 결과와 실패는 관리 > 서버 상태의 "메일 요약"에 보인다.

## 테스트

```
dotnet test
```

모델(엔티티)을 바꿨을 때의 마이그레이션 추가 방법과 CI 구성은 [CLAUDE.md](CLAUDE.md) 참고.
