#!/usr/bin/env bash
# 배포 파일 서명 키 만들기 (처음 한 번). openssl 로 ECDSA P-256 키 쌍을 만든다.
#   공개키 → src/GanttScheduler/update-key.pub (커밋한다. 이 파일이 든 빌드부터 서명된 업데이트만 받는다)
#   개인키 → 화면에 출력 (+ 지정하면 파일). GitHub 시크릿 RELEASE_SIGNING_KEY 로 등록하고 이 컴퓨터에는 남기지 않는다
#
#   tools/release/keygen.sh                  # 새 키. 이미 공개키가 있으면 멈춘다
#   tools/release/keygen.sh --rotate         # 키 바꾸기: 새 공개키를 기존 공개키 뒤에 덧붙인다 (README "서명 키" 참고)
#   KEY_OUT=<파일> tools/release/keygen.sh   # 개인키를 화면 대신 파일로 (권한 600)
#
# 개인키가 새면 누구나 서버가 받는 업데이트를 만들 수 있다. 저장소·채팅·메일에 붙이지 않는다
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
PUB=$ROOT/src/GanttScheduler/update-key.pub
ROTATE=0
[ "${1:-}" = "--rotate" ] && ROTATE=1

command -v openssl > /dev/null || { echo "openssl 이 필요합니다."; exit 1; }
if [ -f "$PUB" ] && [ "$ROTATE" = 0 ]; then
  echo "이미 공개키가 있습니다: $PUB"
  echo "키를 바꾸려면 --rotate 로 실행합니다 (기존 키를 지우면 이미 설치된 서버가 새 업데이트를 받지 못합니다)."
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
umask 077
openssl ecparam -name prime256v1 -genkey -noout -out "$TMP/key.pem"
openssl ec -in "$TMP/key.pem" -pubout -out "$TMP/key.pub" 2> /dev/null

# 만든 키로 서명·확인이 되는지 본다 (서버와 같은 형식: SHA-256, DER)
echo '{"version":"0.0.0"}' > "$TMP/test.json"
openssl dgst -sha256 -sign "$TMP/key.pem" -out "$TMP/test.sig" "$TMP/test.json"
openssl dgst -sha256 -verify "$TMP/key.pub" -signature "$TMP/test.sig" "$TMP/test.json" > /dev/null

if [ "$ROTATE" = 1 ] && [ -f "$PUB" ]; then cat "$TMP/key.pub" >> "$PUB"; else cp "$TMP/key.pub" "$PUB"; fi
chmod 644 "$PUB"
echo "공개키: $PUB"
echo

if [ -n "${KEY_OUT:-}" ]; then
  cp "$TMP/key.pem" "$KEY_OUT"; chmod 600 "$KEY_OUT"
  echo "개인키: $KEY_OUT (시크릿으로 등록한 뒤 지우세요)"
else
  echo "아래 개인키 전체(BEGIN·END 줄 포함)를 GitHub › 이 레포 › Settings › Secrets and variables › Actions ›"
  echo "New repository secret 에 이름 RELEASE_SIGNING_KEY 로 붙여 넣으세요. 이 화면을 닫으면 다시 볼 수 없습니다."
  echo
  cat "$TMP/key.pem"
fi
echo
if [ "$ROTATE" = 1 ]; then
  echo "다음: 1) update-key.pub 를 커밋하고, 시크릿은 아직 바꾸지 않은 채 정식 배포 → 서버가 새 공개키가 든 버전을 받는다"
  echo "      2) 그 뒤 시크릿 RELEASE_SIGNING_KEY 를 새 개인키로 바꾼다 3) 모든 서버가 올라온 뒤 update-key.pub 에서 옛 키를 지운다"
else
  echo "다음: update-key.pub 를 커밋하고 시크릿을 등록한 뒤 정식 배포한다. 이 버전부터 서버는 서명이 맞는 업데이트만 받는다."
fi
