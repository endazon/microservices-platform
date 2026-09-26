# shellcheck shell=bash
# NFR, #1550: 稼働クラスタへ当たる scripts の「明示の指定」を 1 か所で判定する（シェル側）。
# Node 側の対は lib/live-opt-in.js（同じ規則・同じ文言・同じ終了コード 3）。規則の説明はそちらの冒頭。
#
# 使い方（呼び出し側の冒頭。**副作用より前**に置く）:
#   . "$(dirname "$0")/lib/live-opt-in.sh"
#   live_opt_in_scan "$@"; set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"   # --live を取り除き、LIVE=1 を立てる
#   live_opt_in_require "k8s-local-up.sh"                                # 指定が無ければここで exit 3
#
# 🔴 指定があれば LIVE=1 を export する。起動の中から呼ぶ子（istio-edge-up.sh・seed-*.js など）は
#    親の指定を引き継ぐ —— 親が明示の指定で走っている以上、子に同じ指定を二度求めない。

LIVE_OPT_IN_EXIT=3

# 引数から --live を取り除いて LIVE_REST へ入れる。--live があれば LIVE=1 を立てる。
live_opt_in_scan() {
  LIVE_REST=()
  local a
  for a in "$@"; do
    if [ "$a" = "--live" ]; then
      LIVE=1
    else
      LIVE_REST+=("$a")
    fi
  done
}

# live_opt_in_require <名前> [稼働クラスタに触れないモードの説明]
live_opt_in_require() {
  if [ "${LIVE:-}" = "1" ]; then
    export LIVE=1
    return 0
  fi
  {
    printf '[%s] 稼働クラスタへ当たるため、明示の指定が無いので何もせずに終わります（#1550）。\n' "$1"
    printf '  実行するなら --live を付けるか、環境変数 LIVE=1 を与えてください。\n'
    if [ -n "${2:-}" ]; then printf '  稼働クラスタに触れないモード: %s\n' "$2"; fi
  } >&2
  exit "$LIVE_OPT_IN_EXIT"
}
