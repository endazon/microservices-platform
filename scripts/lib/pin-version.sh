#!/usr/bin/env bash
# NFR, 計画 ADR-0135 決定 2 / ADR-0107 決定 3, IADR-0547 (#1858):
# **`scripts/` が入れる製品の版を、内側のイメージの digest の固定と対で 1 つに決める口。**
# `source` して使う。呼び出し元は `scripts/k8s-local-up.sh` と `scripts/istio-edge-up.sh`。
#
# 版（chart の --version・上流マニフェストの URL）と、内側のイメージの `tag@digest`（`deploy/` の values・kustomization）は
# 対で決まる。版だけを env で変えると、新しい chart / マニフェストに古い版のイメージが入る（あるいは固定の外れた参照が入る）。
# そこで上書きの env は**固定と同じ値のときだけ受け、違えば何も変える前に止める**（黙って既定へ戻さない —— 与えた人は
# その版が入ると思っている）。版を変えるときは、置き場のファイルの tag・digest と呼び出し元の固定の版を対で改める
# （版と tag の一致は scripts/scripts.repo.test.js、digest の形は scripts/check-image-digests.js が見る）。

# pin_version <変数名> <固定の版> <digest の置き場>
#   変数が空か固定と同じなら、変数へ固定の版を入れる。違えば stderr に案内を出して exit 1。
pin_version() {
  local name="$1" pinned="$2" where="$3"
  local given="${!name:-}"
  if [ -n "$given" ] && [ "$given" != "$pinned" ]; then
    echo "ERROR: ${name}=${given} は固定の版 ${pinned} と違う。版は ${where} の内側のイメージの digest と対で決まるため、" >&2
    echo "       env だけでは変えられない（IADR-0547）。版を変えるときは ${where} の tag・digest と起動器の固定の版を対で改める" >&2
    echo "       （digest の解決は docs/operations/operations.md §インフラ製品の点検）。" >&2
    exit 1
  fi
  printf -v "$name" '%s' "$pinned"
}
