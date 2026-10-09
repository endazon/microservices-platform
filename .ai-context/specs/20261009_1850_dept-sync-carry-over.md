---
title: 作業仕様書 — 起動器の再実行で稼働 PoC の部門属性の同期（DepartmentAttributeSync__Mode）を黙って Off に戻さず、明示 ＞ 現行 ＞ 初回の既定で引き継ぐ（#1850）
type: spec
status: done
related_ids: [SC-17, FR-05, ADR-0116, IADR-0473, IADR-0488, IADR-0487, IADR-0377]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 1 の 2026-10-09 補完（稼働 PoC の配備値は Fix・コードの既定は Off）
issue: "#1850"
---

# 作業仕様書 — 部門属性の同期の値を起動器の再実行で引き継ぐ（#1850）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `f74e2935`（PR #1848 を含む）。
> 起点は PR #1848（#1783）の独立監査 🟡-1 からの分離。利用者裁定（2026-10-09）: **すぐ実装する**（稼働 PoC へ `Fix` を当てる前に入れる）。
> 🔴 **稼働中のクラスタには何も実行しない。** 稼働 PoC の値の切り替えは運用者が行う（本 PR は起動器・試験・手順だけ）。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0116 決定 1 の 2026-10-09 補完**（planning#741 項目 4。稼働 PoC の配備値は `Fix`、コードの既定は `Off`）。
  配備値が起動器の再実行で黙って外れると、補完の「属性は同期が追いつく」が無警報で崩れる（異動した人が前の部門の資料を見続ける＝ABAC が緩む向き。FR-05）。
- SC-17（利用者アカウント管理）・FR-05（ABAC）。
- 実装 ADR: IADR-0473（部門の同期。2026-10-09 追記の「残るリスク」が本件）、IADR-0488（`ISTIO` の「明示 ＞ 現行 ＞ 初回の既定」と fail-closed）、
  IADR-0487（読む口 `helm get values`。`--all` ではない）、IADR-0377（値は helm を通してだけ書く）。
- **新しい IADR は起こさない。** IADR-0488 の形を別の値へ当てはめる直接の拡張であり、決めることは IADR-0473 への日付つき追記（本件の判断）と
  IADR-0488 への日付つき追記（読みの共有で決定 3「`ISTIO=0` は読まない」の射程が変わること。規則 10）で足りる。

## 何が起きるか（issue の要約・実測）

- 稼働 PoC の `DepartmentAttributeSync__Mode` は、運用者が `helm upgrade msp … --reuse-values --set 'services.authorization.extraEnvAppend[0].name=DepartmentAttributeSync__Mode' --set '…[0].value=Fix'` で入れる
  （`docs/operations/operations.md` §稼働 PoC を `Fix` にする手順）。
- `scripts/k8s-local-up.sh` の [6/7] は `--reuse-values` 無しの `helm upgrade --install msp … -f deploy/local/values-local.yaml $ISTIO_MESH_ARGS $LOCALEMBED_ARGS`。
- `values-local.yaml`・チャートの `values.yaml` のどちらも `services.authorization.extraEnvAppend` を持たない（実測: `grep -n extraEnvAppend` で `bff` の 1 件だけ）。
  したがって再実行で authorization の `extraEnvAppend` は空になり、同期は `Off` に戻る。`Off` では計器の系列が無く `DepartmentSyncNotCorrecting` も鳴らない。

## 直し方（決定）

1. **新しい明示の env `DEPT_SYNC_MODE`**（`Off` / `Report` / `Fix` / 未指定）。それ以外（`fix`・`true` 等）は副作用より前に拒否する
   （`ISTIO`・`RESET_FLOOR` と同じく冒頭で落とす。サービス側は大小文字を区別しないが、起動器の指定は綴りを 1 つに閉じる）。
2. **選び方は 明示 ＞ 現行 ＞ 初回の既定（IADR-0488 と同じ）。**
   - `DEPT_SYNC_MODE=Report|Fix` → その値を宣言する。
   - `DEPT_SYNC_MODE=Off` → **何も読まず `--set`・`-f` を足さない**（＝従来の起動器と完全に同じ。同期はコードの既定 `Off`）。
   - 未指定 × 現行のリリースに `DepartmentAttributeSync__Mode` の要素がある → その値（大小文字・前後の空白はサービスと同じく許して `Off` / `Report` / `Fix` へ正規化）を引き継ぐ。
   - 未指定 × リリースが無い（初回・CI）・要素が無い → **何も足さない**（CI の描画は従来とバイト等価）。
3. **宣言の形は追加の values ファイル（`-f <一時ファイル>`）で、authorization の `extraEnvAppend` のリストを丸ごと与える。**
   リストは「現行の authorization の `extraEnvAppend` から `DepartmentAttributeSync__Mode` を除いた要素（字面のまま）＋ 新しい `DepartmentAttributeSync__Mode`」。
   - `--set 'services.authorization.extraEnvAppend[0]…'` は採らない: `--reuse-values` と併せればリストごと置き換わり現行の他の要素
     （運用者が足した `DepartmentAttributeSync__Interval` 等）が消える。現行の値を同じコマンドの `-f` で与えて添字 0 を `--set` すると、
     要素 0 だけが上書きされ、他の位置にある古い `DepartmentAttributeSync__Mode` が同名で残る（実測: helm v3.16.2。`helm-dept-sync-values.test.js` の対照）。他の要素を `--set` で写し直す形は、`value: "false"` のような文字列を bool に読み替える
     （チャートの `{{- if .value }}` で env が消える）ので採らない。`helm get values -o yaml` の字面をそのまま values ファイルへ写せば型が変わらない。
   - 一時ファイルは `mktemp -d` の下に置き、[6/7] の直後と EXIT で消す（値は秘密ではないが、残さない）。
4. **読みは 1 回。** `helm list`（6 旗の和）＋ `helm get values msp -n <ns> -o yaml` を 1 回だけ読み、メッシュの判定（IADR-0487 / IADR-0488）と本件の判定の両方に使う。
   読む口は `scripts/lib/mesh-mtls-mode.sh` の `current_mesh_mtls_mode` から `current_release_values`（0＝値を出力／1＝リリースが無い／2＝読めない）を切り出し、
   `current_mesh_mtls_mode` はそれを呼ぶ形に寄せる（外から見た振る舞いは同じ）。起動器はこの読みを変数に 1 回だけ取る。
   - 読む条件: メッシュは従来どおり（`ISTIO` 未指定、または `ISTIO=1` かつ `ISTIO_MTLS_MODE` 未指定）。本件は `DEPT_SYNC_MODE` が未指定・`Report`・`Fix` のとき
     （`Report` / `Fix` も他の要素を保つために読む）。`Off` は読まない。
5. **読めないときは止める（fail-closed）。** [2/7] の前に非 0 で止め、何も書き換えない。
   - リリースの読み取りの失敗（helm に届かない・`get values` の失敗）× 未指定・`Report`・`Fix` → 止める（未指定で進むと `Off` へ黙って戻し、`Report` / `Fix` で進むと他の要素を黙って消す）。
     読まずに進めるのは `DEPT_SYNC_MODE=Off` の明示だけ（同期は `Off` になる）。
   - 未指定 × 要素が壊れている（値域外・`secretKeyRef` 等の値でない形・同名が 2 つ以上・`extraEnvAppend` が読めない形〔流れ形式〕）→ 止めて `DEPT_SYNC_MODE` の明示を求める。
     明示の `Report` / `Fix` は壊れた要素を置き換えて進む（読み取りは成功しているので他の要素は保てる）。
6. **ログに選んだ値と出どころを出す**（値は秘密ではない）: `INFO: 部門属性の同期（DepartmentAttributeSync__Mode）は <値>（<明示 DEPT_SYNC_MODE｜現行を引き継ぐ｜既定>）`。
   他の要素を保つときは件数と名前も出す。
7. **運用仕様書**: 🔴「再実行すると外れて `Off` に戻る」を引き継ぎの説明に改め、値を変える正規の手段を `DEPT_SYNC_MODE` の明示（起動器の再実行）にする。
   helm の `--reuse-values` の手順は「起動器を走らせずに値だけ変える」手段として残し、両者が同じ要素（authorization の `extraEnvAppend` の
   `DepartmentAttributeSync__Mode`）を読み書きすることを書く。trace ブロックへ本仕様書を足す。

### 却下した形

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | [6/7] に `--reuse-values` を足す | ✗ IADR-0487 論点 1 の案 B と同じ（`values-local.yaml` から消したキー・以前の opt-in が残り続ける） |
| B | `check-stack-ready.js` の門で `Fix` でなければ赤にする（issue の代替） | △ 検知はできるが黙った変化自体は起きる。裁定は「引き継ぐ」 |
| C | 値を `values-local.yaml` へ書く | ✗ IADR-0473 2026-10-09 追記 決定 2（CI の使い捨てスタックも同じ上書きを使う。計画は helm の既定を変えないとした） |
| D | `--set 'services.authorization.extraEnvAppend[0]…'` で mode だけ渡す | ✗ 現行の他の要素を消すか、古い Mode と同名で並ぶ（上の 3） |
| **E** | **明示 ＞ 現行 ＞ 何もしない。他の要素ごと values ファイルで宣言し直す。読めなければ止める**（**採用**） | ○ |

## 窓の表（規則 11）

窓は「再実行の前のリリースの宣言（前の端＝`helm get values` の authorization の `extraEnvAppend`）」と「再実行の指定（後の端＝`DEPT_SYNC_MODE`）」の間にある。
増える側＝同期を強める（`Off`→`Report`/`Fix`）、減る側＝弱める（`Fix`→`Off`）。3 つの形で比べた（✓＝期待どおり）。C の列は `k8s-local-up.test.js` の #1850 節が全升目を実測で固定する。

| プローブ（前の宣言 × `DEPT_SYNC_MODE` × 読み取り） | 期待 | A: 後の端だけ（従前。明示が無ければ何も足さない） | B: 前の端だけ（現行を優先・明示を無視） | **C: 両端（明示 ＞ 現行 ＞ 何もしない・読めなければ止める）** |
| --- | --- | --- | --- | --- |
| P1 `Fix` × 未指定 × 読める（減る側・黙った除去＝#1850） | `Fix` を保つ | ✗ `Off` へ戻す | ✓ | ✓ |
| P2 無い（初回・CI）× 未指定 | 何も足さない（バイト等価） | ✓ | ✓ | ✓ |
| P3 無い × `Fix`（増える側・明示で入れる） | `Fix` | ✓ | ✓（前が無い） | ✓ |
| P4 `Report` × `Fix`（増える側・Report → Fix の切り替え） | `Fix` | ✓ | ✗ `Report` のまま | ✓ |
| P5 `Fix` × `Off`（減る側・明示で外す） | `Off`（何も足さない） | ✓ | ✗ 外れない | ✓ |
| P6 `Fix` × 未指定 × 読めない | 止まる | ✗ `Off` へ戻して進む | ✗（値が無い） | ✓ |
| P7 `Fix` ＋ 他の要素 × 未指定 | 両方を保つ | ✗ 両方消える | ✓ | ✓ |
| P8 `Fix` ＋ 他の要素 × `Report` | `Report` ＋ 他の要素 | △ 素朴な `--set [0]` なら他が消える | ✗ | ✓ |

片側だけの形は必ず逆側が空く（A は減る側の P1・P6・P7、B は明示の P4・P5）。C だけが全升目を満たす。A の P1 は変異 M1（形 A そのもの）で赤になることを実測する。

**TOCTOU（読みと [6/7] の間に別の操作がリリースを書き換える）**は扱わない（IADR-0487 / IADR-0488 と同じ。起動器と helm の手操作を同じリリースへ並行に流す運用は無い）。

## 母集合（規則 9・10）

走査（2026-10-09・`origin/develop` = `f74e2935`。`':!src/ai-stock-trading' ':!CHANGELOG.md'`）:

- `git grep -l DepartmentAttributeSync__Mode` ＝ 5 ファイル（`IADR-0473`・`20261007_1772_residual-ledger.md`・`20261009_1783_dept-sync-poc-fix.md`・`docs/operations/operations.md`・`AuthorizationService/Program.cs`）
- `git grep -n -E 'extraEnvAppend'`（`deploy/`・`scripts/`・`docs/`・`.ai-context/adr/`）
- `git grep -n -E 'ISTIO=0[^\n]*(読まない|読まず|バイト等価)|helm の読み取りを足さない'`（規則 10: 読みを共有すると「`ISTIO=0` は読まない」が誤りになる記述）

| 箇所 | 記述 | 扱い |
| --- | --- | --- |
| `scripts/k8s-local-up.sh` [6/7] | `--reuse-values` 無しの upgrade | **是正**（本件の `-f`） |
| `scripts/k8s-local-up.sh` 冒頭の値域検査 | `ISTIO`・`RESET_FLOOR` | **是正**（`DEPT_SYNC_MODE` を足す） |
| `scripts/k8s-local-up.sh` #1713 の注記「ISTIO=0 は読まない（…既定のバイト等価はこちらへ移した）」 | 読みの有無 | **是正**（メッシュのためには読まない。全く読まないのは `DEPT_SYNC_MODE=Off` を併せたとき） |
| `scripts/lib/mesh-mtls-mode.sh` `current_mesh_mtls_mode` | 読む口 | **是正**（`current_release_values` を切り出す。振る舞いは同じ） |
| `docs/operations/operations.md` §部門属性の同期 の 🔴「再実行で外れる」・手順 1〜3・確かめ方 | 再実行で外れる | **是正**（引き継ぎ・`DEPT_SYNC_MODE`） |
| `docs/operations/operations.md` §部門属性の同期 の「helm values・compose には既定値を置いていない」 | 既定 | **対象外**（変えない） |
| `.ai-context/adr/IADR-0473` 2026-10-09 追記の「残るリスク」 | 再実行で外れる | **追記**（凍結記録の本文は書き換えず、日付つき追記で塞いだことを足す） |
| `.ai-context/adr/IADR-0488` 決定 3・論点 3「`ISTIO=0` は読まない」 | 読みの有無 | **追記**（同上） |
| `.ai-context/specs/20261009_1783_dept-sync-poc-fix.md:56`・`20261007_1772_residual-ledger.md` | 残る穴の記録 | **対象外**（point-in-time の記録。書き換えない） |
| `scripts/k8s-local-up.test.js` #1710 / #1713 の「明示なら読まない」「`ISTIO=0` は読まない・読めなくても進む」 | 試験 | **是正**（`DEPT_SYNC_MODE=Off` を併せた形へ。主張の射程を「メッシュの判定は読まない」に保つ） |
| `scripts/README.md` `lib/mesh-mtls-mode.sh` 行・`k8s-local-up.sh` 行 | 用途 | **是正**（新しい lib と env を足す） |
| `deploy/local/README.md` §既知の制約（`ISTIO` の 3 値の記述の並び） | 起動器の引き継ぎ | **是正**（1 項目を足す。env の一覧表は無い） |
| `.github/workflows/{integration-stack,cutover-rehearsal,ci}.yml` | 起動器を呼ぶ | **対象外**: 毎回新しいクラスタ。cutover-rehearsal の 2 回目もリリースに要素が無いので何も足さない（バイト等価） |
| `AuthorizationService/Program.cs`・`DepartmentAttributeSyncHostedService.cs` | 値の解釈（大小文字を区別しない・空は `Off`） | **対象外**（現行の値の正規化の根拠として読むだけ） |

## 受け入れ基準

- [x] `DEPT_SYNC_MODE` 未指定 × 現行 `Fix` → [6/7] の values ファイルに `DepartmentAttributeSync__Mode: Fix`、INFO に「現行を引き継ぐ」（P1）
- [x] 未指定 × リリース無し・要素無し → `-f` を足さない。helm の呼び出しは従来の既定と同じ（P2。CI のバイト等価）
- [x] 明示 `Report` / `Fix` が現行に勝つ（P3・P4）。明示 `Off` は何も読まず何も足さない（P5）
- [x] 読めない × 未指定・`Report`・`Fix` → [2/7] の前に止まる。`Off` は進む（P6）
- [x] 現行の他の要素（値の形・`secretKeyRef` の形とも）を字面のまま保つ（P7・P8）
- [x] 値域外の `DEPT_SYNC_MODE` は副作用より前に拒否する。現行の要素が壊れていれば未指定は止まり、明示は置き換えて進む
- [x] 読みは 1 回（メッシュの判定と共有）
- [x] 生成した values ファイルを実物のチャートで描画し、authorization-service の env に `DepartmentAttributeSync__Mode` と他の要素が出る（helm がある環境の試験）
- [x] 運用仕様書の 🔴 を引き継ぎの説明へ改める。trace ブロックを更新する
- [x] 変異で赤になることを確かめる（下表）

## 検証の記録

実行環境: 作業ツリー（基点 `f74e2935`）・helm v3.16.2・mawk 1.3.4・shellcheck。

| コマンド | 結果 |
| --- | --- |
| `node scripts/k8s-local-up.test.js` | **296 件すべて緑**（基点 285 件）（#1850 節 11 件を追加。#1710 / #1713 節の「読まない」4 か所は `DEPT_SYNC_MODE=Off` を併せた形へ） |
| `node scripts/helm-dept-sync-values.test.js` | **4 件緑**（実物のチャートで描画・対照 2 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **1016 件緑**（`live-scripts.json` へ `lib/dept-sync-mode.sh` を offline として分類） |
| `shellcheck -x scripts/k8s-local-up.sh scripts/lib/dept-sync-mode.sh scripts/lib/mesh-mtls-mode.sh` | 既存の SC1091（info）1 件だけ（基点と同数。新しい指摘なし） |
| `check-trace-blocks` / `check-adr-numbering` / `gen-knowledge-graph --check` / `check-reading-budget` / `check-plan-id-qualification` / `check-cross-repo-refs` / `check-doc-links` | すべて OK |

**実測（helm v3.16.2）**: 現行の値を `-f` で与えて `--set 'services.authorization.extraEnvAppend[0]…'` を併せると、要素 0（`DepartmentAttributeSync__Interval`）が上書きされ、
位置 1 の古い `DepartmentAttributeSync__Mode: Report` が残って同名が 2 つ描かれた（当初の想定「リストごと置き換わる」とは違う壊れ方。どちらでも D は採れない）。
`--set …value=false` は bool になり、チャートの `{{- if .value }}` で env の値が消える。

**変異**（`k8s-local-up.test.js` の #1850 節だけを走らせ、1 つずつ当てて戻す。全件赤）:

| 変異 | 結果 |
| --- | --- |
| M1 形 A（未指定は現行を引き継がない） | 赤（P1「#1850 の再発」） |
| M2 他の要素を落とす | 赤（P7） |
| M3 読めなくても進む | 赤（P6） |
| M4 `Off` も helm を読む | 赤（P5） |
| M5 現行が明示に勝つ | 赤（P3・P4） |
| M6 読みを共有しない（読み直す） | 赤（読み 1 回） |
| M7 `DEPT_SYNC_MODE` の値域の検査なし | 赤（値域） |
| M8 判定器が authorization に限らない（bff の同名を拾う） | 赤（P2・判定表） |
| M9 現行の値を大小文字で区別する | 赤（判定表） |
| M10 壊れた要素（値でない形・同名 2 つ）を「無い」と読む | 赤 |
| M11 流れ形式のリストを「無い」と読む | 赤 |
| M12 一時ファイルを消さない | 赤（P1「一時ファイルが残った」） |
| H1（`helm-dept-sync-values.test.js`）他の要素の字下げを崩す | 赤 |
