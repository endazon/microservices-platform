#!/usr/bin/env bash
# NFR-18, 計画 ADR-0107 決定 3 / ADR-0135 決定 2, IADR-0679 (#2410):
# **起動器が取り込む外部の成果物（helm chart の tgz・URL で取り込むマニフェスト）を、版に加えて内容の sha256 で固定する口。**
# `source` して使う。呼び出し元は scripts/k8s-local-up.sh と scripts/istio-edge-up.sh。
#
# 版は lib/pin-version.sh が 1 つに決める（イメージの digest と対）。ここは「その版の中身が宣言どおりか」を見る:
#   - chart: 取得元の URL から tgz を一時ディレクトリへ取り、sha256 を下の表と突き合わせる。合わなければ何も当てずに止まる。
#     helm へは取得した tgz のパスを渡す（chart のリポジトリ〔helm repo add〕を経由しない）。
#   - マニフェスト: kustomization の resources に書いた URL を取り、sha256 を同じディレクトリの `remote-resources.sha256`
#     （1 行「<sha256>  <URL>」）と突き合わせる。合えば kustomization の写しの URL を取得したファイルへ差し替えて当てる。
# 🔴 版を上げるときは、pin-version.sh の版・置き場のイメージの tag@digest・下の表（または remote-resources.sha256）を対で改める
#    （scripts/scripts.repo.test.js が突き合わせる）。sha256 は取得元から 2 回取って一致することを確かめてから書く。

# pinned_work_init → 作業ディレクトリ PINNED_WORK を決める。既定は mktemp -d（終了のときに消す）。
#   PINNED_ARTIFACTS_DIR を与えたときはそこを使い、消さない（試験が起動器の出すコマンドを決定的に比べるため。中身は毎回取り直して確かめる）。
#   🔴 呼び出し側の EXIT trap を 1 つにするため、ここで trap を張る（呼び出し側は自分の EXIT trap を持たない）。
pinned_work_init() {
  if [ -n "${PINNED_ARTIFACTS_DIR:-}" ]; then
    PINNED_WORK="$PINNED_ARTIFACTS_DIR"
    mkdir -p "$PINNED_WORK" || exit 1
  else
    PINNED_WORK="$(mktemp -d)" || exit 1
    # shellcheck disable=SC2064  # 値をこの時点で固定する
    trap "rm -rf '$PINNED_WORK'" EXIT
  fi
}

# pinned_chart_entry <chart> <版> → 「<URL> <sha256>」。表に無ければ 1（固定の無い chart は取り込まない）。
pinned_chart_entry() {
  case "$1@$2" in
    base@1.30.4) printf '%s %s' 'https://istio-release.storage.googleapis.com/charts/base-1.30.4.tgz' 'd3532767f9f2042ad1a1f7ca060c2b734762c8d1bcd57fa0244482359146a5f1' ;;
    istiod@1.30.4) printf '%s %s' 'https://istio-release.storage.googleapis.com/charts/istiod-1.30.4.tgz' '66ad8344e2ef16745aa425dc6609570b42536ecdf1ab5ca04a90abf1bc577344' ;;
    gateway@1.30.4) printf '%s %s' 'https://istio-release.storage.googleapis.com/charts/gateway-1.30.4.tgz' 'b662db852bfec2994725eb478c2e89fe6e73729aeb7ab90352d8042928fa20ce' ;;
    external-secrets@2.8.0) printf '%s %s' 'https://github.com/external-secrets/external-secrets/releases/download/helm-chart-2.8.0/external-secrets-2.8.0.tgz' '251e4615013c6d2f9ade5cedf1cd8615613f286bfc381e44fb005f197e611ecd' ;;
    reloader@2.2.17) printf '%s %s' 'https://stakater.github.io/stakater-charts/reloader-2.2.17.tgz' 'a17ee91d66785c29c17e5dfd038c13b0e35b23301b33f8fdb431b403d8a7a071' ;;
    *) return 1 ;;
  esac
}

# pinned_sha256 <ファイル> → sha256（16 進）。GNU の sha256sum、無ければ macOS の shasum -a 256（lib/local-image-tag.sh と同じ。
#   経路 B は macOS でも走るので、GNU だけの道具に依らない）。
pinned_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1
}

# pinned_fetch_verified <URL> <sha256> <出力ファイル> → 取って sha256 を突き合わせる。合わなければ消して 1。
pinned_fetch_verified() {
  local url="$1" want="$2" out="$3" got
  if ! curl -fsSL --retry 3 -o "$out" "$url"; then
    echo "ERROR: 外部の成果物を取得できない: ${url}" >&2
    rm -f "$out"
    return 1
  fi
  got="$(pinned_sha256 "$out")"
  if [ "$got" != "$want" ]; then
    echo "ERROR: 外部の成果物の sha256 が固定と違う（何も当てずに止める）: ${url}" >&2
    echo "       固定: ${want}" >&2
    echo "       取得: ${got}" >&2
    echo "       版を上げたなら scripts/lib/pinned-artifacts.sh（または remote-resources.sha256）を対で改める（IADR-0679）。" >&2
    rm -f "$out"
    return 1
  fi
}

# pinned_chart <chart> <版> <作業ディレクトリ> → 確かめた tgz のパスを標準出力へ書く（失敗は 1）。
pinned_chart() {
  local chart="$1" version="$2" dir="$3" entry url sha out
  if ! entry="$(pinned_chart_entry "$chart" "$version")"; then
    echo "ERROR: chart ${chart} ${version} の sha256 の固定が無い（scripts/lib/pinned-artifacts.sh。IADR-0679）" >&2
    return 1
  fi
  url="${entry% *}"
  sha="${entry#* }"
  out="${dir}/${chart}-${version}.tgz"
  pinned_fetch_verified "$url" "$sha" "$out" || return 1
  printf '%s' "$out"
}

# pinned_kustomize_dir <kustomization のディレクトリ（リポジトリの根からの相対）> <作業ディレクトリ>
#   <作業ディレクトリ>/<同じ相対パス> へ写し、resources の URL のうち remote-resources.sha256 に在るものを、取得して確かめた
#   ファイルへ差し替える。呼び出し側は `( cd <作業ディレクトリ> && kubectl apply -k <同じ相対パス> )` で当てる。
#   kustomization に URL が残る（表に無い URL）なら 1（確かめていないものを当てない）。
pinned_kustomize_dir() {
  local src="$1" work="$2" dst list sha url name n=0
  case "$src" in /* | *..*) echo "ERROR: pinned_kustomize_dir はリポジトリの根からの相対パスだけを受ける: ${src}" >&2; return 1 ;; esac
  list="${src}/remote-resources.sha256"
  dst="${work}/${src}"
  mkdir -p "$dst"
  cp -R "${src}/." "$dst/"
  if [ -f "$list" ]; then
    while read -r sha url; do
      case "$sha" in '' | '#'*) continue ;; esac
      n=$((n + 1))
      name="remote-${n}.yaml"
      pinned_fetch_verified "$url" "$sha" "${dst}/${name}" || return 1
      # URL の行を、取得したファイルの名前へ差し替える。URL は文字列として比べる（正規表現として扱わない）。
      #   sed -i は GNU と BSD（macOS）で形が違うので使わず、awk で一時ファイルへ書いてから置き換える。
      awk -v url="$url" -v name="$name" '{
        line = $0; t = line; sub(/^[ \t]*-[ \t]*/, "", t); sub(/[ \t]*$/, "", t)
        if (t == url && match(line, /^[ \t]*-[ \t]*/)) { print substr(line, 1, RLENGTH) name } else { print line }
      }' "${dst}/kustomization.yaml" > "${dst}/kustomization.yaml.tmp" && mv "${dst}/kustomization.yaml.tmp" "${dst}/kustomization.yaml" || return 1
    done < "$list"
  fi
  if grep -qE '^[[:space:]]*-[[:space:]]*[a-z][a-z0-9+.-]*://' "${dst}/kustomization.yaml"; then
    echo "ERROR: ${src}/kustomization.yaml に sha256 の固定の無い URL が残る（remote-resources.sha256 に足す。IADR-0679）" >&2
    return 1
  fi
}
