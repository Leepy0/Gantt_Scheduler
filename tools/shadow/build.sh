#!/usr/bin/env bash
# 로컬(오프라인) 검증용: NuGet 없이 가짜 EF 로 앱을 빌드한다.
#   tools/shadow/build.sh         빌드만
#   tools/shadow/build.sh run     빌드 후 http://localhost:5080 에서 실행 (데이터는 메모리, 재시작하면 초기화)
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${SHADOW_DIR:-/tmp/gantt-shadow}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$OUT"
# 소스 복사 (빌드 산출물은 유지해 증분 빌드)
find "$OUT" -mindepth 1 -maxdepth 1 ! -name bin ! -name obj -exec rm -rf {} +
cp -r "$ROOT/src/GanttScheduler/." "$OUT/"
# packages.lock.json 은 실제 패키지 기준이라 패키지가 없는 섀도 프로젝트와 맞지 않는다 (Directory.*.props 는 복사하지 않으므로 영향 없음)
rm -rf "$OUT/Data/Migrations" "$OUT/Data/DesignTimeFactory.cs" "$OUT/Services/ExcelExporter.cs" "$OUT/Services/PdfExporter.cs" "$OUT/Services/ProjectImporter.cs" "$OUT/Services/WindowsServiceSupport.cs" "$OUT/App_Data" "$OUT/packages.lock.json"
cp "$ROOT/tools/shadow/FakeEf.cs" "$ROOT/tools/shadow/FakeExcel.cs" "$ROOT/tools/shadow/FakeService.cs" "$OUT/"
cp "$ROOT/tools/shadow/Shadow.csproj" "$OUT/GanttScheduler.csproj"

cd "$OUT"
# 커밋 번호를 넣어 실제 빌드처럼 "버전+커밋" 형태의 빌드 값이 되게 한다 (화면의 새 버전 감지와 같은 조건)
dotnet build -v q --nologo -p:SourceRevisionId="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo 0000000)" 2>&1 | grep -E "error|warning CS|Build succeeded|Error\(s\)|Warning\(s\)" | sort -u || true
if [ "${1:-}" = "run" ]; then
  ASPNETCORE_ENVIRONMENT=Development exec dotnet bin/Debug/net10.0/GanttScheduler.dll --urls "http://localhost:${PORT:-5080}"
fi
