# 최신 빌드 미리보기

- 커밋: `d64c453` — 단계 정렬·끌어 놓기, 시트 분류 칸, 선행 단계 CSV, 대시보드 겹침 줄 나눔 (0.8.0)
- 시각: 2026-10-17 14:02 (KST)
- 실행: https://github.com/Leepy0/Gantt_Scheduler/actions/runs/39004352190

| 단계 | 결과 |
|---|---|
| 빌드 | 통과 |
| 테스트 (SQLite) | 통과 |
| 테스트 (SQL Server·마이그레이션) | 통과 |
| 화면 점검 | 통과 |

## 화면 점검 결과

- 대상: http://localhost:5080
- 단계: 111 / 111 통과
- 콘솔·서버 오류: 0건

- 통과 — 처음 접속하면 이름 등록 화면으로 이동
- 통과 — 이름·소속(그룹 → 파트) 등록 후 대시보드 표시
- 통과 — 대시보드 간트 일 단위 배율
- 통과 — 대시보드 간트 — 겹치는 단계(동시 진행)는 줄을 나눠 최대 5줄, 겹치지 않는 과제는 한 줄 높이
- 통과 — 내 이름 → 사용자 바꾸기 화면, 보안 헤더
- 통과 — 과제 목록·필터
- 통과 — 대시보드 숫자·막대 — 누르면 같은 판정의 목록(7일 내 목표·올해 완료·유형), 조건 안내와 해제
- 통과 — 과제 상세 간트 — 기준선·의존선 표시
- 통과 — 접근성 — 간트 이름 칸은 단추(키보드로 열기), 첫 포커스는 첫 입력칸, 요약은 aria-live 없음
- 통과 — 체크리스트 — 남은 항목이 있으면 단계 완료 거절, 체크·추가·삭제
- 통과 — 단계 목표일 변경 — 사유 없이는 거절, 사유 입력 후 근무일 기준 연쇄 이동
- 통과 — 작업 추가·진행률 수정
- 통과 — 편집 창 닫기 — 입력을 바꾸면 Esc·바깥 클릭에 버릴지 묻고, 글자를 끌어 선택해도 닫히지 않음
- 통과 — 작성 중인 진행 내용 — 화면을 새로 고쳐도 다시 열면 복원 제안, 등록하면 지움
- 통과 — 간트 드래그 — 작업 막대를 끌면 사유를 받고 일정 변경
- 통과 — 의존 관계 — 선행보다 이르면 거절, 일정을 맞추면 연결
- 통과 — 저장 전 미리보기 — 막대를 끌면 함께 밀리는 후행 작업과 단계 예상 종료를 표로 보이고, 저장 결과가 미리보기와 같음
- 통과 — 의존 유형·간격 — 작업 시트에서 선행마다 유형·간격을 고르면 막히는 이유를 저장 전에 보이고, 저장하면 유형별 의존선·연결 변경 이력
- 통과 — 재계획 — 시작일이 지난 미착수 작업이 있으면 단추가 보이고, 오늘 기준 예측으로 옮겨질 작업을 표로 보임
- 통과 — 과제 간 연결 — 단계 시트에서 다른 과제 단계에 이으면 어긋날 때 두 과제의 단계에 경고 칩, 칩을 누르면 상대 과제
- 통과 — 기준선 다시 확정 — 사유를 받고 직전·새 목표 종료를 이력에 남김
- 통과 — 기준선 차수 이력 — 정보 탭에 차수·시각·사람·사유, 이전 차수를 고르면 그 차수 대비 +n일로 다시 그림
- 통과 — 목록 보기·정보·활동 탭
- 통과 — 진행 내용 등록 — 처리 중에는 다시 등록되지 않음
- 통과 — 자료 — 올리기·미리보기·내려받기·삭제
- 통과 — 동시 수정 — 다른 화면에서 먼저 고치면 바뀐 값을 보여 주고, 같은 시트에서 다시 저장
- 통과 — 동시 수정 — 작업 시트를 연 뒤 다른 화면이 새 작업을 선행으로 걸어도, 목록을 다시 그려 다시 저장하면 그 연결이 남음
- 통과 — 다른 사용자가 이 과제를 바꾸면 띠로 알리고 새로 고침하면 반영, 내 저장은 띠 없음, 날짜가 바뀌면 다시 읽음
- 통과 — 뒤로 가면 목록 필터 유지, 탭은 주소에 남고, 작업 링크는 연 뒤 지움, 없는 과제는 안내 화면
- 통과 — 해외 PC — 오늘 시작은 그 나라 날짜, 시각은 그 나라 시각
- 통과 — 인쇄 — 메뉴·도구 없이, 간트는 월 배율로 종이 한 폭에 (상세·대시보드·내 할 일)
- 통과 — 과제 복제
- 통과 — 연쇄 이동 — 이후 단계의 미착수 작업도 같은 근무일만큼 이동(선택)
- 통과 — 간트에서 단계 목표일을 끌어도 이후 단계 작업 포함 선택지가 보임
- 통과 — 단계 완료 — 남은 작업을 완료일로 닫고 완료
- 통과 — 단계만 완료하면 남은 작업은 완료(단계)로 보이고 끌 수 없으며, 완료를 취소하면 되돌아옴
- 통과 — 모든 단계 완료 시 자동으로 완료 상태
- 통과 — 상태 변경 — 보류
- 통과 — 과제 등록 — 위치 연쇄 선택, 기기 내역
- 통과 — 과제 수정 화면에 기존 값 표시
- 통과 — 과제 수정 화면 — 동시 수정 뒤 손대지 않은 항목은 최신 값으로 맞추고 같은 화면에서 다시 저장
- 통과 — 단계 일정 편집 — 한 시트에서 여러 단계, 기존 목표일을 바꾸면 사유 한 번, 동시 수정 뒤 같은 시트에서 다시 저장
- 통과 — 내 할 일
- 통과 — 분석 — 리드타임·주간 보고·변경 이력
- 통과 — 사유 분류 — 단계 목표일을 바꿀 때 분류를 고르면 변경 분석의 원인 파레토에 오름
- 통과 — 추세·부하·단계 병목 — 스냅샷 추이와 법인 × 기기 수량, 사람 × 주 부하, 칸을 누르면 그 사람의 할 일(읽기 전용)
- 통과 — 기간 보고 — 월·직접 지정 기간, 조직·변동만 조건, 지연·위험 요약, 변경 이력 Excel
- 통과 — 활동 더 보기 — 최근 50건만 먼저 싣고 나머지는 나눠 받음, 이 과제 전체 이력으로 이동
- 통과 — 작업 시트 진행 내용 더 보기 — 빠르게 두 번 눌러도 같은 활동이 두 번 붙지 않음
- 통과 — 관심 과제 — 상세·목록의 별을 누르면 눌린 상태로 바뀌고 내 할 일에 관심 과제로 보임
- 통과 — 알림 — 다른 사람이 나를 알릴 사람으로 고르면 종에 읽지 않은 수, 새 소식·새 활동 표시, 알림을 누르면 그 활동을 강조
- 통과 — 활동 조건·댓글 고치기 — 종류 칩·단계·검색으로 거르고 조건대로 더 보기, 내 댓글은 고치고 지움
- 통과 — 대시보드 간트 — 과제가 많아도 페이지가 늘지 않고 간트 안에서 스크롤, 머리글은 붙어 있음
- 통과 — Excel·PDF 내보내기
- 통과 — 관리 — 계층 추가·순서 이동, 체크 항목, 조직, 사용자 소속
- 통과 — 관리 — 단계 템플릿 기본 기간(근무일) 입력
- 통과 — 관리 — 사용자 메일 주소(관리자만 정함), 메일 서버가 없으면 시험 메일은 이유를 알림
- 통과 — 관리 — 공휴일 연도 칩·여러 줄 붙여넣기·저장/삭제 때 일정 영향 안내
- 통과 — 과제 등록 — 단계·작업 CSV 붙여넣기: 샘플 → 미리보기(동시 진행·작업 기준 목표일) → 등록
- 통과 — 단계 시트 — 목표일을 작업 일정에 맞춤(자동): 작업의 가장 늦은 종료일이 목표일, 간트 줄에 작업 기준 표시
- 통과 — 진행 내용 이미지 — 올리면 첨부로 저장되고 본문의 참조 자리에 그림을 그림
- 통과 — 관리 — CSV 붙여넣기: 미리보기(추가·이미 있음·문제) 뒤 문제가 없을 때만 한 번에 추가
- 통과 — 업데이트 — 버전·상태 표시, 설정 저장, 로컬 빌드는 적용 불가 안내
- 통과 — 업데이트 — 서명 검증 표시, 시작되지 않아 되돌린 버전 안내·다시 시도·안내 닫기
- 통과 — 서버 상태 — 관리 화면에서 들어가 점검 결과·첨부 대조·최근 경고를 보고, 첫 설정이 덜 됐으면 점검표
- 통과 — 업데이트 — 지금 백업하면 목록에 남고, 진단 정보를 보고 내려받음
- 통과 — 오류 번호 — 서버 오류 안내와 오류 화면에 번호를 보임
- 통과 — 새 버전 알림 — 서버가 바뀌면 새로 고침 안내, 끊기면 연결 안내
- 통과 — 과제 삭제 — 열려 있던 다른 화면은 삭제됨 띠
- 통과 — 휴지통 — 관리 화면에서 지운 사람·사유를 보고 복원하면 목록·상세가 돌아옴
- 통과 — 조기 경보 — 대시보드 지연 위험 카드·위험/정체 KPI 링크·간트 범위 토글과 조건 기억
- 통과 — 목록 정렬 — 머리글 링크(aria-sort)·반대 방향·정렬 기억·협력사 검색
- 통과 — 내 할 일 — 행에서 바로 진행률·진행 내용 저장, 다른 화면이 먼저 고치면 최신 값으로
- 통과 — 시트 분류 칸 — 작업 시트는 정보·일정·진행, 기존 작업은 진행 칸(빠른 갱신 맨 위)이 먼저, 새 작업은 정보 칸이 먼저, 오류는 그 입력의 칸으로
- 통과 — 단계 순서 — 정렬(시작순·종료순)과 끌어 놓기, 단계 시트는 정보·일정·진행, 새 단계는 작업 일정에 맞춤이 기본
- 통과 — 작업 여러 개 추가 — 붙여넣으면 서버가 해석한 표(문제 줄은 오류 모양)를 보이고, 고치면 n개를 한 번에 저장
- 통과 — 저장 후 계속 추가 — 작업명만 비우고 담당·날짜는 그대로, 포커스는 작업명
- 통과 — 관리 — 단계 아래 작업 템플릿 추가·선행, 등록 화면에 템플릿 작업 n개 함께 만들기
- 통과 — 과제 복제 — 위치(법인 → 하위)와 담당을 정함
- 통과 — 가져오기 — 양식 받기·올리기·미리보기(오류 행)·고쳐서 가져오기
- 통과 — 상황판 — 메뉴에서 새 창으로 열리고, 등록하지 않은 TV PC 에서 1920×1080 한 화면에 맞으며 과제가 바뀌면 다시 받음
- 통과 — 도움말 — 머리의 ? 로 열고, 대시보드 카드의 ? 는 그 절로 감
- 통과 — 완료 목록 — 종료 연도 고르기(기본 올해), 전체 연도로 바꾸면 주소에 남음
- 통과 — 관리 — 사용자 합치기·담당 이관 후 사용 중지·변경 이력·사용 현황
- 통과 — 모바일 대시보드
- 통과 — 모바일 내 할 일
- 통과 — 모바일 내 할 일 바로 갱신 — 진행률 칩(48px)·진행 내용
- 통과 — 모바일 작업 시트 빠른 갱신 — 진행 칸의 칩, 일정은 다른 칸
- 통과 — 모바일 과제 목록
- 통과 — 모바일 과제 상세 — 기본은 목록 보기
- 통과 — 모바일 재계획 — 하단 시트의 표에서 옮겨질 작업을 보고 사유를 넣어 저장하면 단추가 사라짐
- 통과 — 모바일 작업 시트
- 통과 — 모바일 작업 시트 — 선행마다 유형·간격 칸(터치 크기), 근무일 수는 서버 미리보기
- 통과 — 모바일 간트 보기·단계 시트
- 통과 — 모바일 단계 시트 — 목표일을 바꾸면 이후 단계 작업 포함 선택지
- 통과 — 모바일 단계 일정 편집 — 하단 시트, 날짜 두 칸과 간격
- 통과 — 모바일 과제 등록 — 단계 목표일 초안
- 통과 — 모바일 과제 간 연결 — 단계 카드의 경고 칩과 단계 시트의 연결 목록
- 통과 — 모바일 자료·분석·과제 등록·관리 화면
- 통과 — 모바일 분석 확장 — 추세·변경 분석·부하·기간 보고가 화면 안에 들어옴
- 통과 — 모바일 활동 더 보기·과제 전체 이력
- 통과 — 모바일 알림·활동 조건·알릴 사람 고르기 — 하단 탭 화면에서 넘치지 않고 입력은 터치 크기
- 통과 — 모바일 업데이트 — 시작되지 않아 되돌린 버전 안내
- 통과 — 모바일 서버 상태·백업·진단 정보
- 통과 — 모바일 과제 삭제·휴지통 — 하단 시트로 지우고, 휴지통에서 영구 삭제하면 기록에 남음
- 통과 — 모바일 작업 여러 개 추가 — 하단 시트, 줄마다 이름표로 쌓은 표
- 통과 — 모바일 작업 시트 — 저장 후 계속 추가 단추가 하단에서 넘치지 않음
- 통과 — 모바일 가져오기·관리 작업 템플릿·복제 시트
- 통과 — 모바일 도움말·상황판 — 머리의 ? 로 열고 넘치지 않음
- 통과 — 모바일 사용자 바꾸기·관리 사용자 IP
- 통과 — 모바일 관리 — 합치기·이관 시트, 변경 이력, 사용 현황이 화면 안에 들어옴

## 앱 로그 경고·오류

- 1× `warn Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager — No XML encryptor configured. Key {54cc3e58-c29b-4e5a-a121-0344addc5575} may be persisted to storage in unencrypted form.`
- 1× `warn GanttScheduler.Services.TrashService — 과제 영구 삭제: #11 동남아 A제품 1라인 컨베이어 개선 — 삭제 평영 2026-10-17 05:01Z, 영구 삭제 평영`

## 내보내기 파일

- [과제_가져오기_양식.xlsx](exports/과제_가져오기_양식.xlsx)
- [과제목록.xlsx](exports/과제목록.xlsx)
- [과제일정.pdf](exports/과제일정.pdf)
- [과제일정.xlsx](exports/과제일정.xlsx)
- [변경이력.xlsx](exports/변경이력.xlsx)
- [업데이트진단.txt](exports/업데이트진단.txt)
- [월간보고.xlsx](exports/월간보고.xlsx)
- [인쇄_과제상세.pdf](exports/인쇄_과제상세.pdf)
- [인쇄_대시보드.pdf](exports/인쇄_대시보드.pdf)
- [주간보고.pdf](exports/주간보고.pdf)
- [주간보고.xlsx](exports/주간보고.xlsx)

## 모바일 화면

### m01-dashboard

<img src="screens/m01-dashboard.png" width="320">

### m02-my

<img src="screens/m02-my.png" width="320">

### m03-projects

<img src="screens/m03-projects.png" width="320">

### m04-detail-list

<img src="screens/m04-detail-list.png" width="320">

### m04b-replan

<img src="screens/m04b-replan.png" width="320">

### m05-work-sheet

<img src="screens/m05-work-sheet.png" width="320">

### m05b-work-link-type

<img src="screens/m05b-work-link-type.png" width="320">

### m05c-work-sheet-quick

<img src="screens/m05c-work-sheet-quick.png" width="320">

### m06-detail-gantt

<img src="screens/m06-detail-gantt.png" width="320">

### m07-stage-sheet

<img src="screens/m07-stage-sheet.png" width="320">

### m07b-stage-cascade-works

<img src="screens/m07b-stage-cascade-works.png" width="320">

### m07c-schedule-sheet

<img src="screens/m07c-schedule-sheet.png" width="320">

### m07d-cross-link

<img src="screens/m07d-cross-link.png" width="320">

### m08-detail-files

<img src="screens/m08-detail-files.png" width="320">

### m09-insights-weekly

<img src="screens/m09-insights-weekly.png" width="320">

### m09b-trend

<img src="screens/m09b-trend.png" width="320">

### m09c-changes

<img src="screens/m09c-changes.png" width="320">

### m09d-load

<img src="screens/m09d-load.png" width="320">

### m09e-report-month

<img src="screens/m09e-report-month.png" width="320">

### m10-insights-history

<img src="screens/m10-insights-history.png" width="320">

### m11-admin

<img src="screens/m11-admin.png" width="320">

### m11-update

<img src="screens/m11-update.png" width="320">

### m11b-switch-user

<img src="screens/m11b-switch-user.png" width="320">

### m11c-admin-user-ip

<img src="screens/m11c-admin-user-ip.png" width="320">

### m11d-update-start-failure

<img src="screens/m11d-update-start-failure.png" width="320">

### m11e-edit-draft

<img src="screens/m11e-edit-draft.png" width="320">

### m11f-admin-holiday

<img src="screens/m11f-admin-holiday.png" width="320">

### m12-status

<img src="screens/m12-status.png" width="320">

### m12b-update-backup

<img src="screens/m12b-update-backup.png" width="320">

### m13-activity-more

<img src="screens/m13-activity-more.png" width="320">

### m13b-history-project

<img src="screens/m13b-history-project.png" width="320">

### m14-delete-sheet

<img src="screens/m14-delete-sheet.png" width="320">

### m14-my-quick

<img src="screens/m14-my-quick.png" width="320">

### m14-notifications

<img src="screens/m14-notifications.png" width="320">

### m14b-trash

<img src="screens/m14b-trash.png" width="320">

### m14c-trash-log

<img src="screens/m14c-trash-log.png" width="320">

### m15-activity-filter

<img src="screens/m15-activity-filter.png" width="320">

### m15b-mention-box

<img src="screens/m15b-mention-box.png" width="320">

### m19-paste-works

<img src="screens/m19-paste-works.png" width="320">

### m19b-work-continue

<img src="screens/m19b-work-continue.png" width="320">

### m20-import

<img src="screens/m20-import.png" width="320">

### m21-admin-template-works

<img src="screens/m21-admin-template-works.png" width="320">

### m22-clone-location

<img src="screens/m22-clone-location.png" width="320">

### m23-help

<img src="screens/m23-help.png" width="320">

### m24-board

<img src="screens/m24-board.png" width="320">

### m25-admin-merge

<img src="screens/m25-admin-merge.png" width="320">

### m25b-admin-reassign

<img src="screens/m25b-admin-reassign.png" width="320">

### m25c-admin-changes

<img src="screens/m25c-admin-changes.png" width="320">

### m25d-admin-usage

<img src="screens/m25d-admin-usage.png" width="320">

## 데스크톱 화면

### d00-welcome

![](screens/d00-welcome.png)

### d01-dashboard

![](screens/d01-dashboard.png)

### d01b-switch-user

![](screens/d01b-switch-user.png)

### d02-projects

![](screens/d02-projects.png)

### d02b-projects-due

![](screens/d02b-projects-due.png)

### d03-detail-gantt

![](screens/d03-detail-gantt.png)

### d04-stage-sheet-checklist

![](screens/d04-stage-sheet-checklist.png)

### d04b-check-edit

![](screens/d04b-check-edit.png)

### d05-work-sheet-new

![](screens/d05-work-sheet-new.png)

### d06-gantt-drag

![](screens/d06-gantt-drag.png)

### d07-work-sheet-predecessor

![](screens/d07-work-sheet-predecessor.png)

### d08-detail-gantt-links

![](screens/d08-detail-gantt-links.png)

### d08b-plan-preview

![](screens/d08b-plan-preview.png)

### d08c-link-type

![](screens/d08c-link-type.png)

### d08d-replan

![](screens/d08d-replan.png)

### d08e-cross-link

![](screens/d08e-cross-link.png)

### d08f-cross-warn

![](screens/d08f-cross-warn.png)

### d08g-rebaseline

![](screens/d08g-rebaseline.png)

### d08h-baseline-history

![](screens/d08h-baseline-history.png)

### d09-detail-list

![](screens/d09-detail-list.png)

### d10-detail-activity

![](screens/d10-detail-activity.png)

### d11-detail-files

![](screens/d11-detail-files.png)

### d12-conflict

![](screens/d12-conflict.png)

### d12b-work-conflict

![](screens/d12b-work-conflict.png)

### d13-edit-form

![](screens/d13-edit-form.png)

### d13b-edit-draft

![](screens/d13b-edit-draft.png)

### d13c-edit-draft-error

![](screens/d13c-edit-draft-error.png)

### d14-my

![](screens/d14-my.png)

### d15-insights-leadtime

![](screens/d15-insights-leadtime.png)

### d15b-leadtime-zero

![](screens/d15b-leadtime-zero.png)

### d15c-reason-code

![](screens/d15c-reason-code.png)

### d15d-changes

![](screens/d15d-changes.png)

### d15e-trend

![](screens/d15e-trend.png)

### d15f-load

![](screens/d15f-load.png)

### d15g-my-readonly

![](screens/d15g-my-readonly.png)

### d16-insights-weekly

![](screens/d16-insights-weekly.png)

### d16b-weekly-past

![](screens/d16b-weekly-past.png)

### d16c-report-month

![](screens/d16c-report-month.png)

### d16d-report-custom

![](screens/d16d-report-custom.png)

### d17-insights-history

![](screens/d17-insights-history.png)

### d18-admin-category

![](screens/d18-admin-category.png)

### d18b-admin-default-days

![](screens/d18b-admin-default-days.png)

### d18c-admin-holiday-years

![](screens/d18c-admin-holiday-years.png)

### d18d-admin-holiday-paste

![](screens/d18d-admin-holiday-paste.png)

### d18e-admin-reason

![](screens/d18e-admin-reason.png)

### d19-admin-org

![](screens/d19-admin-org.png)

### d20-admin-user

![](screens/d20-admin-user.png)

### d20b-admin-user-ip

![](screens/d20b-admin-user-ip.png)

### d20c-admin-user-email

![](screens/d20c-admin-user-email.png)

### d24-update

![](screens/d24-update.png)

### d24b-update-start-failure

![](screens/d24b-update-start-failure.png)

### d25-new-version

![](screens/d25-new-version.png)

### d30-cascade-works

![](screens/d30-cascade-works.png)

### d30b-gantt-target-cascade-works

![](screens/d30b-gantt-target-cascade-works.png)

### d31-stage-close-works

![](screens/d31-stage-close-works.png)

### d32-stage-closed-works

![](screens/d32-stage-closed-works.png)

### d33-stage-done-open-work

![](screens/d33-stage-done-open-work.png)

### d34-sheet-discard-confirm

![](screens/d34-sheet-discard-confirm.png)

### d35-draft-restore

![](screens/d35-draft-restore.png)

### d36-not-found

![](screens/d36-not-found.png)

### d37-change-banner

![](screens/d37-change-banner.png)

### d38-print-detail

![](screens/d38-print-detail.png)

### d39-print-dashboard

![](screens/d39-print-dashboard.png)

### d40-print-my

![](screens/d40-print-my.png)

### d41-abroad-history

![](screens/d41-abroad-history.png)

### d42-edit-conflict

![](screens/d42-edit-conflict.png)

### d42b-schedule-sheet

![](screens/d42b-schedule-sheet.png)

### d42c-schedule-conflict

![](screens/d42c-schedule-conflict.png)

### d43-status

![](screens/d43-status.png)

### d43b-status-setup

![](screens/d43b-status-setup.png)

### d44-update-backup

![](screens/d44-update-backup.png)

### d45-error-number

![](screens/d45-error-number.png)

### d46-activity-more

![](screens/d46-activity-more.png)

### d46b-history-project

![](screens/d46b-history-project.png)

### d47-dashboard-gantt-scroll

![](screens/d47-dashboard-gantt-scroll.png)

### d48-delete-sheet

![](screens/d48-delete-sheet.png)

### d48-watch-list

![](screens/d48-watch-list.png)

### d48b-trash

![](screens/d48b-trash.png)

### d49-mention-pick

![](screens/d49-mention-pick.png)

### d50-bell-new-count

![](screens/d50-bell-new-count.png)

### d50b-my-news

![](screens/d50b-my-news.png)

### d51-notifications

![](screens/d51-notifications.png)

### d52-activity-deeplink

![](screens/d52-activity-deeplink.png)

### d53-activity-filter

![](screens/d53-activity-filter.png)

### d54-comment-deleted

![](screens/d54-comment-deleted.png)

### d55-dashboard-risk

![](screens/d55-dashboard-risk.png)

### d56-projects-sort

![](screens/d56-projects-sort.png)

### d57-my-quick

![](screens/d57-my-quick.png)

### d58-work-sheet-quick

![](screens/d58-work-sheet-quick.png)

### d58b-stage-drag

![](screens/d58b-stage-drag.png)

### d60-edit-paste-preview

![](screens/d60-edit-paste-preview.png)

### d60-paste-works

![](screens/d60-paste-works.png)

### d61-paste-project

![](screens/d61-paste-project.png)

### d61-work-continue

![](screens/d61-work-continue.png)

### d62-admin-template-works

![](screens/d62-admin-template-works.png)

### d62-stage-auto-target

![](screens/d62-stage-auto-target.png)

### d63-clone-location

![](screens/d63-clone-location.png)

### d63-comment-image

![](screens/d63-comment-image.png)

### d64-admin-csv

![](screens/d64-admin-csv.png)

### d64-import-errors

![](screens/d64-import-errors.png)

### d64b-import-ready

![](screens/d64b-import-ready.png)

### d65-board

![](screens/d65-board.png)

### d66-help

![](screens/d66-help.png)

### d67-list-year

![](screens/d67-list-year.png)

### d70-admin-merge

![](screens/d70-admin-merge.png)

### d71-admin-reassign

![](screens/d71-admin-reassign.png)

### d72-admin-changes

![](screens/d72-admin-changes.png)

### d73-admin-usage

![](screens/d73-admin-usage.png)

