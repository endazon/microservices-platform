---
title: ISTIO を付けない k8s-local-up.sh の再実行で、現行のメッシュ宣言（mesh.enabled）を引き継ぎ黙って外さない（#1713）
type: spec
status: done
related_ids: [NFR-16, ADR-0005, ADR-0021, IADR-0488, IADR-0487, IADR-0377, IADR-0317, IADR-0307]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
issue: "#1713"
---

# 仕様書: 再実行でメッシュ宣言を黙って外さない（#1713）

> 本仕様書は実装着手前に作成する。起点は #1713（#1710 の独立監査 🟡3 からの起票）。裁定（利用者）: **案 1 = 引き継ぐ**。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-16**（通信暗号化。恒久: サービス間 mTLS）。メッシュで動いているクラスタが起動器の再実行で
  PeerAuthentication・AuthorizationPolicy・サイドカー注入の宣言をまるごと失うことは、NFR-16 の統制が黙って外れることである（#1710 と同じ番号）。
- 計画 ADR: ADR-0005（メッシュ / mTLS）、ADR-0021（入口＝Istio Ingress Gateway）
- 関連 IADR: IADR-0307 決定 1（`ISTIO=1` の opt-in・既定は不変）、IADR-0487（#1710: mTLS モードの引き継ぎ）、
  IADR-0317（#1691: 移行済みの再実行の拒否）、IADR-0377（mode は helm だけが書く）。本件の判断は **IADR-0488** に置く。

## 何が起きるか（issue の要約）

- `k8s-local-up.sh` の [6/7] は `--reuse-values` 無しの `helm upgrade --install msp … -f values-local.yaml $ISTIO_MESH_ARGS`。
- `ISTIO_MESH_ARGS` は `ISTIO=1` のときだけ `--set mesh.enabled=true …` になり、それ以外は空。
- `values-local.yaml` は `mesh.enabled: false`。
- 結果: `ISTIO=1` で立てたクラスタに `ISTIO` を付けず再実行すると、メッシュ宣言（`istio-mtls.yaml` の描画物・`namespace.istioInjection`・
  `mesh.backchannelLogout.fromOutsideMesh`）がまるごと外れる。#1710 が塞いだ「STRICT が黙って PERMISSIVE へ戻る」と同じ型。

## 直し方（決定。詳細と却下案は IADR-0488）

1. **`ISTIO` を 3 値にする。** `1`＝入れる／`0`＝外す（明示）／未指定（空）＝**現行の helm の宣言を引き継ぐ**。
   それ以外の値（`true` 等）は副作用より前に拒否する（以前は黙って「外す」と同じに扱っていた。未指定に意味ができたので値域を閉じる）。
2. **読む口は #1710 の `current_mesh_mtls_mode` をそのまま使う。** 終了コードが既に 3 状態を分けている:
   0＝メッシュ宣言あり（`mesh.enabled: true` かつ `mtlsMode` が値域内。モードを返す）／1＝リリースが無い・メッシュ未宣言／2＝読めない。
   ラッパー関数は足さない（呼び出しは 1 回で `ISTIO` と `ISTIO_MTLS_MODE` の両方の判定に使う）。
3. **選び方（[2/7] の前・1 回だけ読む）。**
   - `ISTIO` 未指定 × 0 → `ISTIO=1` として以降を進め、`INFO: メッシュ（mesh.enabled: true）は現行を引き継ぎます（ISTIO 未指定。外すなら ISTIO=0）` を出す。
     **引き継いだ値は以降「明示されたのと同じ」に扱う**（コントロールプレーンの冪等な upgrade・注入ラベル・rollout restart・`LOCALEDGE=1` なら Istio のエッジ）。
     `ISTIO_MTLS_MODE` も未指定なら同じ読みから mTLS モードを引き継ぐ（#1710 の INFO をそのまま出す）。明示があれば明示が勝つ。
   - `ISTIO` 未指定 × 1 → 従来どおりメッシュ無し（初回・`ISTIO` 無しで立てたクラスタ）。
   - `ISTIO` 未指定 × 2 → **[2/7] の前に非 0 で止め、`ISTIO=1`（＋`ISTIO_MTLS_MODE`）か `ISTIO=0` の明示を求める**（fail-closed）。
     `ISTIO_MTLS_MODE` が明示されていても止める（`mesh.enabled: true` で `mtlsMode` だけ壊れている場合と、helm に届かない場合を
     終了コードで分けていないため。明示の `ISTIO=1` を足せば進む）。
   - `ISTIO=1` × `ISTIO_MTLS_MODE` 未指定 → #1710 のまま（引き継ぎ・初回 PERMISSIVE・読めなければ止める）。
   - `ISTIO=0` → **読まない**。従来の「`ISTIO` 無し」と同じ経路（`mesh.*` の `--set` 無し＝`values-local.yaml` の `false` が当たり、宣言が外れる）。
4. **移行済みの入口（#1691 の拒否）は据え置く。** 判定（`EDGE_ON_ISTIO`）と拒否は引き継ぎの読みより前に在り、`ISTIO` が `1` でない（未指定・`0`）ときは従来どおり止まる。
   未指定を「引き継ぎ」に読んで通すことは今回しない（Traefik へ戻す意図との区別は IADR-0317 決定 7 の 1 コマンドに委ねたまま。理由は IADR-0488）。
5. **`RESET_FLOOR` の WARN を引き継ぎの判定の後へ移す（規則 10）。** 冒頭の WARN は「`ISTIO=1` でない」を「床が効かない」と読んでいたが、
   未指定はメッシュを引き継げば `ISTIO=1` になり床が効く。値域の検査（0/1/空以外の拒否）は冒頭に残す。
6. **既定経路（`ISTIO` 未指定）で `helm list` を 1 回読むようになる。** #1710 は「既定経路では読まない（バイト等価）」を固定していたが、
   引き継ぎは既定経路で読まなければ成り立たない。バイト等価は `ISTIO=0` の側へ移す（`ISTIO=0` は読まない・`mesh.*` を付けない）。

## 窓の表（規則 11）

窓は「再実行の前のクラスタの宣言（前の端＝helm の `mesh.enabled`）」と「再実行の指定（後の端＝`ISTIO`）」の間にある。
増える側＝メッシュを入れる（false → true）、減る側＝外す（true → false）。3 つの形で比べた（✓＝期待どおり）。
C の列は `k8s-local-up.test.js` の #1713 節が全升目を実測で固定する。

| プローブ（前の宣言 × `ISTIO` × 読み取り） | 期待 | A: 後の端だけ（従前。`1` 以外は外す） | B: 前の端だけ（現行を優先・指定を無視） | **C: 両端（明示 ＞ 現行 ＞ 無し・読めなければ止める）** |
| --- | --- | --- | --- | --- |
| Q1 true × 未指定 × 読める（減る側・黙った除去＝#1713） | true を保つ | ✗ 外す | ✓ | ✓ |
| Q2 false（`ISTIO` 無しで立てた）× 未指定 | false のまま | ✓ | ✓ | ✓ |
| Q3 無い（初回）× 未指定 | 入れない（従来） | ✓ | ✓ | ✓ |
| Q4 false × `ISTIO=1`（増える側・明示で入れる） | true | ✓ | ✗ 入らない | ✓ |
| Q5 true × `ISTIO=0`（減る側・明示で外す） | 外す | ✓ | ✗ 外れない | ✓ |
| Q6 true × 未指定 × 読めない | 止まる | ✗ 外して進む | ✗（値が無い） | ✓ |
| Q7 何でも × `ISTIO=0` × 読めない | 外して進む（読まない） | ✓ | ✗ | ✓ |
| Q8 true（STRICT）× 未指定 × mTLS 未指定 | mesh true ＋ STRICT（未移行 LOCALEDGE なら [6/7] PERMISSIVE → [5/5] STRICT） | ✗ 外す | ✓（mesh）／段取りは C と同じ | ✓ |

片側だけの形は必ず逆側が空く（A は減る側の Q1・Q6・Q8、B は増える側の Q4 と明示の除去 Q5）。C だけが全升目を満たす。
A の Q1 は変異 M1（形 A そのもの）で赤になることを実測する。B の列は形の定義からの導出である。

**TOCTOU（読みと [6/7] の間に別の実行がリリースを書き換える）**は扱わない（#1710 と同じ。起動器を同じクラスタへ並行に走らせる運用は無い）。

## 母集合（規則 9: `ISTIO` と `mesh.enabled` / `mesh.*` の全文走査）

走査（2026-10-02・`origin/develop` = `e0d35208`）:

- `git grep -l -E '\bISTIO\b' -- ':!src/ai-stock-trading' ':!.ai-context/specs' ':!.ai-context/superpowers' ':!.ai-context/adr' ':!CHANGELOG.md'` ＝ 20 ファイル
- `git grep -l -E 'mesh\.(enabled|mtlsMode)|mesh:' -- ':!src/ai-stock-trading' ':!.ai-context' ':!CHANGELOG.md'` ＝ 17 ファイル（重複を除いた和は下表）

**読み書きの経路（`mesh.*` の `--set`・values）**

| 経路 | 読む／書く | 扱い |
| --- | --- | --- |
| `k8s-local-up.sh` [6/7] `helm upgrade --install msp … -f values-local.yaml $ISTIO_MESH_ARGS`（`--reuse-values` 無し） | 書く（`mesh.enabled`・`mesh.mtlsMode`・`namespace.istioInjection`・`mesh.backchannelLogout.fromOutsideMesh`） | **是正**: `ISTIO` 未指定の引き継ぎで `ISTIO_MESH_ARGS` が付く |
| `deploy/local/values-local.yaml` `mesh.enabled: false` | 書く（既定値） | **対象外**: 初回・`ISTIO=0` の値として正しい |
| `scripts/lib/mesh-mtls-mode.sh` `set_mesh_mtls_mode`（`--reuse-values --set mesh.mtlsMode`） | 書く | **対象外**: `--reuse-values` なので `mesh.enabled` を変えない |
| `scripts/lib/mesh-mtls-mode.sh` `current_mesh_mtls_mode`（`helm get values msp -n … -o yaml`） | 読む | **流用**（変更なし。注記だけ #1713 の用途を足す） |
| `scripts/istio-edge-up.sh` [5/5]（`set_mesh_mtls_mode`） | 書く（mode のみ） | **対象外**: 起動器から呼ばれるのは `ISTIO=1`（引き継ぎ含む）のときだけ |
| `scripts/check-stack-ready.js` G12（`helm get values -a`・`ISTIO=1` で requireMesh） | 読む | **対象外**: 門は env の `ISTIO` を見る。未指定の再実行で門へ `ISTIO=1` を渡さないと G12 は notice 側（緩い側）になるが、起動器の宣言は外れていないので乖離は無い。門の側の意味論は変えない（残余） |
| `deploy/helm/.../values.yaml`・`templates/{istio-mtls,namespace,drift-postsync-job}.yaml` | 本番像の既定 | **対象外** |
| `docs/how-to/deployment.md`・`deploy/argocd/README.md`・`docs/operations/operations.md`（`mesh.enabled`） | ArgoCD／本番像の手順 | **対象外**: 起動器の再実行を述べない |
| `scripts/helm-private-notes-sync-authz.test.js`・`MeshMtlsTests.cs` | 試験（チャート描画） | **対象外** |

**`ISTIO` の意味を述べる箇所（規則 10: 「未指定＝無し」と読んでいる記述）**

| 箇所 | 記述 | 扱い |
| --- | --- | --- |
| `scripts/k8s-local-up.sh` 冒頭 RESET_FLOOR の WARN | `ISTIO != 1` を「床が効かない」と読む | **是正**（判定の後へ移す） |
| `scripts/k8s-local-up.sh` #1710 の判定・注記「ISTIO 無しの既定経路では読まない」 | 既定経路で読まない | **是正**（統合した判定へ書き換え） |
| `scripts/k8s-local-up.sh` 入口の移設の注記「ISTIO 未設定なら実行されない＝既定はバイト等価」 | 未設定＝無し | **是正**（`ISTIO=0`・未指定で初回／メッシュ未宣言のとき） |
| `scripts/k8s-local-up.sh` #1316 注記「ISTIO 無しでは表面化しない」 | メッシュ無しの意味 | **対象外**: 「メッシュ無し」の挙動の記述として正しい |
| `scripts/k8s-local-up.test.js` #1159「ISTIO 未設定なら mesh.* の --set が 1 つも足されない」・#1710「既定経路では helm の読み取りを足さない」 | 既定のバイト等価 | **是正**（前者は「初回（リリース無し）なら」と `ISTIO=0` の 2 つに、後者は `ISTIO=0` は読まない・既定は `helm list` だけ、へ） |
| `deploy/istio/README.md` §経路B・§mTLS・§再実行 | `ISTIO=1` の opt-in | **是正**（3 値と引き継ぎを足す） |
| `deploy/local/README.md` 末尾「Istio/mTLS … は無効」 | 既定は無効 | **是正**（再実行は引き継ぐ・外すのは `ISTIO=0`） |
| `deploy/local/edge/README.md`「`ISTIO` 未設定なら本オーバーレイの挙動は 1 バイトも変わらない」 | 未設定＝無し | **是正**（メッシュを宣言していないクラスタでは、に限定） |
| `deploy/local/edge-istio/README.md`・`kustomization.yaml`「既定（ISTIO 未設定）では一切適用されない」 | 未設定＝無し | **是正** |
| `scripts/README.md` `lib/mesh-mtls-mode.sh` 行 | 読む口の用途 | **是正**（`ISTIO` 未指定の引き継ぎにも使う） |
| `.github/workflows/integration-stack.yml` `ISTIO: … && '1' \|\| ''` | 比較実行は空 | **対象外**: CI は毎回新しいクラスタ（リリース無し）なので空＝初回＝メッシュ無しで従来と同じ。`'0'` へ変えると `t25-monthly-summary.js` が「床の無い比較実行」を `ISTIO` が空で見分けているのが崩れる |
| `scripts/t25-monthly-summary.js`・`t25-rerun-on-chance-red.js` | CI ログの `ISTIO` を読む | **対象外**（上と同じ） |
| `scripts/check-stack-ready.js` | 門の `ISTIO=1` | **対象外**（上表） |
| `scripts/istio-edge-up.sh`・`k8s-local-down.sh`・`verify-oidc-edge-flow.sh` | `ISTIO=1` で立てる旨の案内 | **対象外**: 立てるときの指定として正しい |
| `deploy/local/edge-istio/virtualservice-app.yaml`・`deploy/helm/.../wikijs.yaml`・`values-local.yaml` のコメント | `ISTIO=1` の構成を述べる | **対象外** |
| `docs/how-to/obsidian-plugin-install.md`・`docs/operations/object-storage-seaweedfs-cutover-runbook.md` | `ISTIO=1` の構成を述べる | **対象外** |
| `.ai-context/adr/*`・`.ai-context/specs/*` | 凍結記録 | **対象外**（本文は書き換えない）。ただし IADR-0307 決定 1「既定は完全に不変」と IADR-0487 の残余（MSP#1713 で扱う）は意味が変わるため、日付つき追記で IADR-0488 を指す |

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `scripts/k8s-local-up.sh` | `ISTIO` の値域検査（冒頭）・統合した引き継ぎ判定（[2/7] の前）・RESET_FLOOR の WARN の移動・注記 |
| `scripts/lib/mesh-mtls-mode.sh` | 読む口の注記に #1713 の用途を足す（関数は変えない） |
| `scripts/k8s-local-up.test.js` | #1713 節・#1159 / #1710 の既定バイト等価の試験を 3 値に合わせて改める |
| `scripts/README.md` / `deploy/istio/README.md` / `deploy/local/README.md` / `deploy/local/edge/README.md` / `deploy/local/edge-istio/README.md` / `deploy/local/edge-istio/kustomization.yaml` | 上表 |
| `.ai-context/adr/IADR-0488_...md`（新規）・`README.md`（索引） | 判断の記録 |
| `.ai-context/adr/IADR-0487_...md` / `IADR-0307_...md` | 日付つき追記（IADR-0488 を指す） |

**IADR-0487 への追記ではなく IADR-0488 を起こす理由**: #1710 は `ISTIO=1` の中の mTLS モードの選び方であり、既定経路（`ISTIO` 未指定）には触れなかった。
本件は**既定経路の意味**（IADR-0307 決定 1「既定は完全に不変」）を変え、`ISTIO` の値域を閉じ、#1691 の拒否との関係を決める。
1 IADR = 1 意思決定に従い分ける（読む口の流用は IADR-0487 の決定 2 をそのまま引く）。

## 受け入れ基準（→ 試験。`k8s-local-up.test.js` の #1713 節）

1. 未指定 × 現行 true（STRICT）→ [6/7] に `--set mesh.enabled=true` と `mesh.mtlsMode=STRICT` が付き、INFO を出す。Istio の段（コントロールプレーン・rollout restart）も通る。
2. 未指定 × 現行 false → `mesh.*` を付けない。INFO を出さない。
3. 未指定 × リリース無し → 従来どおり（`mesh.*` 無し・`helm get values` を呼ばない）。
4. `ISTIO=0` × 現行 true → `mesh.*` を付けない（外す）。helm の読み取り（`list` / `get values`）をしない。
5. 読めない（`helm list` 失敗・`helm get values` 失敗・`mesh.enabled: true` で `mtlsMode` 欠落）× 未指定 → `[2/7]` の前に非 0。
   `ISTIO=1` / `ISTIO=0` の明示を告げる。`docker build`・`helm upgrade`・`kubectl label` が呼ばれない。`ISTIO_MTLS_MODE` を明示しても止まる。
6. 未指定 × 現行 true × `ISTIO_MTLS_MODE=PERMISSIVE` 明示 → mesh true ＋ PERMISSIVE（明示が勝つ）。
7. 未指定 × 現行 STRICT × 未移行 `LOCALEDGE=1` → [6/7] PERMISSIVE、`istio-edge-up.sh` [5/5] で STRICT（段取りは明示と同じ）。
8. `ISTIO` が 0 / 1 / 空 以外 → クラスタに触れる前に非 0。
9. RESET_FLOOR: 未指定 × 現行 true × `LOCALEDGE=1` なら WARN を出さない（床が効く）。未指定 × リリース無しなら従来どおり WARN。
10. 移行済み × 未指定 → 従来どおり拒否（#1691。helm を読まない）。

## 変異（自己変異で赤になること。2026-10-02 実測）

`k8s-local-up.test.js` は最初の失敗で止まるので、各変異で最初に赤になった試験を記す。変異は 1 件ずつ当て、退避した原本（作業ツリーの外）を
`cp` で戻して `diff -q` で復元を確認した（7 件とも復元済み）。変異前の全件は 247 件で緑（#1710 時点の 237 件 ＋ #1713 節 10 件）。

| 変異 | 結果（exit=1 と最初の失敗） |
| --- | --- |
| M1. `ISTIO` 未指定を引き継がない（`ISTIO=1` を `:` に。窓の表の形 A） | 赤: 「#1713 の再発: ISTIO 未指定の再実行でメッシュ宣言（mesh.enabled=true）を外した」 |
| M2. `ISTIO` 未指定で読めないときに止めずメッシュ無しへ倒す（`-ge 2` に `&& [ -n "${ISTIO:-}" ]`） | 赤: 「helm list が失敗: 読めないのに進んだ（メッシュを黙って外すか推測で入れる）」 |
| M3. `ISTIO=0` でも読む（判定の条件を `!= "1"` に） | 赤: 「ISTIO=0 なのに現行の宣言を読みに行った」 |
| M4. `ISTIO` の値域を閉じない | 赤: 「ISTIO=true を受け付けた（黙って「外す」と読む）」 |
| M5. mTLS の引き継ぎを `ISTIO` の引き継ぎより前に置く（引き継いだ `ISTIO=1` でモードを引き継がない） | 赤: 「メッシュは保ったが mTLS モードを引き継いでいない（#1710 との組み合わせ）」 |
| M6. RESET_FLOOR の WARN を冒頭（引き継ぎの前）へ戻す | 赤: 「床が効く起動（引き継いだ ISTIO=1 ＋ LOCALEDGE=1）で「効かない」と告げた」 |
| M7. #1691 の拒否を `ISTIO=0` だけに狭める（未指定を通す） | 赤: 既存の #1691 節「移行済みなのに LOCALEDGE=1 だけで Traefik 経路へ進んだ」（#1713 節の据え置きの試験より先に当たる） |

窓の表の形 B（前の端だけ）は実装した変異を置いていない（列は形の定義からの導出）。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/scripts.test.js` / `node scripts/scripts.repo.test.js`
- 文書系: `check-trace-blocks` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links` /
  `check-adr-index-sync` / `check-reading-budget` / `check-commit-messages --range origin/develop..HEAD`
- 稼働クラスタでは実行しない（スタブのみ）。
