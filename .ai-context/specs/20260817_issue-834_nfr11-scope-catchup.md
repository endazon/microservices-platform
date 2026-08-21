---
title: 作業仕様書 — IADR-0206 の「経路B は NFR-11 の適用外」という整理を撤回し、ADR-0047 の裁定へ追随する
type: spec
status: done
related_ids:
  - NFR-11
  - ADR-0047
  - ADR-0023
  - IADR-0206
  - IADR-0091
  - IADR-0116
  - IADR-0183
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-11 の適用範囲・2026-08-16 の裁定)
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
related_specs:
  - "../adr/IADR-0206_local-edge-tls-cert-manager.md"
  - "20260816_issue-779_edge-tls-termination.md"
  - "20260817_planning-pin-767a9d48.md"
---

# 作業仕様書: `IADR-0206` の `NFR-11` 適用外整理の撤回（#834）

## 1. 起点となる ID（トレーサビリティ）

- **`ADR-0047`**（エッジ TLS 証明書の運用はローカル検証環境〔経路B〕にも及ぶ。`Accepted`。
  利用者裁定 2026-08-16 / 裁定依頼 planning#383）。ブランチ名・コミット件名の起点 ID はこれを採る。
- **`NFR-11`**（全経路の HTTPS 化）。同裁定で**適用範囲は環境を問わない**ことが明記された。
- **`IADR-0206`**（経路B のエッジ TLS 終端。`Accepted`）—— 本作業で条文を追随させる対象。
- Issue: **#834**（条文の追随）。実体側は **#841** が持つ。

計画書リンク: `ADR-0047`（計画リポ）、
`01_requirements.md`（計画リポ）（`NFR-11` 行）。

## 2. 目的・背景

計画側 `NFR-11` に利用者裁定 2026-08-16 が入り、**ローカル検証環境（経路B）も適用内**と確定した。
裁定文は実装側の読みを名指しで否定している —— 「`LOCALEDGE=1` が loopback へ bind する閉域であり
『外部から到達し得る』に当たらない」という読みは**採らない**。理由は
**経路B で HTTPS を省く選択肢を将来にわたって閉じるため**である。

`IADR-0206` の決定 4 に付された ★ ブロックは、**まさにその否定された読みを条文にしている**
（「`NFR-11` が言う『外部から到達し得る』に当たらない、という整理で**適用外**とする」
「`NFR-11` の充足先は本番像であり…」）。`.claude/rules/adr.md` の大原則（計画書が正・実装を計画へ合わせる）に従い、
**実装側の条文を計画へ合わせる**。

**覆ったのは枠付けだけである。** `ADR-0047` 決定 1・2 はエッジ TLS 終端の設計を定めるもので、
`IADR-0206` の決定 1〜6（cert-manager ＋ selfsigned→CA の 2 段・`ADR-0023` の設計要件 3 点の踏襲）は
**既にその形を採っている**。計画側も「本 ADR の決定 2 と同じ形を採っている」と明記している。
したがって**決定そのものは改めない**。

## 3. 対象範囲

- **対象**: `docs/adr/IADR-0206_local-edge-tls-cert-manager.md` の
  ① 決定 4 の ★ ブロックへの**日付つき追記ブロック**（`［2026-08-17 追記 / #834］`）による撤回、
  ② frontmatter の `related_ids` へ `ADR-0047` を**項目として併記**、③ `updated:` の前進。
- **対象外（＝ #841 の射程。一切触らない）**: `deploy/` 配下（`traefik-entrypoint.yaml`・
  `admin-ingress-*.yaml`・`argocd-ingress.yaml`・`platform-frontend-ingress.yaml`・realm の redirectUri）、
  `scripts/k8s-local-up.test.js` の 2 試験（現状の平文を固定している assert）、
  追跡下の平文 URL（`http://…localhost:50000`）。
- **対象外（射程外）**: 本番像の HTTPS 化（#780 / #782）、`planning/` submodule、`src/ai-stock-trading`。
- **新規 IADR は起こさない。** 既存決定の**枠付けの追随**であって新しい決定ではない
  （新規決定が要るのは #841 の項目 6 —— 決定 4「http 経路を残す・恒久リダイレクトを足さない」の射程が
  裁定で変わるかの判断であり、**実体を触る側が判断する**）。

## 4. 設計（何をどう書くか）

`.claude/rules/traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」に従う。

1. **元の整理を消さない。** ★ ブロックの本文はそのまま残し、その直後へ追記ブロックを足す。
2. **注記そのものへ起票 ID を書く** —— `［2026-08-17 追記 / #834］`。
3. **旧 ID を後継へ付け替えない。** frontmatter の `NFR-11` は残し、**その隣へ** `ADR-0047` を項目として足す
   （説明を混ぜない）。
4. `updated: 2026-08-16` → `2026-08-17`。
5. **`status` は `Accepted` のまま。** 決定の本体は `ADR-0047` に適合しており、覆ったのは関係の整理だけである。

追記に書く内容は次の 3 点に限る（実体の設計判断を先取りしない）。

- **「適用外」という整理を撤回する**。典拠は計画 `NFR-11` の利用者裁定 2026-08-16（裁定依頼 planning#383）と
  新設の `ADR-0047`。経路B は **`NFR-11` の適用内**である。
- **決定 1〜6 は改めない。** `ADR-0047` 決定 2（DNS-01 も Vault PKI も取れないドメインでは selfsigned CA を許容・
  設計要件 3 点は同じく守る）と本 ADR の決定 2・3 は同じ形である。
- **経路B に残る平文（admin:50000 の管理系・80 の併存）は `NFR-11` に対する未達であり、
  その解消は #841 が担う。** 決定 4 の射程が裁定で変わるかの判断も #841 に属する。

`docs/adr/README.md` の索引行は**触らない** —— `IADR-0206` の索引行は `NFR-11` にも「適用外」にも言及しておらず
（下記 §5 の走査で確認）、追記によって誤りにならない。先例としても `IADR-0091` の
`［2026-08-16 追記 / #779］` で索引行は更新されていない。

## 5. 母集合（規則 9・10。走査基準 develop `5ed54b02`・**本仕様書を書く前**）

**誤りの側の文字列で、追跡下の全ファイルを走査した。** `--include` も拡張子も使わず、
パスの除外（`':!planning' ':!src/ai-stock-trading'`）だけで取っている（規則 3）。

```
git grep -n -- "<語>" -- . ':!planning' ':!src/ai-stock-trading'
```

| # | 軸（語） | 生の数 | 是正対象 | 判断 |
| --- | --- | --- | --- | --- |
| 1 | `NFR-11` | 11 行 / 4 ファイル | `IADR-0206` の 5 行 | 他は `docs/specs/` 2 件と `feedback/` 1 件（下記 除外） |
| 2 | `適用外` | 8 行 / 8 ファイル | `IADR-0206` の 1 行 | 他は共通シェル・submodule 規約・到達し得ない分岐の話で無関係 |
| 3 | `到達し得` | 8 行 / 5 ファイル | `IADR-0206` の 2 行 | 他は ABAC 経路・防御的実装の話 |
| 4 | `閉域` | 17 行 / 11 ファイル | `IADR-0206` の 1 行（L128） | 他は Qdrant / Headlamp の**公開範囲**の記述 |
| 5 | `本番像` | 81 行 / 38 ファイル | `IADR-0206` の 1 行（L130「充足先は本番像」） | 他は helm / values の不変性の記述 |
| 6 | `充足` | 85 行 / 62 ファイル | 同上 1 行 | 他は要件充足・DoD の記述 |
| 7 | `loopback` | 9 行 / 8 ファイル | `IADR-0206` の 1 行（L128） | 他は bind の事実（`IADR-0091` L78・`k8s-local-up.sh` L47 等） |
| 8 | `HTTPS 化` | 16 行 / 14 ファイル | 0 行 | すべて #388 の追跡記述であり、適用外の主張ではない |
| 9 | `IADR-0206` | 48 行 / 15 ファイル | 0 行 | 参照元に「適用外」の枠付けを再述したものは無い（索引行 `docs/adr/README.md` L262 を含む） |

**軸を 1 本で終わらせていない**（規則 5。9 軸）。**結論: 是正すべき live な権威文書は
`docs/adr/IADR-0206_local-edge-tls-cert-manager.md` 1 件である。**

### 除外したものとその理由（黙って落とさない。規則 6）

| 除外 | 理由 |
| --- | --- |
| `docs/specs/20260816_issue-779_edge-tls-termination.md`（`NFR-11`） | **確定済み（`status: done`・マージ済み）の仕様書**。`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」。走査基準つきの過去の測定記録である |
| `docs/specs/20260817_planning-pin-767a9d48.md`（`NFR-11` / `適用外` / `到達し得` / `閉域`） | 同上。かつ**当該記述は裁定を正しく引用している側**であり、誤りではない |
| `feedback/20260816_adr-0023-scope-local-route.md`（`NFR-11` / `適用外` / `到達し得` / `閉域`） | 環流記録。本文 L61-64 は**裁定を仰ぐ問い**であって主張ではない。frontmatter は #833 で既に `status: accepted` へ追随済みであり、**本文で状態変更を言い直す追記は凍結の射程①**（[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 の 2026-08-16 追記 / 裁定 planning#369）に当たるため足さない |
| `deploy/local/edge/README.md` L74・L81（`平文 http のみ` / `TLS 化はスコープ外`） | **#841 の射程**（実体）。かつ現時点では**事実として正しい**記述であり、実体を直す PR が同時に直すのが正しい単位である |
| `scripts/k8s-local-up.test.js` L1181-1201 | 同上（#841）。**現状を固定している試験であり、期待値の反転は実体と同じ PR で行う** |
| `deploy/local/edge/README.md` L33・`docs/adr/IADR-0091` L78・`scripts/k8s-local-up.sh` L47（`loopback` / `閉域`） | **公開範囲（到達可能性）の記述**であり、`NFR-11` の適用可否の主張ではない。`NFR-11` 自身が「**HTTPS 化は通信路の保護であって到達可能性の制御ではない**」と両者を分けている |
| `docs/adr/README.md` の `IADR-0206` 索引行 | 走査の結果、`NFR-11`・`適用外`・`充足`・`本番像` のいずれも含まない。追記で誤りにならないため触らない（触ると 200 字上限・LCS 12 の制約検査が要るが、その必要が無い） |

### 規則 10（是正後の語で引き直す）

本作業で新たに書く語は `適用内` / `ADR-0047` / `#841` / `［2026-08-17 追記 / #834］` である。
**是正後にこれらで引き直し**、既存記述と矛盾・二重化しないことを確かめる（実施結果は §7 に記す）。
**導出値は走査ではなく計算し直す** —— 本作業では新しい件数（管理ツール 7 件等）を**自分で書き起こさない**
（既存条文の値には触れず、実測が要る値は #841 が引き直す）。

## 6. 受け入れ基準

- [ ] `IADR-0206` の ★ ブロックの**元の整理が残っている**（消していない）
- [ ] その直後に `［2026-08-17 追記 / #834］` の追記ブロックがあり、**適用外の整理を撤回**し、
      典拠として計画 `NFR-11` の裁定 2026-08-16（planning#383）と `ADR-0047` を挙げている
- [ ] frontmatter の `related_ids` に **`NFR-11` が残り**、その隣に `ADR-0047` が**項目として**ある
- [ ] `updated: 2026-08-17`・`status: Accepted`（据え置き）
- [ ] `deploy/` `scripts/` `docs/specs/`（確定済み）`feedback/` `planning/` `src/ai-stock-trading` に差分が無い
- [ ] 新規 IADR を起こしていない
- [ ] §5 の走査を規則 10 で引き直し、新たな誤りが出ていない

## 7. テスト方針・検証

条文のみの変更であり、実行コードの振る舞いは変わらない。検証は文書検査器で行う
（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序: `git add -A` → 検査器 → コミット → HEAD を読む検査器）。

```
node scripts/check-doc-links.js
node scripts/check-doc-status.js
node scripts/check-doc-type-vocabulary.js
node scripts/check-cross-repo-refs.js
node scripts/check-plan-id-qualification.js
node scripts/check-adr-numbering.js
node scripts/check-reading-budget.js
node scripts/check-kit-sync.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
# コミット後
node scripts/check-doc-updated.js
node scripts/check-commit-messages.js origin/develop..HEAD
```

**終了コードは判定ではない**（`skip` も `pass` も EXIT=0）。**判定行を出力の全文から読む。**

## 8. 計画書との差異

- 差異: **なし**。本作業は計画（`NFR-11` の裁定・`ADR-0047`）へ実装側の条文を合わせるものである。
- **環流済みの既知の誤りが 1 件ある（本作業では読み替えない）**: 計画 `ADR-0047` は経路B のエッジ TLS を
  実装した ADR を **`IADR-0205`** と 4 箇所で呼んでいるが、実体は **`IADR-0206`** である
  （`IADR-0205` は別件）。**planning#395 として起票済み**であり、**実装側から番号を読み替えない**
  （計画書が正である）。本仕様書・追記ブロックでは自リポの実体である `IADR-0206` を書く。

## 9. 未決事項

- **`IADR-0206` 決定 4（http 経路を残す・http→https の恒久リダイレクトを足さない）の射程が
  裁定で変わるか**は、**#841 の判断事項**（同 issue のやること 6）とする。本作業では判断しない ——
  実体（entrypoint の TLS 化・4 ingress・realm・平文 URL）を実走で確かめられる側が判断すべきであり、
  条文だけの PR で先に決めると「はず」で決めることになる。
