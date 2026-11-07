#!/usr/bin/env bash
# NFR-18, IADR-0704 決定 4: ノードの 2 枚目の防御（node-guard.nft・node-guard-enforce.nft）と、ノードの確かめ（check-node.sh）を固定する。
#
#   sudo bash deploy/cloud/node/check-node.test.sh
#
# - nftables の規則は**実物の nft** で、使い捨てのネットワークの名前空間（unshare -n）へ当てて読む（手元のネットワークには触れない）。
# - check-node.sh は、その名前空間の中で、マウント・装置・sshd・systemd・ss をスタブにし、ファイルは作業場の根（NODE_ROOT）から読む。
# root・unshare・nft が要る（無ければ失敗にする。CI は sudo で流す）。ノードには触れない。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
[ "$(id -u)" = 0 ] || { echo "✗ root で流す（nft を使い捨てのネットワークの名前空間へ当てる）" >&2; exit 1; }
for t in nft unshare; do command -v "$t" >/dev/null || { echo "✗ $t が無い" >&2; exit 1; }; done
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT
pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; [ -f "$W/out" ] && sed 's/^/      /' "$W/out" >&2; exit 1; }

# --- 規則そのもの ---
nft -c -f "$HERE/node-guard.nft" || ng "node-guard.nft を nft が読めない"
grep -qE '^[[:space:]]*flush[[:space:]]+ruleset' "$HERE/node-guard.nft" "$HERE/node-guard-enforce.nft" && ng "規則が flush ruleset を持つ（k3s の表を消す）"
listed="$(unshare -n bash -c "nft -f '$HERE/node-guard.nft' && nft -f '$HERE/node-guard.nft' && nft list ruleset")" || ng "名前空間へ当てられない（2 回当てても作り直せること）"
[ "$(grep -c '^table ' <<< "$listed")" = 1 ] || ng "当てた後の表が inet node_guard の 1 つでない"
grep -q 'type filter hook input priority filter; policy accept;' <<< "$listed" || ng "入りの鎖が policy accept でない（落とすかは guard_tail だけが決める）"
grep -q 'hook forward\|hook output' <<< "$listed" && ng "転送・出の鎖を持つ（入りだけを絞る）"
for want in 'iif "lo" accept' 'ct state established,related accept' 'tcp dport 22 accept' 'iifname { "cni0", "flannel.1" } accept' 'jump guard_tail'; do
  grep -qF "$want" <<< "$listed" || ng "規則に「$want」が無い"
done
grep -qE 'tcp dport (6443|10250)' <<< "$listed" && ng "API・kubelet の口を受けている"
grep -qE 'counter packets [0-9]+ bytes [0-9]+$' <<< "$listed" && ! grep -q ' drop$' <<< "$listed" || ng "node-guard.nft だけでは「報告」（数えるだけ・落とさない）でない"
listed="$(unshare -n bash -c "nft -f '$HERE/node-guard.nft' && nft -f '$HERE/node-guard-enforce.nft' && nft list chain inet node_guard guard_tail")"
grep -qE 'counter packets [0-9]+ bytes [0-9]+ drop$' <<< "$listed" || ng "node-guard-enforce.nft で「落とす」にならない"
ok "規則: 自分の表だけを作り直し（flush ruleset なし）、入りだけを絞る。受けるのはループバック・戻り・ICMP・22・DHCP・Pod の口。既定は報告、enforce で落とす"

# --- check-node.sh（スタブ） ---
mkdir -p "$W/bin" "$W/root/etc/systemd/system/k3s.service.d" "$W/root/etc/rancher/k3s"
cat > "$W/bin/mountpoint" <<'EOF'
#!/usr/bin/env bash
p="${2#"$NODE_ROOT"}"; grep -qxF "$p" "$STUB/mounts"
EOF
cat > "$W/bin/findmnt" <<'EOF'
#!/usr/bin/env bash
p="${5#"$NODE_ROOT"}"; awk -v p="$p" '$1 == p { print $2 }' "$STUB/sources"
EOF
cat > "$W/bin/lsblk" <<'EOF'
#!/usr/bin/env bash
awk -v d="$3" '$1 == d { print $2 }' "$STUB/types"
EOF
cat > "$W/bin/sshd" <<'EOF'
#!/usr/bin/env bash
cat "$STUB/sshd"
EOF
cat > "$W/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
[ "$*" = "is-enabled nftables" ] && cat "$STUB/nftables-enabled"
EOF
cat > "$W/bin/ss" <<'EOF'
#!/usr/bin/env bash
cat "$STUB/ss"
EOF
chmod +x "$W/bin/"*
export STUB="$W/stub" NODE_ROOT="$W/root"
mkdir -p "$STUB"
reset_node() {
  printf '%s\n' /var/lib/rancher /etc/rancher /var/log/pods > "$STUB/mounts"
  printf '%s\n' "/var/lib/rancher /dev/mapper/rancher" "/etc/rancher /dev/mapper/rancher[/etc-rancher]" "/var/log/pods /dev/mapper/rancher[/pod-logs]" > "$STUB/sources"
  echo "/dev/mapper/rancher crypt" > "$STUB/types"
  printf '%s\n' "port 22" "permitrootlogin no" "passwordauthentication no" "kbdinteractiveauthentication no" > "$STUB/sshd"
  echo enabled > "$STUB/nftables-enabled"
  printf '%s\n' "tcp LISTEN 0 4096 0.0.0.0:22 0.0.0.0:*" "tcp LISTEN 0 4096 127.0.0.1:6443 0.0.0.0:*" "tcp LISTEN 0 4096 *:10250 *:*" > "$STUB/ss"
  cat > "$NODE_ROOT/etc/systemd/system/k3s.service.d/10-require-encrypted-data.conf" <<'EOF'
[Unit]
ConditionPathIsMountPoint=/var/lib/rancher
ConditionPathIsMountPoint=/etc/rancher
ConditionPathIsMountPoint=/var/log/pods
RequiresMountsFor=/var/lib/rancher
RequiresMountsFor=/etc/rancher
RequiresMountsFor=/var/log/pods
EOF
  printf '%s\n' '#!/usr/sbin/nft -f' 'include "/etc/nftables.d/node-guard.nft"' 'include "/etc/nftables.d/node-guard-enforce.nft"' > "$NODE_ROOT/etc/nftables.conf"
  : > "$NODE_ROOT/etc/rancher/k3s/k3s.yaml"; chmod 600 "$NODE_ROOT/etc/rancher/k3s/k3s.yaml"
}
# check <段の規則のファイル…> -- <check-node.sh の引数…>: 名前空間の中で規則を当て、スタブの PATH で check-node.sh を流す。
check() {
  local loads=() a
  while [ "$#" -gt 0 ] && [ "$1" != -- ]; do loads+=("$1"); shift; done
  [ "${1:-}" = -- ] && shift
  local script="set -e;"
  for a in "${loads[@]+"${loads[@]}"}"; do script+=" nft -f '$HERE/$a';"; done
  script+=" PATH='$W/bin':\"\$PATH\" bash '$HERE/check-node.sh' \"\$@\""
  set +e; unshare -n bash -c "$script" _ "$@" > "$W/out" 2>&1; rc=$?; set -e
}
expect_ng() {
  [ "$rc" -eq 1 ] && grep -q "^NG  .*$1" "$W/out" || ng "check-node.sh が「$2」を検出しない（rc=$rc）"
  reset_node
}

reset_node
check node-guard.nft node-guard-enforce.nft
[ "$rc" -eq 0 ] || ng "手順書どおりのノードで check-node.sh が通らない（rc=$rc）"
grep -q '10250' "$W/out" || ng "ループバックの外で受けている口（10250）を参考に名指さない"
grep -q '6443' "$W/out" && ng "ループバックの 6443 を外の口として名指した"
ok "check-node.sh: 手順書どおりのノード（暗号化した 3 つの領域・落とす段・SSH）で通り、外で受ける口を参考に名指す"

check node-guard.nft;                                   expect_ng '段が report' "報告の段のまま（落とす段を求める）"
check node-guard.nft -- --expect report
[ "$rc" -eq 0 ] || ng "--expect report で報告の段が通らない"; reset_node
check;                                                  expect_ng 'inet node_guard が無い' "2 枚目の防御が無い"
sed -i '1a flush ruleset' "$NODE_ROOT/etc/nftables.conf"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'flush ruleset' "nftables.conf の flush ruleset"
sed -i '/node-guard-enforce/d' "$NODE_ROOT/etc/nftables.conf"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'node-guard-enforce.nft を読み込まない' "再起動で報告へ戻る nftables.conf"
echo disabled > "$STUB/nftables-enabled"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'nftables.service が有効でない' "nftables.service の無効"
sed -i 's/^permitrootlogin no$/permitrootlogin without-password/' "$STUB/sshd"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'permitrootlogin no' "root のログインを受ける sshd"
sed -i 's/^passwordauthentication no$/passwordauthentication yes/' "$STUB/sshd"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'passwordauthentication no' "パスワードを受ける sshd"
sed -i '/^\/etc\/rancher$/d' "$STUB/mounts"
check node-guard.nft node-guard-enforce.nft;            expect_ng '/etc/rancher がマウント点でない' "暗号化していない /etc/rancher"
echo "/var/log/pods /dev/sda1" >> "$STUB/sources"; sed -i '/^\/var\/log\/pods \/dev\/mapper/d' "$STUB/sources"; echo "/dev/sda1 part" >> "$STUB/types"
check node-guard.nft node-guard-enforce.nft;            expect_ng '/var/log/pods の元' "ルートのディスクの上の Pod のログ"
sed -i '/ConditionPathIsMountPoint=\/var\/log\/pods/d' "$NODE_ROOT/etc/systemd/system/k3s.service.d/10-require-encrypted-data.conf"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'drop-in が /var/log/pods' "drop-in の条件の欠け"
chmod 644 "$NODE_ROOT/etc/rancher/k3s/k3s.yaml"
check node-guard.nft node-guard-enforce.nft;            expect_ng 'kubeconfig が 0600 でない' "0600 でない kubeconfig"
ok "check-node.sh: 報告の段・表の欠け・flush ruleset・読み込みの欠け・サービスの無効・sshd の 2 つ・暗号化の外の 2 つ・drop-in の欠け・kubeconfig の権限を名指して落ちる"

echo "check-node.test.sh: ${pass} passed"
