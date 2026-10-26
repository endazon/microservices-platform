# shellcheck shell=bash
# NFR, IADR-0642 (#2273): k3d クラスタの作成を、短い上限・診断・1 回の作り直しで包む。
#
# 背景: CI の使い捨てスタックの作成が、ノードの起動（`Starting node 'k3d-…-agent-0'`）や flannel の
#   `/run/flannel/subnet.env` の欠落で止まり、ステップの上限 30 分を無言で使い切った（同型 2 回。#2257・#2261 の CI）。
#
# 1 試行 = `k3d cluster create`（上限 K3D_CREATE_TIMEOUT 秒）→ 全ノードの Ready 待ち（上限 K3D_NODE_READY_TIMEOUT 秒）。
#   失敗したら診断を出し、**この実行が作ったクラスタだけを**消して 1 回だけ作り直す。2 回目も失敗したら止まる。
# 🔴 呼んでよいのは「クラスタが無かった」ときだけ（reuse の経路から呼ばない。既存のクラスタ＝PoC を消さないため）。
# 🔴 作り直しは試験の skip ではない。作り直した後のスタックで、全ての門が同じように走る。回数は出力と注記に残す。

# 上限の値を検める（クラスタに触る前に落とす）。正の整数（秒）だけを受け付ける。
#   K3D_DIAG_TIMEOUT は診断の 1 コマンドごとの上限（既定 20 秒）。
k3d_create_validate_timeouts() {
  local name value
  for name in K3D_CREATE_TIMEOUT K3D_NODE_READY_TIMEOUT K3D_DIAG_TIMEOUT; do
    value="${!name:-}"
    [ -z "$value" ] && continue
    case "$value" in
      *[!0-9]* | 0*) echo "ERROR: ${name} は正の整数（秒）で与えます: '${value}'" >&2; return 1 ;;
    esac
  done
}

# 診断の 1 コマンドを上限つきで走らせる（K3D_DIAG_TIMEOUT 秒・既定 20）。
# 🔴 docker のデーモンが固まっていると、上限の無い診断がそこで止まり、元の 30 分の無言が戻る。1 コマンドずつ上限を掛け、
#   診断全体（最大 7 コマンド）を約 2 分に収める。timeout が無い環境は上限なしで走らせる（作成の側と同じく warn は出してある）。
k3d_diag_run() {
  if command -v timeout >/dev/null 2>&1; then
    timeout -k 5 "${K3D_DIAG_TIMEOUT:-20}" "$@"
  else
    "$@"
  fi
}

# 失敗した試行の診断。どれが失敗しても・上限を超えても続ける（診断で止まらない）。
k3d_create_diagnose() {
  local cluster="$1" node rc
  echo "---- k3d diagnostics (cluster ${cluster}; 1 コマンドの上限 ${K3D_DIAG_TIMEOUT:-20}s) ----" >&2
  command -v timeout >/dev/null 2>&1 || echo "WARN: timeout（coreutils）が無いので、診断を上限なしで走らせる" >&2
  k3d_diag_run docker ps -a >&2 || echo "    (docker ps -a が失敗した・上限を超えた: exit $?)" >&2
  for node in "k3d-${cluster}-server-0" "k3d-${cluster}-agent-0"; do
    echo "---- docker logs --tail 200 ${node} ----" >&2
    k3d_diag_run docker logs --tail 200 "$node" >&2 2>&1 || echo "    (docker logs が失敗した・上限を超えた: exit $?)" >&2
    # 2 回目の事象（#2261 の CI）: flannel が subnet.env を書かないとノードは Ready にならない。名指しで出す。
    rc=0; k3d_diag_run docker exec "$node" test -f /run/flannel/subnet.env >/dev/null 2>&1 || rc=$?
    if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then
      echo "    ${node}: /run/flannel/subnet.env の有無を確かめられなかった（docker exec が上限を超えた）" >&2
    elif [ "$rc" -ne 0 ]; then
      echo "    ${node}: /run/flannel/subnet.env が無い（flannel がノードのサブネットを書いていない）" >&2
    fi
  done
  echo "---- docker info ----" >&2
  k3d_diag_run docker info >&2 || echo "    (docker info が失敗した・上限を超えた: exit $?)" >&2
  k3d_diag_run kubectl get nodes -o wide --request-timeout=15s >&2 2>&1 || true
  echo "---- end of k3d diagnostics ----" >&2
}

# 作成の引数から期待するノード数（server 1 ＋ --agents N）を数える。
k3d_expected_nodes() {
  local agents=0 prev=""
  for a in "$@"; do
    [ "$prev" = "--agents" ] && agents="$a"
    case "$a" in --agents=*) agents="${a#--agents=}" ;; esac
    prev="$a"
  done
  case "$agents" in ''|*[!0-9]*) agents=0 ;; esac
  echo $((1 + agents))
}

# 1 試行。成功で 0、失敗で 1。失敗の理由を 1 行出す。
k3d_create_attempt() {
  local cluster="$1"; shift
  local create_timeout="${K3D_CREATE_TIMEOUT:-300}" ready_timeout="${K3D_NODE_READY_TIMEOUT:-180}" rc=0
  if command -v timeout >/dev/null 2>&1; then
    timeout -k 30 "$create_timeout" k3d cluster create "$cluster" "$@" || rc=$?
  else
    echo "WARN: timeout（coreutils）が無いので、k3d cluster create を上限なしで呼ぶ" >&2
    k3d cluster create "$cluster" "$@" || rc=$?
  fi
  if [ "$rc" -ne 0 ]; then
    if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then
      echo "ERROR: k3d cluster create が上限 ${create_timeout} 秒を超えた（exit ${rc}）" >&2
    else
      echo "ERROR: k3d cluster create が失敗した（exit ${rc}）" >&2
    fi
    return 1
  fi
  # ノードが登録される前の `kubectl wait … node --all` は "no matching resources found" で即座に失敗する
  #   （要らない作り直しになる）。同じ上限の中で、期待する数のノードが見えるまで待ってから Ready を待つ。
  local expected seen=0 deadline remaining
  expected="$(k3d_expected_nodes "$@")"
  deadline=$((SECONDS + ready_timeout))
  while :; do
    seen="$(kubectl get nodes -o name --request-timeout=5s 2>/dev/null | grep -c '^node/' || true)"
    [ "${seen:-0}" -ge "$expected" ] && break
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "ERROR: ノードが上限 ${ready_timeout} 秒のうちに揃わなかった（期待 ${expected}・見えた ${seen:-0}）" >&2
      return 1
    fi
    sleep 1
  done
  remaining=$((deadline - SECONDS)); [ "$remaining" -lt 1 ] && remaining=1
  if ! kubectl wait --for=condition=Ready node --all --timeout="${remaining}s"; then
    echo "ERROR: ノードが上限 ${ready_timeout} 秒のうちに Ready にならなかった" >&2
    return 1
  fi
  return 0
}

# 作り直しを見えるようにする（出力・GitHub Actions の注記・ジョブの要約）。
k3d_create_report_recreate() {
  local cluster="$1"
  local msg="k3d cluster '${cluster}' was recreated once after a failed create (#2273 / IADR-0642). This is not a skipped test; count it as a flake."
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "::warning title=k3d cluster recreated::${msg}"
  fi
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf -- '- ⚠️ %s\n' "$msg" >> "$GITHUB_STEP_SUMMARY" || true
  fi
}

# 本体。K3D_RECREATE_COUNT（作り直しの回数）を export する。
k3d_create_with_retry() {
  local cluster="$1"; shift
  K3D_RECREATE_COUNT=0
  export K3D_RECREATE_COUNT
  echo "    k3d cluster create: 上限 ${K3D_CREATE_TIMEOUT:-300}s・ノードの Ready 待ち ${K3D_NODE_READY_TIMEOUT:-180}s・作り直しは 1 回まで"
  if k3d_create_attempt "$cluster" "$@"; then
    echo "    k3d recreate count: ${K3D_RECREATE_COUNT}"
    return 0
  fi
  k3d_create_diagnose "$cluster"
  echo "==> k3d: この実行が作ったクラスタ '${cluster}' を消して 1 回だけ作り直す" >&2
  k3d cluster delete "$cluster" || true
  K3D_RECREATE_COUNT=1
  k3d_create_report_recreate "$cluster"
  if k3d_create_attempt "$cluster" "$@"; then
    echo "    k3d recreate count: ${K3D_RECREATE_COUNT}"
    return 0
  fi
  k3d_create_diagnose "$cluster"
  echo "ERROR: k3d クラスタの作成が作り直しの後も失敗した（作り直しは 1 回まで。k3d recreate count: ${K3D_RECREATE_COUNT}）" >&2
  return 1
}
