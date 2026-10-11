#!/usr/bin/env bash
# 업데이트 자체 시험: 실제 프로세스로 "확인 → 받기 → 적용 → 되돌리기"를 끝까지 돌려 본다.
# 옛 버전(0.0.1)을 설치해 띄우고, 폴더에 둔 새 버전(0.0.2)으로 스스로 바뀌는지, 데이터·설정이 남는지 확인한다.
# 마지막으로 시작하자마자 죽는 버전(0.0.4, 깨진 appsettings.json)을 적용해 도우미가 이전 버전으로 자동으로 되돌리는지 본다.
# openssl 이 있으면 시험용 서명 키를 만들어 공개키를 넣은 빌드로 시험한다(서명된 배포 정보만 받는지, 서명 없는 업로드를 거절하는지).
#
#   tools/release/selftest.sh                          # src/GanttScheduler 를 런타임 미포함으로 게시해 시험
#   RID=win-x64 tools/release/selftest.sh              # 런타임 포함(실제 배포와 같은 형태)
#   RID=win-x64 MODE=service tools/release/selftest.sh # Windows 서비스로 등록해 시험 (관리자 권한 필요) — 실제 운영 형태
#   PROJECT=/tmp/gantt-shadow MEMORY_DB=1 tools/release/selftest.sh   # 섀도 빌드로 시험 (NuGet 없는 환경).
#       섀도 빌드는 DB 가 메모리에 있어 다시 시작하면 비므로, DB 보존 확인은 건너뛰고 사용자를 다시 등록한다
#   SIGN=0 tools/release/selftest.sh                   # 서명 없이 시험 (공개키 없는 빌드)
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
# 서명 시험: openssl 이 있으면 켠다. 시험용 키는 이 시험 폴더에만 만든다 (저장소의 update-key.pub 는 건드리지 않는다)
if [ -z "${SIGN:-}" ]; then if command -v openssl > /dev/null 2>&1; then SIGN=1; else SIGN=0; fi; fi
KEYARG=(-p:UpdateKeyFile=)   # 서명 없이 시험할 때는 저장소의 공개키가 있어도 넣지 않는다
if [ "$SIGN" = 1 ]; then
  openssl ecparam -name prime256v1 -genkey -noout -out "$WORK/sign.pem" 2> /dev/null \
    && openssl ec -in "$WORK/sign.pem" -pubout -out "$WORK/update-key.pub" 2> /dev/null || { echo "::error::[selftest] 시험용 서명 키를 만들지 못함"; exit 1; }
  KEYARG=(-p:UpdateKeyFile="$WORK/update-key.pub")
fi

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

# 패키지는 잠금 파일(packages.lock.json)대로 한 번만 복원한다 — 테스트·배포와 같은 패키지로 시험하기 위해 (섀도 빌드는 잠금 파일이 없다)
RESTORED=0
restore() {
  [ "$RESTORED" = 1 ] && return 0
  if [ -f "$PROJECT/packages.lock.json" ]; then
    # 잠금 파일에는 게시 대상(win-x64·linux-x64)까지 들어 있다 (GanttScheduler.csproj 의 RuntimeIdentifiers)
    dotnet restore "$PROJECT" --locked-mode -v q 2>&1 | grep -E "error" && return 1
  else
    local rid=()
    if [ -n "${RID:-}" ]; then rid=(-r "$RID"); fi
    dotnet restore "$PROJECT" "${rid[@]}" -v q 2>&1 | grep -E "error" && return 1
  fi
  RESTORED=1
}
publish() {  # $1 버전  $2 결과 폴더
  local rid=()
  if [ -n "${RID:-}" ]; then rid=(-r "$RID" --self-contained true); fi
  restore || return 1
  dotnet publish "$PROJECT" -c Release --no-restore "${rid[@]}" "${KEYARG[@]}" -o "$2" -v q \
    -p:Version="$1" -p:BuildChannel=release -p:SchemaId=S1 -p:SourceRevisionId="$(echo "$1" | tr -d .)abcdef0" 2>&1 | grep -E "error|Build FAILED" && return 1
  [ -f "$2/GanttScheduler.dll" ]
}
pack() {  # $1 버전  $2 게시 폴더  $3 결과 폴더
  if [ "$SIGN" = 1 ]; then
    RELEASE_SIGNING_KEY="$(cat "$WORK/sign.pem")" "$PY" "$ROOT/tools/release/pack.py" --dir "$2" --out "$3" --version "$1" --schema S1 \
      --name GanttScheduler-test.zip --require-signature --verify-key "$WORK/update-key.pub" > /dev/null
  else
    RELEASE_SIGNING_KEY= "$PY" "$ROOT/tools/release/pack.py" --dir "$2" --out "$3" --version "$1" --schema S1 --name GanttScheduler-test.zip > /dev/null
  fi
}
# 파일로 올리기: zip 과 (있으면) 같은 폴더의 배포 정보·서명을 함께 올린다
upload() {  # $1 패키지 폴더  [$2 = zip 만]
  local files=(-F "file=@$1/GanttScheduler-test.zip")
  if [ -z "${2:-}" ]; then
    files+=(-F "file=@$1/GanttScheduler.json")
    [ -f "$1/GanttScheduler.json.sig" ] && files+=(-F "file=@$1/GanttScheduler.json.sig")
  fi
  curl -s -b "$JAR" -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" "${files[@]}" "$URL/Admin/Update?handler=Upload"
}
# JSON 응답의 값 하나 (예: jget "$r" status.history.0.ok). 없으면 빈 문자열
jget() {
  "$PY" - "$1" "$2" <<'PYEOF'
import json, sys
try:
    v = json.loads(sys.argv[1])
    for k in sys.argv[2].split('.'):
        v = v[int(k)] if isinstance(v, list) else v[k]
    print(json.dumps(v, ensure_ascii=False) if not isinstance(v, str) else v)
except Exception:
    print("")
PYEOF
}
status()  { curl -s -b "$JAR" -H "Accept: application/json" "$URL/Admin/Update?handler=Status"; }
post()    { curl -s -b "$JAR" -X POST -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -H "Content-Type: application/json" -d '{}' "$URL/Admin/Update?handler=$1"; }
version() { curl -s -m 3 "$URL/version" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p'; }
token()   { curl -s -c "$JAR" -b "$JAR" "$URL$1" | sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' | head -1; }
wait_version() {  # $1 기대 버전  $2 초
  for _ in $(seq 1 "$2"); do [ "$(version)" = "$1" ] && return 0; sleep 1; done; return 1
}
has() { case "$1" in *"$2"*) return 0;; *) return 1;; esac; }

say "게시: 0.0.1(설치할 버전), 0.0.2·0.0.3(새 버전), 0.0.4(시작하자마자 죽는 버전)  project=$PROJECT rid=${RID:-없음} 서명=$SIGN"
publish 0.0.1 "$APP" || die "0.0.1 게시 실패"
publish 0.0.2 "$WORK/p2" || die "0.0.2 게시 실패"
cp -r "$WORK/p2" "$WORK/p3"
cp -r "$WORK/p2" "$WORK/p4"
# 0.0.4: 설정 파일이 깨져 시작하자마자 죽는다 (appsettings.json 은 서버별 설정이 아니라 교체 대상이다)
printf '{ "Logging": ' > "$WORK/p4/appsettings.json"
echo "옛 버전에만 있는 파일" > "$APP/old-only.txt"
echo "새 버전에만 있는 파일" > "$WORK/p2/new-only.txt"
pack 0.0.1 "$APP" "$WORK/pack1" || die "0.0.1 패키지 실패"   # 설치 폴더에 package.files 가 생긴다 (zip 으로 설치한 것과 같은 상태)
pack 0.0.2 "$WORK/p2" "$SRC" || die "0.0.2 패키지 실패"
pack 0.0.3 "$WORK/p3" "$WORK/pack3" || die "0.0.3 패키지 실패"
pack 0.0.4 "$WORK/p4" "$WORK/pack4" || die "0.0.4 패키지 실패"
rm -f "$APP/release.json"

# 서버마다 다른 것들: 데이터, 손본 설정, 직접 넣은 파일 — 업데이트 뒤에도 그대로여야 한다
mkdir -p "$APP/App_Data"
echo "데이터" > "$APP/App_Data/keep-data.txt"
echo "직접 넣은 파일" > "$APP/custom.txt"
# 설정은 파일로 준다 — 서비스는 이 셸의 환경 변수를 물려받지 않는다
# VerifySeconds: 새 버전이 이 시간 안에 시작되지 않으면 되돌린다 (시험 시간을 줄이려고 기본 120초보다 짧게)
cat > "$APP/appsettings.Production.json" <<JSON
{ "Database": { "Provider": "Sqlite" }, "SelfTest": "keep", "Update": { "Source": "$SRC", "CheckHours": 1000, "VerifySeconds": 60 } }
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
  # SYSTEM 이 아니라 전용 계정으로 돌아야 한다 (업데이트 뒤 재시작도 이 계정의 권한으로 한다)
  sc.exe qc "$SVC" | tr -d '\r' | grep -i "SERVICE_START_NAME" | grep -qi "NT SERVICE.$SVC" || fail "서비스가 전용 계정으로 등록되지 않음: $(sc.exe qc "$SVC" | tr -d '\r' | grep -i START_NAME)"
  # 설치 명령을 다시 실행해도(설정 변경) 문제없이 다시 떠야 한다
  "$APP/GanttScheduler.exe" install --name "$SVC" --port "$PORT" > "$WORK/install.log" 2>&1 || die "서비스 다시 등록 실패"
  sc.exe qc "$SVC" | tr -d '\r' | grep -i "SERVICE_START_NAME" | grep -qi "NT SERVICE.$SVC" || fail "다시 등록한 뒤 계정이 바뀜"
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
if [ "$SIGN" = 1 ]; then [ "$(jget "$r" status.signatureCheck)" = true ] || fail "서명 검증이 켜져 있지 않음: $r"; fi
# 응답 JSON 은 한글을 \uXXXX 로 내보내므로 영문 부분으로 확인한다: "host":"Windows 서비스 (이름)"
if [ "${MODE:-}" = service ]; then { has "$r" '"host":"Windows ' && has "$r" "($SVC)\""; } || fail "실행 방식이 서비스로 표시되지 않음"; fi
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
if [ "$SIGN" = 1 ]; then
  # 서명을 확인하는 서버: zip 만 올리거나 서명이 맞지 않으면 거절한다
  r=$(upload "$WORK/pack3" zip-only); has "$r" '"ok":false' || fail "서명 없이 올린 zip 을 받음: $r"
  cp -r "$WORK/pack3" "$WORK/pack3-bad"; cp "$WORK/pack1/GanttScheduler.json.sig" "$WORK/pack3-bad/GanttScheduler.json.sig"
  r=$(upload "$WORK/pack3-bad"); has "$r" '"ok":false' || fail "다른 배포 정보의 서명으로 올린 파일을 받음: $r"
fi
r=$(upload "$WORK/pack3")
has "$r" '"staged":{"version":"0.0.3"' || fail "올린 파일을 받아 두지 못함: $r"
has "$r" '"origin":"upload"' || fail "올린 파일의 출처 표시가 다름"
r=$(post Discard); has "$r" '"staged":null' || fail "받은 파일을 지우지 못함: $r"
echo "zip 이 아님" > "$WORK/bad.zip"
code=$(curl -s -o /dev/null -w '%{http_code}' -b "$JAR" -H "X-CSRF-TOKEN: $TOKEN" -H "Accept: application/json" -F "file=@$WORK/bad.zip" "$URL/Admin/Update?handler=Upload")
[ "$code" = 400 ] || fail "잘못된 파일을 거절하지 않음 ($code)"
r=$(upload "$SRC")
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

say "시작하자마자 죽는 버전 (0.0.4) → 도우미가 이전 버전(0.0.1)으로 자동으로 되돌림"
cp "$WORK/pack4/"* "$SRC/"
r=$(post Check); has "$r" '"latest":{"version":"0.0.4"' || die "0.0.4 를 찾지 못함: $r"
r=$(post Download); has "$r" '"ok":true' || die "0.0.4 받기 시작 실패: $r"
for _ in $(seq 1 60); do r=$(status); has "$r" '"staged":{"version":"0.0.4"' && break; sleep 1; done
has "$r" '"staged":{"version":"0.0.4"' || die "0.0.4 를 받아 두지 못함: $r"
r=$(post Apply); has "$r" '"ok":true' || die "0.0.4 적용 시작 실패: $r"
# 서버가 내려갔다가(새 버전 시작 실패) 이전 버전으로 다시 뜰 때까지. 서비스는 시작 재시도(최대 90초) 뒤에 되돌린다
back=0
for _ in $(seq 1 400); do
  sleep 1
  [ "$(version)" = 0.0.1 ] || continue
  r=$(status)
  if [ -n "${MEMORY_DB:-}" ] && ! has "$r" '"ok":true'; then register > /dev/null 2>&1; r=$(status); fi
  if [ "$(jget "$r" status.startFailure.version)" = 0.0.4 ]; then back=1; break; fi
done
[ "$back" = 1 ] || die "시작되지 않는 새 버전을 되돌리지 않음 (지금: $(version)): $r"
running                                                  || fail "자동 되돌림 뒤 서비스로 돌고 있지 않음"
[ "$(jget "$r" status.startFailure.rolledBack)" = true ] || fail "되돌렸다고 표시되지 않음: $r"
[ "$(jget "$r" status.startFailure.retry)" = apply ]     || fail "받아 둔 0.0.4 로 다시 시도할 수 없음: $r"
[ "$(jget "$r" status.skipAuto)" = 0.0.4 ]               || fail "실패한 버전이 자동 적용 대상에서 빠지지 않음: $r"
[ "$(jget "$r" status.history.0.to)" = 0.0.4 ] && [ "$(jget "$r" status.history.0.ok)" = false ] || fail "실패 이력이 없음: $r"
[ "$(jget "$r" status.previous)" = null ] || [ -z "$(jget "$r" status.previous)" ] || fail "되돌린 뒤 이전 버전 기록이 남음: $r"
"$PY" -c "import json,sys; json.load(open(sys.argv[1], encoding='utf-8-sig'))" "$APP/appsettings.json" || fail "깨진 설정 파일이 남아 있음"
[ -f "$APP/App_Data/keep-data.txt" ] && [ -f "$APP/custom.txt" ] || fail "자동 되돌림 뒤 데이터·직접 넣은 파일이 사라짐"
grep -q SelfTest "$APP/appsettings.Production.json"      || fail "자동 되돌림 뒤 서버 설정이 바뀜"
grep -q "시작되지 않음" "$APP/App_Data/updates/apply.log"   || fail "apply.log 에 시작 실패 기록이 없음"
say "실패 원인: $(jget "$r" status.startFailure.error) / $(jget "$r" status.startFailure.detail | head -n 3 | tr '\n' ' ' | cut -c1-200)"
r=$(post DismissFailure); [ -z "$(jget "$r" status.startFailure)" ] || [ "$(jget "$r" status.startFailure)" = null ] || fail "시작 실패 안내를 닫지 못함: $r"
finish
