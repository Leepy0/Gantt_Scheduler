#!/usr/bin/env bash
# 이 레포(비공개)의 정식 릴리스를 공개 배포 레포에 그대로 옮긴다.
# 평소에는 배포 워크플로가 하는 일이다. 시크릿 RELEASE_REPO_TOKEN 이 없거나 만료돼 워크플로의
# "공개 배포 레포에 올리기" 단계가 실패했을 때, 두 레포에 쓸 수 있는 gh 로그인으로 직접 옮기는 용도다.
#
#   tools/release/mirror.sh 0.4.1
set -euo pipefail
VER=${1:?버전을 지정하세요 (예: 0.4.1)}
SRC=${SRC_REPO:-Leepy0/Gantt_Scheduler}
DST=${RELEASE_REPO:-Leepy0/Gantt_Scheduler_Release}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "v$VER: $SRC → $DST"
for name in GanttScheduler-win-x64.zip GanttScheduler-win-x64.zip.sha256 GanttScheduler.json; do
  id=$(gh api "repos/$SRC/releases/tags/v$VER" --jq ".assets[] | select(.name==\"$name\") | .id")
  [ -n "$id" ] || { echo "v$VER 릴리스에 $name 이 없습니다." >&2; exit 1; }
  gh api "repos/$SRC/releases/assets/$id" -H "Accept: application/octet-stream" > "$WORK/$name"
done

# 옮기기 전에 내용 확인: 매니페스트의 버전·해시·크기가 받은 zip 과 맞아야 한다
python3 - "$WORK" "$VER" <<'PY'
import hashlib, json, os, sys
d, ver = sys.argv[1], sys.argv[2]
m = json.load(open(os.path.join(d, "GanttScheduler.json"), encoding="utf-8"))
z = os.path.join(d, m["file"])
sha = hashlib.sha256(open(z, "rb").read()).hexdigest()
assert m["version"] == ver, f"매니페스트 버전이 {m['version']} 입니다"
assert sha == m["sha256"], "zip 의 SHA-256 이 매니페스트와 다릅니다"
assert os.path.getsize(z) == m["size"], "zip 크기가 매니페스트와 다릅니다"
with open(os.path.join(d, "notes.md"), "w", encoding="utf-8") as f:
    f.write("Windows x64 · .NET 런타임 포함\n\n"
            "- 이미 설치된 서버: 스스로 업데이트합니다. 바로 바꾸려면 관리 > 업데이트에서 적용하거나, 아래 zip 을 그 화면에 올립니다.\n"
            "- 처음 설치: zip 을 풀고 install-service.cmd 를 실행합니다 (README 참고).\n\n" + m.get("notes", "") + "\n")
print("확인:", m["version"], sha[:12], m["size"])
PY

gh release delete "v$VER" -R "$DST" --cleanup-tag -y 2>/dev/null || true
gh release create "v$VER" -R "$DST" \
  "$WORK/GanttScheduler-win-x64.zip" "$WORK/GanttScheduler-win-x64.zip.sha256" "$WORK/GanttScheduler.json" \
  --latest --title "v$VER" --notes-file "$WORK/notes.md"
echo "완료: https://github.com/$DST/releases/tag/v$VER"
