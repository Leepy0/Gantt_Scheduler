#!/usr/bin/env bash
# 업데이트 자체 시험: 실제 프로세스로 "확인 → 받기 → 적용 → 되돌리기"를 끝까지 돌려 본다.
# 옛 버전(0.0.1)을 설치해 띄우고, 폴더에 둔 새 버전(0.0.2)으로 스스로 바뀌는지, 데이터·설정이 남는지 확인한다.
#
#   tools/release/selftest.sh                          # src/GanttScheduler 를 런타임 미포함으로 게시해 시험
#   RID=win-x64 tools/release/selftest.sh              # 런타임 포함(실제 배포와 같은 형태)
#   RID=win-x64 MODE=service tools/release/selftest.sh # Windows 서비스로 등록해 시험 (관리자 권한 필요) — 실제 운영 형태
#   PROJECT=/tmp/gantt-shadow MEMORY_DB=1 tools/release/selftest.sh   # 섀도 빌드로 시험 (NuGet 없는 환경).
#       섀도 빌드는 DB 가 메모리에 있어 다시 시작하면 비므로, DB 보존 확인은 건너뛰고 사용자를 다시 등록한다
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
PROJECT=${PROJECT:-$ROOT/src/GanttScheduler}
WORK=${WORK:-${TMPDIR:-/tmp}/gantt-selftest}
PORT=${PORT:-5097}
URL=http://localhost:$PORT
PY=$(command -v python3 || command -v python)
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

rm -rf "$WORK"; mkdir -p "$WORK"
# Windows(Git Bash)에서는 .NET 이 알아듣는 C:/… 형태의 경로를 쓴다
WORK=$(cd "$WORK" && (pwd -W 2>/dev/null || pwd))
APP=$WORK/app; SRC=$WORK/source; JAR=$WORK/cookies.txt; LOG=$WORK/app.log
SVC=GanttSelfTest
FAILED=0

say()  { echo "[selftest] $*"; }
fail() { echo "::error::[selftest] $*"; FAILED=1; }
die()  { fail "$*"; finish; }
finish() {
  if [ -f "$APP/App_Data/updates/apply.log" ]; then echo "--- apply.log"; cat "$APP/App_Data/updates/apply.log"; fi
  echo "--- app.log (끝부분)"; tail -n 40 "$LOG" 2>/dev/null
  if [ -f "$APP/App_Data/startup-error.log" ]; then echo "--- startup-error.log"; head -n 20 "$APP/App_Data/startup-error.log"; fi
  stop_app
  if [ "$FAILED" = 0 ]; then say "통과"; exit 0; fi
  # Actions 에서는 로그를 내려받지 못하는 환경이 있어, 실패 원인을 주석(annotation)으로도 남긴다
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    detail=$( { tail -n 15 "$WORK/install.log" 2>/dev/null; tail -n 12 "$APP/App_Data/updates/apply.log" 2>/dev/null; head -n 8 "$APP/App_Data/startup-error.log" 2>/dev/null; tail -n 25 "$LOG" 2>/dev/null; } | tr -d '\r' | cut -c1-300 | sed ':a;N;$!ba;s/\n/%0A/g')
    echo "::error title=selftest 로그::$detail"
  fi
  say "실패"; exit 1
}
stop_app() {
  if [ "${MODE:-}" = service ]; then "$APP/GanttScheduler.exe" uninstall --name "$SVC" | tr -d '\r'; fi
  if command -v taskkill > /dev/null 2>&1; then taskkill //F //IM GanttScheduler.exe > /dev/null 2>&1; return 0; fi
  ps -eo pid,args | awk -v p="$APP" 'index($0, p) && !/awk/ {print $1}' | xargs -r kill 2>/dev/null; return 0
}

publish() {  # $1 버전  $2 결과 폴더
  local rid=()
  if [ -n "${RID:-}" ]; then rid=(-r "$RID" --self-contained true); fi
  dotnet publish "$PROJECT" -c Release "${rid[@]}" -o "$2" -v q \
    -p:Version="$1" -p:BuildChannel=release -p:SchemaId=S1 -p:SourceRevisionId="$(echo "$1" | tr -d .)abcdef0" 2>&1 | grep -E "error|Build FAILED" && return 1
  [ -f "$2/GanttScheduler.dll" ]
}
pack() {  # $1 버전  $2 게시 폴더  $3 결과 폴더
  "$PY" "$ROOT/tools/release/pack.py" --dir "$2" --out "$3" --version "$1" --schema S1 --name GanttScheduler-test.zip > /dev/null
}
status()  { curl -s -b "$JAR" -H "Accept: application/json" "$URL/Admin/Update?handler=Status"; }
post()    { curl -s -b "$JAR" -X POST -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -H "Content-Type: application/json" -d '{}' "$URL/Admin/Update?handler=$1"; }
version() { curl -s -m 3 "$URL/version" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p'; }
token()   { curl -s -c "$JAR" -b "$JAR" "$URL$1" | sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' | head -1; }
wait_version() {  # $1 기대 버전  $2 초
  for _ in $(seq 1 "$2"); do [ "$(version)" = "$1" ] && return 0; sleep 1; done; return 1
}
has() { case "$1" in *"$2"*) return 0;; *) return 1;; esac; }

say "게시: 0.0.1(설치할 버전), 0.0.2·0.0.3(새 버전)  project=$PROJECT rid=${RID:-없음}"
publish 0.0.1 "$APP" || die "0.0.1 게시 실패"
publish 0.0.2 "$WORK/p2" || die "0.0.2 게시 실패"
cp -r "$WORK/p2" "$WORK/p3"
echo "옛 버전에만 있는 파일" > "$APP/old-only.txt"
echo "새 버전에만 있는 파일" > "$WORK/p2/new-only.txt"
pack 0.0.1 "$APP" "$WORK/pack1"          # 설치 폴더에 package.files 가 생긴다 (zip 으로 설치한 것과 같은 상태)
pack 0.0.2 "$WORK/p2" "$SRC"
pack 0.0.3 "$WORK/p3" "$WORK/pack3"
rm -f "$APP/release.json"

# 서버마다 다른 것들: 데이터, 손본 설정, 직접 넣은 파일 — 업데이트 뒤에도 그대로여야 한다
mkdir -p "$APP/App_Data"
echo "데이터" > "$APP/App_Data/keep-data.txt"
echo "직접 넣은 파일" > "$APP/custom.txt"
# 설정은 파일로 준다 — 서비스는 이 셸의 환경 변수를 물려받지 않는다
cat > "$APP/appsettings.Production.json" <<JSON
{ "Database": { "Provider": "Sqlite" }, "SelfTest": "keep", "Update": { "Source": "$SRC", "CheckHours": 1000 } }
JSON
echo "<!-- selftest-keep -->" >> "$APP/web.config"

running() {  # 서비스 모드: 서비스가 서비스로서 돌고 있는지
  [ "${MODE:-}" != service ] && return 0
  sc.exe query "$SVC" | grep -q RUNNING
}

export ASPNETCORE_ENVIRONMENT=Production
if [ "${MODE:-}" = service ]; then
  say "서비스 등록·시작 (v0.0.1)"
  # 서비스 계정이 업데이트 폴더를 읽을 수 있게 한다 (프로그램 폴더 권한은 설치 명령이 준다)
  MSYS_NO_PATHCONV=1 icacls "$SRC" /grant "*S-1-1-0:(OI)(CI)RX" /Q > /dev/null
  "$APP/GanttScheduler.exe" install --name "$SVC" --port "$PORT" > "$WORK/install.log" 2>&1; rc=$?
  tr -d '\r' < "$WORK/install.log"
  [ "$rc" = 0 ] || die "서비스 등록 실패"
  running || die "서비스가 실행 중이 아님"
  # 설치 명령을 다시 실행해도(설정 변경) 문제없이 다시 떠야 한다
  "$APP/GanttScheduler.exe" install --name "$SVC" --port "$PORT" > /dev/null || die "서비스 다시 등록 실패"
else
  say "서버 시작 (v0.0.1)"
  if [ -n "${RID:-}" ]; then (cd "$APP" && nohup ./GanttScheduler --urls "$URL" > "$LOG" 2>&1 &)
  else (cd "$APP" && nohup dotnet GanttScheduler.dll --urls "$URL" > "$LOG" 2>&1 &); fi
fi
wait_version 0.0.1 60 || die "서버가 시작되지 않음"

register() {
  local t code
  t=$(token /Welcome)
  code=$(curl -s -o /dev/null -w '%{http_code}' -c "$JAR" -b "$JAR" --data-urlencode "Name=관리자" --data-urlencode "__RequestVerificationToken=$t" "$URL/Welcome")
  [ "$code" = 302 ]
}

say "관리자 등록"
register || die "등록 실패"
TOKEN=$(token /Admin/Update)
[ -n "$TOKEN" ] || die "업데이트 화면을 열 수 없음"

say "확인 → 받기"
r=$(post Check);   has "$r" '"latest":{"version":"0.0.2"' || die "새 버전을 찾지 못함: $r"
r=$(post Download); has "$r" '"ok":true' || die "받기 시작 실패: $r"
for _ in $(seq 1 60); do r=$(status); has "$r" '"staged":{"version":"0.0.2"' && break; sleep 1; done
has "$r" '"staged":{"version":"0.0.2"' || die "받아 두지 못함: $r"
has "$r" '"schemaChange":false' || fail "DB 구조가 같은데 바뀐 것으로 표시됨"

say "적용"
r=$(post Apply); has "$r" '"ok":true' || die "적용 시작 실패: $r"
wait_version 0.0.2 120 || die "새 버전으로 바뀌지 않음 (지금: $(version))"
running                                                 || fail "업데이트 뒤 서비스로 돌고 있지 않음"
[ -f "$APP/new-only.txt" ]                              || fail "새 파일이 복사되지 않음"
[ ! -f "$APP/old-only.txt" ]                            || fail "옛 버전 파일이 남아 있음"
[ -f "$APP/App_Data/keep-data.txt" ]                    || fail "데이터 파일이 사라짐"
[ -f "$APP/custom.txt" ]                                || fail "직접 넣은 파일이 사라짐"
grep -q SelfTest "$APP/appsettings.Production.json"     || fail "서버 설정이 덮어써짐"
grep -q selftest-keep "$APP/web.config"                 || fail "web.config 가 덮어써짐"
[ ! -f "$APP/app_offline.htm" ]                         || fail "점검 안내 파일이 남아 있음"
if [ -n "${MEMORY_DB:-}" ]; then register || die "다시 등록 실패"
else
  code=$(curl -s -o /dev/null -w '%{http_code}' -b "$JAR" "$URL/")
  [ "$code" = 200 ] || fail "업데이트 뒤 등록 정보(DB)가 사라짐 ($code)"
fi
r=$(status)
has "$r" '"previous":{"version":"0.0.1"' || fail "되돌릴 이전 버전이 기록되지 않음: $r"
has "$r" '"kind":"apply","from":"0.0.1","to":"0.0.2"' || fail "적용 이력이 없음"
has "$r" '"staged":null' || fail "적용한 뒤에도 받아 둔 파일이 남음"
# 서버가 바뀌어도 열려 있던 화면의 요청(위조 방지 토큰)이 그대로 통해야 한다
r=$(post Check); has "$r" '"ok":true' || fail "업데이트 뒤 기존 화면의 요청이 거절됨: $r"

say "파일로 올리기 (0.0.3) → 지우기, 잘못된 파일 거절"
r=$(curl -s -b "$JAR" -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -F "file=@$WORK/pack3/GanttScheduler-test.zip" "$URL/Admin/Update?handler=Upload")
has "$r" '"staged":{"version":"0.0.3"' || fail "올린 파일을 받아 두지 못함: $r"
has "$r" '"origin":"upload"' || fail "올린 파일의 출처 표시가 다름"
r=$(post Discard); has "$r" '"staged":null' || fail "받은 파일을 지우지 못함: $r"
echo "zip 이 아님" > "$WORK/bad.zip"
code=$(curl -s -o /dev/null -w '%{http_code}' -b "$JAR" -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -F "file=@$WORK/bad.zip" "$URL/Admin/Update?handler=Upload")
[ "$code" = 400 ] || fail "잘못된 파일을 거절하지 않음 ($code)"
r=$(curl -s -b "$JAR" -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -F "file=@$SRC/GanttScheduler-test.zip" "$URL/Admin/Update?handler=Upload")
has "$r" '"ok":false' || fail "지금과 같은 버전의 파일을 거절하지 않음: $r"

say "되돌리기"
r=$(post Rollback); has "$r" '"ok":true' || die "되돌리기 시작 실패: $r"
wait_version 0.0.1 120 || die "이전 버전으로 돌아가지 않음 (지금: $(version))"
[ -z "${MEMORY_DB:-}" ] || register || die "다시 등록 실패"
running                                                 || fail "되돌린 뒤 서비스로 돌고 있지 않음"
[ -f "$APP/old-only.txt" ]                              || fail "되돌린 뒤 옛 파일이 없음"
[ ! -f "$APP/new-only.txt" ]                            || fail "되돌린 뒤 새 버전 파일이 남음"
[ -f "$APP/App_Data/keep-data.txt" ] && [ -f "$APP/custom.txt" ] || fail "되돌린 뒤 데이터·직접 넣은 파일이 사라짐"
grep -q SelfTest "$APP/appsettings.Production.json"     || fail "되돌린 뒤 서버 설정이 바뀜"
r=$(status)
has "$r" '"previous":null' || fail "되돌린 뒤에도 이전 버전이 남아 있음"
has "$r" '"kind":"rollback","from":"0.0.2","to":"0.0.1"' || fail "되돌리기 이력이 없음"
finish
