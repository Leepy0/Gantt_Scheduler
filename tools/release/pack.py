#!/usr/bin/env python3
"""배포 패키지 만들기: publish 폴더 → zip + SHA-256 + 매니페스트(GanttScheduler.json).

릴리스 워크플로와 업데이트 자체 시험이 같이 쓴다. 서버의 자동 업데이트(Services/UpdateService.cs)가 읽는 형식이므로
항목을 바꿀 때는 그쪽도 같이 본다.

  package.files   패키지에 든 파일 목록 — 업데이트할 때 "앱이 깔아 둔 파일"만 바꾸는 기준
  release.json    zip 안에 넣는 버전 정보 — 파일로 올려 적용할 때 서버가 이것으로 버전을 안다
"""
import argparse
import datetime
import hashlib
import json
import os
import zipfile

p = argparse.ArgumentParser()
p.add_argument("--dir", required=True, help="dotnet publish 결과 폴더")
p.add_argument("--out", required=True, help="결과를 둘 폴더")
p.add_argument("--version", required=True)
p.add_argument("--schema", default="", help="마지막 DB 마이그레이션 이름")
p.add_argument("--rid", default="", help="대상 런타임 (win-x64 등)")
p.add_argument("--name", default="GanttScheduler-win-x64.zip")
p.add_argument("--notes", default="", help="변경 내용 파일")
p.add_argument("--page", default="", help="릴리스 페이지 주소")
a = p.parse_args()

notes = ""
if a.notes and os.path.exists(a.notes):
    with open(a.notes, encoding="utf-8") as f:
        notes = f.read().strip()
published = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
info = {"version": a.version, "schema": a.schema, "rid": a.rid, "published": published, "notes": notes}

with open(os.path.join(a.dir, "release.json"), "w", encoding="utf-8") as f:
    json.dump(info, f, ensure_ascii=False, indent=2)

files = []
for root, _, names in os.walk(a.dir):
    for n in names:
        files.append(os.path.relpath(os.path.join(root, n), a.dir).replace(os.sep, "/"))
if "package.files" not in files:
    files.append("package.files")
files.sort()
with open(os.path.join(a.dir, "package.files"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(files) + "\n")

os.makedirs(a.out, exist_ok=True)
target = os.path.join(a.out, a.name)
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for rel in files:
        z.write(os.path.join(a.dir, rel), rel)

h = hashlib.sha256()
with open(target, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
sha = h.hexdigest()
with open(target + ".sha256", "w", encoding="utf-8", newline="\n") as f:
    f.write(f"{sha}  {a.name}\n")

manifest = dict(info, file=a.name, sha256=sha, size=os.path.getsize(target), page=a.page)
with open(os.path.join(a.out, "GanttScheduler.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
print(json.dumps({k: v for k, v in manifest.items() if k != "notes"}, ensure_ascii=False))
