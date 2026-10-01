---
title: ISTIO_MTLS_MODE を付けない k8s-local-up.sh の再実行で、メッシュの mTLS を STRICT から黙って PERMISSIVE へ戻さない（#1710）
type: spec
status: done
related_ids: [NFR-16, ADR-0005, ADR-0021, IADR-0487, IADR-0377, IADR-0317, IADR-0307]
author: claude
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
issue: "#1710"
---

# 仕様書: 再実行で mTLS を黙って緩めない（#1710）

> 本仕様書は実装着手前に作成する。起点は #1710（監査からの owner 起票・medium）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-16**（通信暗号化。恒久: サービス間 mTLS）。STRICT で動いているメッシュが起動器の再実行で平文許容へ戻ることは、
  NFR-16 の統制が黙って外れることである。メタ作業ではないので採番する。
- 計画 ADR: ADR-0005（メッシュ / mTLS）、ADR-0021（入口＝Istio Ingress Gateway）
- 関連 IADR: IADR-0307 決定 4（段取り「注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT」）、IADR-0377（mode は helm だけが書く・決定 2 の降格）、
  IADR-0317（#1691 の 2026-09-28 追記: 移行済みの再実行）。本件の判断は **IADR-0487** に置く。

## 何が起きるか（issue の要約）

- #1694 で足した拒否のエラー文は「Istio の入口のまま再実行する: `ISTIO=1 LOCALEDGE=1 bash scripts/k8s-local-up.sh --live`」と勧める（`ISTIO_MTLS_MODE` 無し）。
- `k8s-local-up.sh` の [6/7] は `ISTIO_MTLS_MODE_AT_INSTALL="${ISTIO_MTLS_MODE:-PERMISSIVE}"` を `--reuse-values` 無しの `--set mesh.mtlsMode=…` で渡す。
- `istio-edge-up.sh` は `ISTIO_MTLS_MODE` 未指定ならモードを変えない。
- 結果: STRICT で動く移行済みクラスタに勧めどおりの再実行をすると、**PERMISSIVE へ黙って降格する**。
  [6/7] の直前の注記「再実行のたびに緩めない」（#1691）は `ISTIO_MTLS_MODE=STRICT` を渡したときにしか成り立たない。

## 直し方（決定。詳細と却下案は IADR-0487）

1. **選び方は 明示 ＞ 現行 ＞ PERMISSIVE。** `ISTIO=1` かつ `ISTIO_MTLS_MODE` が空のとき、`[2/7]` の前（副作用より前）で現行の値を読む。
   - 明示（env）があれば読まない。そのまま使う（緩めるのも `ISTIO_MTLS_MODE=PERMISSIVE` の明示で行う）。
   - リリース `msp` が在り、メッシュを宣言している（`mesh.enabled: true`）なら、その `mesh.mtlsMode` を `ISTIO_MTLS_MODE` へ引き継ぎ、
     `INFO: mesh.mtlsMode は現行の <mode> を引き継ぎます（ISTIO_MTLS_MODE 未指定）` を出す。以降は明示と同じに扱う
     （未移行の `LOCALEDGE=1` なら [6/7] は PERMISSIVE で入り、`istio-edge-up.sh` [5/5] が入口を移した後で STRICT へ戻す）。
   - リリースが無い・メッシュ未宣言（`mesh.enabled` が true でない）なら初回とみなし、従来どおり PERMISSIVE。
2. **読む先は `helm get values msp -o yaml`（利用者が与えた値）。** `--all` は使わない（チャート既定 `mtlsMode: STRICT` を混ぜ、メッシュ未宣言のリリースを STRICT と読む）。
   稼働の PeerAuthentication も読まない（書く口は helm ただ 1 つ＝ helm の宣言が正。乖離は G12 が別に落とす）。
   リリースの有無は `helm list -a -q --filter '^msp$'`（無ければ空で 0）で確かめ、「無い」と「読めない」を分ける。
3. **読めないときは止める（fail-closed）。** `helm list` / `helm get values` の失敗・`mesh.enabled: true` なのに `mtlsMode` が無い・値域外は、
   `[2/7]` の前に非 0 で止め、`ISTIO_MTLS_MODE=STRICT`（または PERMISSIVE）の明示を告げる。
   「STRICT を保つ」側へ倒さない理由: PERMISSIVE で動いているクラスタ（入口が Traefik の構成を含む）を推測で STRICT にすると、入口が 502 になり得る（#1072）。
   降格・昇格のどちらも推測であり、止めるのが唯一の「書き換えない」側である。
4. **#1694 のエラー文**を `ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT bash scripts/k8s-local-up.sh --live` に改め、PERMISSIVE の場合と省いたときの挙動を添える。
5. **誤解を招く注記**（[6/7] 直前「再実行のたびに緩めない」・`[opt-in] Istio` 節冒頭の「既定は PERMISSIVE で入る」）を、初回の既定と再実行の引き継ぎに書き分ける。

## 窓の表（規則 11）

窓は「再実行の前のクラスタの宣言（前の端）」と「再実行の指定（後の端）」の間にある。増える側＝昇格（PERMISSIVE → STRICT）、減る側＝降格（STRICT → PERMISSIVE）の
両方のプローブを置き、3 つの形で比べた（✓＝期待どおり）。C の列は `k8s-local-up.test.js` の #1710 節が全升目を実測で固定する。

| プローブ（前の宣言 × 指定 × 読み取り） | 期待 | A: 後の端だけ（従前 `${ISTIO_MTLS_MODE:-PERMISSIVE}`） | B: 前の端だけ（現行を優先・指定を無視） | **C: 両端（明示 ＞ 現行 ＞ PERMISSIVE・読めなければ止める）** |
| --- | --- | --- | --- | --- |
| P1 STRICT × 未指定 × 読める（減る側・黙った降格） | STRICT | ✗ PERMISSIVE | ✓ | ✓ |
| P2 PERMISSIVE × 未指定 × 読める | PERMISSIVE | ✓ | ✓ | ✓ |
| P3 無い（初回）× 未指定 | PERMISSIVE | ✓ | ✓ | ✓ |
| P4 PERMISSIVE × STRICT 指定（増える側・昇格） | STRICT | ✓ | ✗ PERMISSIVE | ✓ |
| P5 STRICT × PERMISSIVE 指定（減る側・明示の降格） | PERMISSIVE | ✓ | ✗ STRICT | ✓ |
| P6 STRICT × 未指定 × 読めない | 止まる | ✗ PERMISSIVE で進む | ✗（読めないと値が無い） | ✓ |
| P7 何でも × STRICT 指定 × 読めない | STRICT で進む | ✓ | ✗ | ✓ |
| P8 STRICT × 未指定 × 未移行 LOCALEDGE | [6/7] PERMISSIVE → [5/5] STRICT | ✗ PERMISSIVE のまま | ✗ [6/7] で STRICT（502） | ✓ |

片側だけの形は必ず逆側が空く（A は減る側の P1・P6・P8、B は増える側の P4 と明示の降格 P5）。C だけが全升目を満たす。
A の P1 は下の変異 M1（形 A そのもの）で赤になることを実測した。A の他の升目と B の列は形の定義からの導出である（B を実装した変異は置いていない）。

## 母集合（規則 9: `ISTIO_MTLS_MODE` と `mtlsMode` の全文走査）

走査: `git grep -n -l "ISTIO_MTLS_MODE\|mtlsMode" -- ':!src/ai-stock-trading'`（2026-10-01・`origin/develop` = `78bc7b21`）＝ 29 ファイル。

| 箇所 | 再実行の挙動を述べるか | 扱い |
| --- | --- | --- |
| `scripts/k8s-local-up.sh`（[6/7] の宣言・注記・#1694 のエラー文） | 動く本体 | **是正**（判定の追加・注記・エラー文） |
| `scripts/lib/mesh-mtls-mode.sh` | 書く口 | **追加**（読む口 `current_mesh_mtls_mode` / 純関数 `mesh_values_mtls_mode`） |
| `scripts/istio-edge-up.sh` | 未指定なら変えない | **対象外**: 起動器から呼ばれるときは引き継いだ値が渡る。単独実行の「未指定なら変えない」は降格しない（正しい） |
| `scripts/k8s-local-up.test.js` | 試験 | **追加**（helm スタブの模型と #1710 節） |
| `scripts/README.md` の `lib/mesh-mtls-mode.sh` 行 | 書く口だけを述べる | **是正**（読む口を足す） |
| `deploy/istio/README.md` §mTLS モードは既定 PERMISSIVE で入る・移行済みの再実行 | 「既定 PERMISSIVE」「[6/7] から要求どおり」 | **是正**（規則 10: 未指定の再実行は引き継ぐ。初回だけが PERMISSIVE） |
| `deploy/local/edge-istio/README.md` 使い方 | `# PERMISSIVE` の注記 | **是正**（初回の値であることと、再実行は引き継ぐことを添える） |
| `deploy/helm/.../values.yaml`・`templates/istio-mtls.yaml` | 本番像の既定 STRICT | **対象外**: 再実行の挙動ではない |
| `scripts/check-stack-ready.js`（G12） | 宣言と稼働の乖離 | **対象外**: 読むだけ |
| `scripts/helm-private-notes-sync-authz.test.js`・`scripts/reset-floor.test.js`・`MeshMtlsTests.cs` | 試験 | **対象外**: 起動器の再実行を扱わない（`reset-floor` は `istio-edge-up.sh` 単独） |
| `.ai-context/adr/*`（0026/0307/0317/0377/0403/0407）・`.ai-context/specs/*` | 凍結記録 | **対象外**（本文は書き換えない）。ただし IADR-0377 決定 3「未設定なら PERMISSIVE」と IADR-0317 #1691 追記は本件で意味が変わるため、日付つき追記で IADR-0487 を指す |
| `docs/operations/object-storage-seaweedfs-cutover-runbook.md` 手順 5 | 起動器を流さない手動 `helm upgrade` | **対象外**: 起動器の再実行ではない（同手順は既に `helm get values` で値を引き継ぐ） |

`docs/` 配下に起動器の再実行時の mTLS の挙動を述べる箇所は無かった（`STRICT` の走査でも、本番像の STRICT の方針を述べる記述だけ）。

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `scripts/lib/mesh-mtls-mode.sh` | `mesh_values_mtls_mode`（純関数）・`current_mesh_mtls_mode`（helm を読む）を追加 |
| `scripts/k8s-local-up.sh` | `[2/7]` の前の引き継ぎ判定・fail-closed・#1694 のエラー文・注記 2 箇所 |
| `scripts/k8s-local-up.test.js` | helm スタブ（既定はリリース無し＝従来と同じ）・runUp の env 除去に `ISTIO` / `ISTIO_MTLS_MODE`・#1710 節 9 件 |
| `scripts/README.md` / `deploy/istio/README.md` / `deploy/local/edge-istio/README.md` | 上表 |
| `.ai-context/adr/IADR-0487_...md`（新規）・`README.md`（索引） | 判断の記録 |
| `.ai-context/adr/IADR-0377_...md` / `IADR-0317_...md` | 日付つき追記（IADR-0487 を指す） |

## 受け入れ基準（→ 試験。`k8s-local-up.test.js` の #1710 節）

1. 移行済み ＋ 未指定 ＋ 現行 STRICT → [6/7] は STRICT、INFO を出し、`istio-edge-up.sh` [5/5] へも STRICT が渡る。
2. 移行済み ＋ 未指定 ＋ 現行 PERMISSIVE → PERMISSIVE のまま（昇格しない）。
3. 明示が勝つ（現行 STRICT ＋ 明示 PERMISSIVE → PERMISSIVE。現行 PERMISSIVE ＋ 明示 STRICT → STRICT）。明示なら読まず、読み取りが失敗しても進む。
4. 初回（リリース無し）＋ 未指定 → PERMISSIVE。`mesh.enabled: false` のリリースからも引き継がない。
5. 読めない（`helm list` 失敗・`helm get values` 失敗・`mtlsMode` 欠落・値域外）＋ 未指定 → `[2/7]` の前に非 0、`ISTIO_MTLS_MODE=STRICT` を告げる。
6. 未移行の `LOCALEDGE=1` ＋ 引き継いだ STRICT → [6/7] PERMISSIVE、[5/5] で STRICT。
7. #1694 のエラー文が `ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT bash scripts/k8s-local-up.sh --live` を含む。
8. `ISTIO` 未設定の既定経路は helm の読み取りを足さない（既定のバイト等価）。
9. 純関数の判定表（`mesh:` 直下だけを見る。入れ子・別キー・引用符・CRLF）。

## 変異（自己変異で赤になること。2026-10-01 実測）

`k8s-local-up.test.js` は最初の失敗で止まるので、各変異で最初に赤になった試験を記す。変異は 1 件ずつ当てて元へ戻した（`diff -q` で復元を確認）。

| 変異 | 結果（exit=1 と最初の失敗） |
| --- | --- |
| M1. 引き継ぎを外す（`0)` の `ISTIO_MTLS_MODE="$mtls_current"` を `:` に。＝従前の `${ISTIO_MTLS_MODE:-PERMISSIVE}` だけに戻る。窓の表の形 A） | 赤: 「#1710 の再発: 現行 STRICT のクラスタを未指定の再実行で PERMISSIVE へ降格した」 |
| M2. 読めないときに初回扱いへ倒す（`1)` を `1\|2)` に） | 赤: 「helm list が失敗: 読めないのに進んだ（PERMISSIVE へ倒すと STRICT を黙って緩める）」 |
| M4. #1694 のエラー文から `ISTIO_MTLS_MODE=STRICT` を外す | 赤: 「Istio のまま再実行する指定に ISTIO_MTLS_MODE=STRICT が無い」 |
| M5. 引き継ぎを移行済み（`EDGE_ON_ISTIO=1`）に限る | 赤: 「リリースの有無を確かめていない」（初回・未移行の経路。P8 も同じ理由で空く） |

`helm get values` への `--all` の混入は、スタブが `--all` を区別しないため起動器の経路では捕まらない。純関数の表と IADR-0487 の理由で守る（残余）。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/scripts.test.js` / `node scripts/reset-floor.test.js`
- 文書系: `check-trace-blocks` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links` /
  `check-reading-budget` / `check-doc-type-vocabulary` / `check-commit-messages --range origin/develop..HEAD`
- 稼働クラスタでは実行しない（スタブのみ）。`shellcheck` は本環境に無い（CI の lint に委ねる）。

［2026-10-01 追記 / #1710］独立監査（GO・🟡 3 件）の是正:

- 上の「`--all` はメッシュ未宣言のリリースを STRICT と読む」は不正確（`values-local.yaml` が `mesh.enabled: false` を与えるため、未宣言は `--all` でも初回に落ちる）。`--all` の実害は「`mesh.enabled: true` なのに `mtlsMode` が無い」ときに止まらずチャート既定の STRICT を読むこと。IADR-0487 に同じ訂正を追記した。
- 「`--all` の混入はスタブが区別しないため捕まらない（残余）」は解消した。helm スタブを実機に寄せ（namespace 指定が無ければ不在・`--all` は拒否）、読み先の行を固定した。監査の変異 M2（`--all`）・M6（get values の `-n` 欠落）に加え、`helm list` の `-n` 欠落も殺すことを実測した（いずれも 237 件中で赤）。
- 🟡3（`ISTIO` 無しの再実行でメッシュ宣言が外れる。以前からの挙動）は射程外として MSP#1713 へ起票した。
- 🟢: エラー文を貼り付けて実行できる形にし、リリース名を `MSP_HELM_RELEASE` に揃えた。`deploy/istio/README.md` の見出しを「初回は」に直した。

