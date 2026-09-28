#!/usr/bin/env bash
# NFR-21, ADR-0008, IADR-0471 (#1689): platform-backup イメージのビルドが失敗したときの**原因の分類**。
# `source` して `backup_image_build_cause <ビルドのログのファイル>` を呼ぶ。標準出力へ次の 1 語を出す。
#
#   registry … 資格情報ヘルパーの失敗、またはレジストリ・ミラーへの認証・到達の失敗（age の版の問題ではない）
#   version  … 版の解決の失敗（上流が age の -rN を上げた・ベースの Alpine と食い違う）→ Runbook §6「イメージの版を上げる」
#   unknown  … どちらにも当たらない（断定しない。ログを見てもらう）
#
# 🔴 以前の WARN は原因を「age の版が上がった」に決め打ちし、資格情報ヘルパーの失敗（`error getting credentials`）でも
#    版を上げる手順へ導いた（#1689）。判定はこの関数に閉じ、試験（scripts/k8s-local-up.test.js）が実物の文言で固定する。
#
# 文言の出どころ（実物を確かめた）:
#   - `error getting credentials - err: …` … docker-credential-helpers（client/client.go）
#   - `unable to select packages:` / `unable to select package (or its dependencies)` … apk-tools（src/commit.c / src/app_fetch.c）
#   - `server returned error: HTTP/1.1 404 Not Found` / `bad address '…'` / `download timed out` … busybox wget
#     （Dockerfile は age をミラーのファイル age-<版>.apk で取るため、-rN が消えた日の実際の失敗は wget の 404 である）
#   - `ベースの Alpine（…）が ALPINE_BRANCH=… と違います` … deploy/local/platform-backup/image/Dockerfile 自身
# sha256 の不一致（busybox sha256sum の FAILED）は version に入れない —— 同じ版の中身が変わったことを意味し、
# 版を上げて上書きしてよいものではない。unknown として扱う。

# 資格情報ヘルパー・レジストリの認証・到達の失敗（BuildKit / containerd / busybox wget の文言）。
BACKUP_IMAGE_BUILD_REGISTRY_RE='error getting credentials|failed to authorize|unauthorized|authentication required|pull access denied|toomanyrequests|failed to resolve source metadata|failed to do request|dial tcp|i/o timeout|no such host|TLS handshake timeout|connection refused|bad address|download timed out'
# 版の解決の失敗（apk の 2 形・ミラーの 404・ベースの Alpine の食い違い）。
# 🔴 食い違いは**展開後の値**（ALPINE_BRANCH=v3.24）で引く。BuildKit は失敗した RUN の本文をそのままログへ出すため、
#    字面の `ALPINE_BRANCH=$ALPINE_BRANCH と違います` で引くと、RUN が別の理由で落ちたときも version に倒れる。
BACKUP_IMAGE_BUILD_VERSION_RE='unable to select package|server returned error: HTTP/[0-9.]+ 404|ALPINE_BRANCH=v[0-9][^ ]* と違います'

# backup_image_build_cause <logfile>
# ログが無い・読めないときは unknown（断定しない）。判定の順は registry → version
# （資格情報・到達の失敗はベースのメタデータの段で起き、age を取る RUN まで進まない）。
backup_image_build_cause() {
  local log="${1:-}"
  if [ -z "$log" ] || [ ! -r "$log" ]; then
    echo unknown
    return 0
  fi
  if grep -Eiq -- "$BACKUP_IMAGE_BUILD_REGISTRY_RE" "$log"; then
    echo registry
  elif grep -Eq -- "$BACKUP_IMAGE_BUILD_VERSION_RE" "$log"; then
    echo version
  else
    echo unknown
  fi
}
