#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0703 決定 4: クラウドのノードの防御の確かめ（オーナーがノードの上で root で流す。読むだけ・何も変えない）。
#
#   sudo bash check-node.sh                    # 2 枚目の防御が「落とす」段であることまで求める
#   sudo bash check-node.sh --expect report    # 段の途中（「報告」の段で数えている間）
#
# 確かめること（手順書 docs/operations/cloud-node-hardening-runbook.md）:
#   1. 暗号化した領域（LUKS）: /var/lib/rancher・/etc/rancher（管理者の kubeconfig）・/var/log/pods（Pod のログ）が、どれもマウント点で、
#      元が dm-crypt の装置である。k3s の drop-in が 3 つのマウントを起動の条件にしている。
#   2. 2 枚目の防御（nftables の表 inet node_guard）が在り、段が --expect と同じ。/etc/nftables.conf が `flush ruleset` を持たず、
#      node-guard を読み込み、nftables.service が有効（再起動の後も戻る）。
#   3. SSH: root のログイン・パスワード・キーボード対話を受けない（`sshd -T` の実効値）。
#   4. 参考: ループバックの外で受けている口（2 枚目の防御が落とす。22 のほかに在れば名指す）。
# 終了コード: 0＝すべて合う / 1＝合わない項目がある（NG の行） / 2＝引数の誤り
set -uo pipefail

EXPECT=enforce
while [ "$#" -gt 0 ]; do
  case "$1" in
    --expect) EXPECT="${2:-}"; shift 2 ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --expect enforce|report）" >&2; exit 2 ;;
  esac
done
case "$EXPECT" in enforce | report) ;; *) echo "ERROR: --expect は enforce か report" >&2; exit 2 ;; esac
R="${NODE_ROOT:-}"   # 試験のための根（既定はノードの /）

bad=0
ok() { echo "ok  $1"; }
ng() { echo "NG  $1"; bad=1; }

# --- 1. 暗号化した領域 ---
ENCRYPTED_PATHS=(/var/lib/rancher /etc/rancher /var/log/pods)
for p in "${ENCRYPTED_PATHS[@]}"; do
  if ! mountpoint -q "$R$p"; then ng "$p がマウント点でない（暗号化した領域へ寄せていない）"; continue; fi
  src="$(findmnt -n -o SOURCE --target "$R$p" 2>/dev/null)"
  dev="${src%%\[*}"
  if [ -n "$dev" ] && [ "$(lsblk -ndo TYPE "$dev" 2>/dev/null)" = crypt ]; then ok "$p は暗号化した領域（$dev）"; else ng "$p の元（${src:-不明}）が dm-crypt でない"; fi
done
dropin="$(cat "$R"/etc/systemd/system/k3s.service.d/*.conf 2>/dev/null)"
for p in "${ENCRYPTED_PATHS[@]}"; do
  grep -qx "ConditionPathIsMountPoint=$p" <<< "$dropin" || ng "k3s の drop-in が $p のマウントを起動の条件にしていない"
done
for p in "${ENCRYPTED_PATHS[@]}"; do
  grep -qE "^RequiresMountsFor=(.* )?${p}( .*)?$" <<< "$dropin" || ng "k3s の drop-in の RequiresMountsFor が $p を持たない"
done
kc="$R/etc/rancher/k3s/k3s.yaml"
if [ -e "$kc" ]; then
  [ "$(stat -c %a "$kc")" = 600 ] && ok "管理者の kubeconfig は 0600" || ng "管理者の kubeconfig が 0600 でない"
fi

# --- 2. 2 枚目の防御 ---
if ! table="$(nft list table inet node_guard 2>/dev/null)"; then
  ng "nftables の表 inet node_guard が無い（2 枚目の防御を当てていない）"
else
  tail_="$(nft list chain inet node_guard guard_tail 2>/dev/null)"
  if grep -qE '^[[:space:]]*(limit .* )?counter( packets [0-9]+ bytes [0-9]+)? drop$' <<< "$tail_"; then mode=enforce; else mode=report; fi
  [ "$mode" = "$EXPECT" ] && ok "2 枚目の防御の段: $mode" || ng "2 枚目の防御の段が $mode（求める段は $EXPECT）"
  grep -q 'tcp dport 22 accept' <<< "$table" || ng "2 枚目の防御が SSH（22）を受けていない"
fi
conf="$R/etc/nftables.conf"
if [ -r "$conf" ]; then
  grep -qE '^[[:space:]]*flush[[:space:]]+ruleset' "$conf" && ng "/etc/nftables.conf が flush ruleset を持つ（nftables の再起動で k3s の表が消える）"
  grep -qE '^[[:space:]]*include[[:space:]]+"[^"]*node-guard\.nft"' "$conf" || ng "/etc/nftables.conf が node-guard.nft を読み込まない（再起動で 2 枚目が消える）"
  if [ "$EXPECT" = enforce ]; then
    grep -qE '^[[:space:]]*include[[:space:]]+"[^"]*node-guard-enforce\.nft"' "$conf" || ng "/etc/nftables.conf が node-guard-enforce.nft を読み込まない（再起動で「報告」へ戻る）"
  fi
else
  ng "/etc/nftables.conf を読めない"
fi
[ "$(systemctl is-enabled nftables 2>/dev/null)" = enabled ] && ok "nftables.service は有効" || ng "nftables.service が有効でない（再起動で 2 枚目が消える）"

# --- 3. SSH ---
eff="$(sshd -T 2>/dev/null)"
if [ -z "$eff" ]; then
  ng "sshd -T を読めない（root で流す）"
else
  for want in "permitrootlogin no" "passwordauthentication no" "kbdinteractiveauthentication no"; do
    grep -qx "$want" <<< "$eff" && ok "sshd: $want" || ng "sshd の実効値が「$want」でない"
  done
fi

# --- 4. 参考: ループバックの外で受けている口 ---
others="$(ss -H -ltnu 2>/dev/null | awk '{print $1, $5}' | grep -vE ' (127\.[0-9.]+|\[::1\]|::1):[0-9]+$' | grep -vE ':22$' | sort -u)"
if [ -n "$others" ]; then
  echo "参考: ループバックの外で受けている口（2 枚目の防御が落とす。提供元のファイアウォールでも閉じていること）:"
  sed 's/^/      /' <<< "$others"
fi

[ "$bad" = 0 ] && { echo "OK: ノードの防御は手順書どおり"; exit 0; }
echo "NG の項目を手順書で直してから、もう一度流す" >&2
exit 1
