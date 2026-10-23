# shellcheck shell=bash
# NFR-20, ADR-0007, IADR-0605 (#2132): 経路B（ローカル k8s）で作る自製イメージの**不変のタグ**を決める。
#   scripts/k8s-local-up.sh（[2/7] のビルドと [6/7] の helm）と scripts/k8s-local-images.sh が同じ値を使う。
#   chart は global.image.tag が無いと描画を落とす（latest へ黙って倒さない。templates/_image.tpl）。
#
#   - IMAGE_TAG が与えられていれば、それを使う（検査は下の image_tag_validate。latest・空・Docker のタグの形でない値を拒む）。
#   - 無ければ、作業ツリーの HEAD の sha（12 桁）にする。
#   - 🔴 作業ツリーに変更（追跡下の差分・submodule の差分・追跡していない新しいファイル）があれば
#     `<sha>-dirty-<差分の sha256 の先頭 8 桁>` にする。HEAD の sha だけにすると、中身の違うイメージが同じタグで取り込まれ、
#     IfNotPresent の下で「同じタグ＝同じ中身」が崩れる（latest と同じ事故）。差分が同じなら同じタグになる（再実行で増えない）。
#     submodule の中の追跡していないファイルは数えない（git diff --submodule=diff が見ない）。
#
#   使い方:  . scripts/lib/local-image-tag.sh;  IMAGE_TAG="$(resolve_local_image_tag "$ROOT")" || exit 1
#   単体で:  bash scripts/lib/local-image-tag.sh [<repo-root>]   # 決まるタグを 1 行出す（クラスタには触れない）

# <tag> が chart の受け付ける形か（templates/_image.tpl と同じ規則）。違えば理由を stderr へ出して 1。
image_tag_validate() {
  local tag="$1"
  if [ -z "$tag" ]; then
    echo "ERROR: イメージのタグが空です（IADR-0605）。" >&2; return 1
  fi
  if [ "$tag" = "latest" ]; then
    echo "ERROR: イメージのタグに latest は使えません。不変のタグ（git の sha）を使います（NFR-20 / IADR-0605）。" >&2; return 1
  fi
  if ! printf '%s' "$tag" | grep -Eq '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$'; then
    echo "ERROR: イメージのタグ '$tag' は Docker のタグの形ではありません（[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}。IADR-0605）。" >&2; return 1
  fi
}

# sha256 を出す（GNU の sha256sum、無ければ macOS の shasum -a 256。出力の形は同じ「<hash>  <path>」）。
#   経路B は macOS（Rancher Desktop / Docker Desktop）でも走るので、GNU だけの道具（sha256sum・xargs -r）に依らない。
_local_image_tag_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

# <repo-root> の作業ツリーから不変のタグを作る（IMAGE_TAG を見ない）。
local_image_tag() {
  local root="${1:-.}" sha status hash
  if ! sha="$(git -C "$root" rev-parse --short=12 HEAD 2>/dev/null)"; then
    echo "ERROR: $root の git の HEAD を読めません。イメージのタグを作れないので、IMAGE_TAG=<不変のタグ> を与えてください（IADR-0605）。" >&2
    return 1
  fi
  status="$(git -C "$root" status --porcelain --ignore-submodules=none 2>/dev/null)" || {
    echo "ERROR: $root の git status を読めません（IADR-0605）。" >&2; return 1
  }
  if [ -z "$status" ]; then
    printf '%s\n' "$sha"
    return 0
  fi
  hash="$(
    {
      git -C "$root" diff HEAD --binary --submodule=diff
      (cd "$root" && git ls-files -z --others --exclude-standard | while IFS= read -r -d '' f; do _local_image_tag_sha256 "$f"; done)
    } | _local_image_tag_sha256 | cut -c1-8
  )"
  printf '%s-dirty-%s\n' "$sha" "$hash"
}

# IMAGE_TAG があればそれを検査して返し、無ければ local_image_tag の値を返す。
resolve_local_image_tag() {
  local tag
  if [ -n "${IMAGE_TAG:-}" ]; then
    tag="$IMAGE_TAG"
  else
    tag="$(local_image_tag "${1:-.}")" || return 1
  fi
  image_tag_validate "$tag" || return 1
  printf '%s\n' "$tag"
}

# 単体で実行されたら、決まるタグを出す（source されたときは何もしない）。
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  set -euo pipefail
  resolve_local_image_tag "${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
fi
