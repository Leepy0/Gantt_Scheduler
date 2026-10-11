#!/usr/bin/env python3
"""배포 패키지 만들기: publish 폴더 → zip + SHA-256 + 매니페스트(GanttScheduler.json).

릴리스 워크플로와 업데이트 자체 시험이 같이 쓴다. 서버의 자동 업데이트(Services/UpdateService.cs)가 읽는 형식이므로
항목을 바꿀 때는 그쪽도 같이 본다.

  package.files   패키지에 든 파일 목록 — 업데이트할 때 "앱이 깔아 둔 파일"만 바꾸는 기준
  release.json    zip 안에 넣는 버전 정보 — 파일로 올려 적용할 때 서버가 이것으로 버전을 안다
  GanttScheduler.json.sig
                  매니페스트에 대한 분리 서명(ECDSA P-256, SHA-256, DER). 환경 변수 RELEASE_SIGNING_KEY(개인키 PEM)가
                  있을 때만 만든다. 공개키(src/GanttScheduler/update-key.pub)가 든 서버는 이 서명이 맞아야 받는다.
                  서명은 매니페스트 파일의 바이트 그대로에 대한 것이므로, 만든 뒤 매니페스트를 고치면 안 된다
"""
import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import zipfile

# Windows 러너의 파이썬은 표준 출력이 cp1252 라 한글 안내를 찍다가 죽는다. 출력만 UTF-8 로 고정한다
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

p = argparse.ArgumentParser()
p.add_argument("--dir", help="dotnet publish 결과 폴더")
p.add_argument("--out", help="결과를 둘 폴더")
p.add_argument("--version")
p.add_argument("--schema", default="", help="마지막 DB 마이그레이션 이름")
p.add_argument("--rid", default="", help="대상 런타임 (win-x64 등)")
p.add_argument("--name", default="GanttScheduler-win-x64.zip")
p.add_argument("--notes", default="", help="변경 내용 파일")
p.add_argument("--page", default="", help="릴리스 페이지 주소")
p.add_argument("--require-signature", action="store_true", help="서명 키가 없으면 실패한다 (정식 배포)")
p.add_argument("--verify-key", default="", help="만든 서명을 이 공개키 파일로 확인한다 (키가 여러 개면 하나라도 맞으면 된다)")
p.add_argument("--check", default="", help="패키지를 만들지 않고, 이 매니페스트와 옆의 .sig 를 --verify-key 로 확인만 한다")
a = p.parse_args()
if not a.check and not (a.dir and a.out and a.version):
    p.error("--dir, --out, --version 이 필요합니다")


def openssl(*args):
    r = subprocess.run(["openssl", *args], capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr).strip()


def verify(manifest, sig, key_file):
    """공개키 파일에 여러 키가 있을 수 있다(키 교체 중). 하나라도 맞으면 서버가 받는다"""
    with open(key_file, encoding="ascii") as f:
        blocks = re.findall(r"-----BEGIN PUBLIC KEY-----.+?-----END PUBLIC KEY-----", f.read(), re.S)
    for block in blocks:
        fd, pub_path = tempfile.mkstemp(suffix=".pub")
        try:
            with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
                f.write(block + "\n")
            code, _ = openssl("dgst", "-sha256", "-verify", pub_path, "-signature", sig, manifest)
            if code == 0:
                return True
        finally:
            os.remove(pub_path)
    return False


if a.check:
    if not a.verify_key or not verify(a.check, a.check + ".sig", a.verify_key):
        sys.exit(f"{a.check} 의 서명을 {a.verify_key or '(공개키 없음)'} 로 확인하지 못했습니다.")
    print("서명 확인: " + a.check)
    sys.exit(0)

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
manifest_path = os.path.join(a.out, "GanttScheduler.json")
with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
print(json.dumps({k: v for k, v in manifest.items() if k != "notes"}, ensure_ascii=False))


def private_key_pem(text):
    """시크릿에 PEM 을 그대로 넣었거나, 줄바꿈이 사라지지 않게 base64 로 한 번 더 감싸 넣은 경우 모두 받는다"""
    text = text.strip()
    if "BEGIN" not in text:
        text = base64.b64decode(text).decode("ascii").strip()
    return text + "\n"


key = os.environ.get("RELEASE_SIGNING_KEY", "").strip()
sig_path = manifest_path + ".sig"
if not key:
    if os.path.exists(sig_path):
        os.remove(sig_path)   # 예전에 만든 서명이 다른 매니페스트에 붙어 나가지 않게
    if a.require_signature:
        sys.exit("서명 키(RELEASE_SIGNING_KEY)가 없습니다.")
    print("서명 없음 (RELEASE_SIGNING_KEY 없음)")
else:
    # 개인키는 이 프로세스만 읽을 수 있는 임시 파일에 잠깐 두고 바로 지운다
    fd, key_path = tempfile.mkstemp(suffix=".pem")
    try:
        with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
            f.write(private_key_pem(key))
        code, out = openssl("dgst", "-sha256", "-sign", key_path, "-out", sig_path, manifest_path)
        if code != 0:
            sys.exit("서명을 만들지 못했습니다: " + out)
    finally:
        os.remove(key_path)
    print("서명: " + os.path.basename(sig_path))

    if a.verify_key:
        if not verify(manifest_path, sig_path, a.verify_key):
            os.remove(sig_path)
            sys.exit(f"서명이 {a.verify_key} 의 공개키와 맞지 않습니다. 시크릿 RELEASE_SIGNING_KEY 가 이 저장소의 공개키와 짝인지 확인하세요.")
        print("서명 확인: " + a.verify_key)
