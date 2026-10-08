#!/usr/bin/env bash
# 미리보기 실행 (Codespaces·로컬 공용). 샘플 데이터가 든 SQLite 로 5080 포트에서 띄운다.
#   tools/preview.sh start   백그라운드 실행 (로그: /tmp/gantt-preview.log)
#   tools/preview.sh stop    중지
#   tools/preview.sh reset   데이터 초기화 후 다시 실행
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PID=/tmp/gantt-preview.pid
LOG=/tmp/gantt-preview.log

stop() {
  if [ -f "$PID" ] && kill -0 "$(cat "$PID")" 2>/dev/null; then kill "$(cat "$PID")" || true; fi
  rm -f "$PID"
}

start() {
  stop
  cd "$ROOT/src/GanttScheduler"
  # dotnet run 대신 빌드한 DLL 을 직접 띄운다 — 기록한 PID 가 앱 자신이어야 stop 이 확실히 멈춘다
  dotnet build -v q --nologo > "$LOG" 2>&1 || { cat "$LOG"; exit 1; }
  ASPNETCORE_ENVIRONMENT=Development nohup dotnet bin/Debug/net10.0/GanttScheduler.dll --urls http://0.0.0.0:5080 >> "$LOG" 2>&1 &
  echo $! > "$PID"
  echo "실행 중: http://localhost:5080 (로그: $LOG)"
}

case "${1:-start}" in
  start) start ;;
  stop) stop ;;
  reset) stop; rm -rf "$ROOT/src/GanttScheduler/App_Data"; start ;;
  *) echo "사용법: tools/preview.sh [start|stop|reset]"; exit 1 ;;
esac
