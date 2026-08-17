---
title: 作業仕様書 — 波 11 末クロス監査の是正 7 件
type: spec
status: done
related_ids:
  - NFR-11
  - ADR-0047
  - NFR
  - IADR-0091
  - IADR-0094
  - IADR-0183
  - IADR-0204
  - IADR-0206
  - IADR-0220
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR-11 の対象。認証基盤〔Keycloak〕を名指ししている)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md"
related_specs:
  - "20260817_issue-834_nfr11-scope-catchup.md"
  - "20260817_issue-836_kit-class-x-rejudgement.md"
  - "20260817_issue-841_admin-entrypoint-https.md"
  - "20260817_wave10-audit-followup.md"
  - "../adr/IADR-0220_admin-entrypoint-tls-and-http-redirect.md"
  - "../adr/IADR-0206_local-edge-tls-cert-manager.md"
---

# 作業仕様書: 波 11 末クロス監査の是正

## 1. 起点

波 11（#840 / #843 / #844）のマージ後、**書いた側とは別の、フレッシュな文脈のエージェント 2 種**
（`adr-guardian` / `traceability-auditor`）へ **diff と受け入れ基準だけを渡して**監査させた
（実装者の主張は渡していない）。**証跡（実行コマンドと生出力）を必須**とした。

**両監査とも「計画 `ADR-0047` / `NFR-11` への重大な違反 0 件」**である（確認できた項目は §5）。
本書はそのうえで挙がった**追随漏れ 7 件**を是正する。**新規 IADR は起こさない** ——
7 件はいずれも**既存決定の「記述の射程」の問題**であり、決定そのものを動かさないためである。

**起点 ID の取り方**: 実体側の起点は `NFR-11` / `ADR-0047`（是正 2〜6）。
是正 1（CHANGELOG の remap）と是正 7（確定済み仕様書の訂正記録）は**メタ作業**であり、
計画の非機能要件表 `NFR-01`〜`NFR-27` に**当たる番号が無い**ため無採番 `NFR` を用いる
（`.claude/rules/traceability.md` §`NFR-xx` の**場合 2**。**同規約はこの場合に計画へ環流しないと明記している**）。

**追記ブロックの起票 ID について**: 本作業には専用の issue を起票していない（親が PR を起こす）。
`.claude/rules/traceability.repo.md`「**注記そのものへ起票 ID を書き**」を満たすため、
**`［2026-08-17 追記 / 波 11 末クロス監査］`** を用いる。**番号でない起票元を書く先例**は実測で 3 件あり、
**[[IADR-0211]] が `［2026-08-16 追記 / 波 7 末クロス監査］` を 2 ブロック持ち**、
**[[IADR-0214]]`:228` がその作法を引いている**（ほかに `［2026-08-07 追記 / クロス監査 §4・§5・A-1・G-a］` 4 件）。

```
$ git grep -c '追記 / 波 7 末クロス監査' -- docs/
docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md:2
docs/adr/IADR-0214_gate-inputs-subset-of-workflow-paths.md:1
```

**`#841` を名乗らせない**のは、**#841 の PR はこの追記を書いていない**ためである（実在しない出典を作らない）。
**`#834` も同じ理由で使わない。**

## 2. 是正する 7 件

| # | 指摘 | 種別 | 対象 | 対応 |
| --- | --- | --- | --- | --- |
| **1** | `5ed54b02` の件名スコープ `ADR-0030` が誤帰属 | **重大** | `scripts/changelog-overrides.json` | `remap`（`scope` → `NFR,IADR-0204`） |
| **2** | 「経路B の全エンドポイントが https」が成り立たない（**Keycloak issuer が平文のまま**） | **中（最重）** | `IADR-0220` §結果 / §検出しないこと、`IADR-0206` | 射程をエッジへ限定＋残件を明示 |
| **3** | `IADR-0220` の Supersedes が `IADR-0094` 決定 2 を取りこぼし | 中 | `IADR-0220` §関連 / frontmatter | 併記＋相互リンク化 |
| **4** | `IADR-0206:203-204` が、消えた引用元を指している | 中 | `IADR-0206` | 日付つき追記（本文は不変） |
| **5** | `IADR-0206:36` / `:209` の `IADR-0103` 誤帰属に隣接の目印が無い | 軽微 | `IADR-0206` | 各 1 行の目印（本文は不変） |
| **6** | `IADR-0220` の「live な ADR 4 件」が母集合を狭く見せる | 軽微 | `IADR-0220` | 1 句を添える |
| **7** | 確定済み仕様書の検証コマンドが実在しないファイルを指す | 軽微 | 本書 §4 | **書き換えず本書へ記録** |

---

### 是正 1 —— `5ed54b02` の `ADR-0030` は誤帰属である（`remap`）

```
$ git log --format='%s' -1 5ed54b02
chore(ADR-0030): キット分類 X 4 件を IADR-0204 決定 2 の 3 点突合で再判定する (#840)

$ git show --name-only --format='' 5ed54b02
.github/workflows/pr-title.yml
docs/how-to/session-handoff.md
docs/specs/20260817_issue-836_kit-class-x-rejudgement.md
scripts/README.md
scripts/check-commit-messages.js
scripts/check-cross-repo-refs.js
scripts/kit-sync-classification.json
scripts/scripts.repo.test.js
scripts/scripts.test.js
```

**`ADR-0030` はバックエンドアプリケーション層のライブラリ標準**の計画 ADR であるが、
**9 ファイルはすべて workflows / docs / scripts であり、バックエンドを 1 行も触っていない。**
作業仕様書 `20260817_issue-836_kit-class-x-rejudgement.md` の `related_ids` も
**`NFR`（無採番）＋ `IADR-0115/0130/0169/0183/0192/0201/0204/0207/0208`** であり、**`ADR-0030` を持たない** ——
**件名だけがこの区別を潰していた。**

**force push 禁止のため履歴は直さない。`scripts/changelog-overrides.json` の `remap` で生成物のみ是正する。**
`scope` を **`NFR,IADR-0204`** へ補正し、**`type`（`chore`）と `desc` は元コミットの値を保つ**
（誤っているのは起点 ID だけである）。

**これは既存の `2cd8508` の `reason` と同じ論理である**（名乗っている ID の成果物を 1 つも触っていない）。
`reason` の書き方もそちらへ揃えた。

**`NFR` を無採番にする根拠**（作業前に計画の ID 列を実測した）: `NFR-01`〜`NFR-27` は**すべて製品品質の要件**であり、
**キット同期・検査器・文書統制に当たる番号が無い**。`.claude/rules/traceability.md` の**場合 2** に該当する
（**無理に近い番号を付けない**。**この場合は計画へ環流しない**）。
`IADR-0204` は当該コミットが実際に適用した決定（キット分類 X の 3 点突合による再判定）である。

---

### 是正 2 —— **`NFR-11` の残債を「解消済み」と読ませない**（最も重い）

`docs/adr/IADR-0220:158` はこう書いていた。

> - **良い影響**: 経路B の全エンドポイントが https になり、`NFR-11` の「平文 HTTP を残さない」を経路B でも満たす。

**成り立たない。** 計画 `02_requirements/01_requirements.md` の `NFR-11` 行は、対象に
**「基盤 SPA・BFF・**認証基盤（Keycloak）**・Wiki.js」**を名指ししている。ところが経路B では
[[IADR-0091]] **決定 5**（`Accepted`・live・**本波で未改定**）が

> issuer は現行 `http://keycloak:8080`（手順A）を維持し、ツール UI のみ 50000 集約する

と定めており、**issuer は平文のまま残っている**（実測: `git grep -c 'http://keycloak:8080'` が
live な設定・コード 30 ファイル超に当たる）。

**放置すると `NFR-11` の残債が「解消済み」と読まれ、後続の監査が未達を閉じてしまう。**

**対応**（3 点）:

1. `IADR-0220` §結果 の当該行を**エッジ（`web`:80 / `websecure`:443 / `admin`:50000）に射程を限定**した書き方へ直す
2. `IADR-0220` §検出しないこと（明示）へ
   **「issuer（`http://keycloak:8080`）の https 化は `NFR-11` の残件として開いている」**を追加する
   （解消は **#780** が担う）
3. `IADR-0206` の `［2026-08-17 追記 / #834］`（`:150`）が残存平文を
   「**admin:50000 の管理ツール群と、80 の併存**」の 2 つに限って列挙しており、**Keycloak issuer が漏れている** ——
   **日付つき追記で足す**（**本文は書き換えない**）

---

### 是正 3 —— Supersedes の射程が過小（`IADR-0094` 決定 2 の取りこぼし）

[[IADR-0094]] **決定 2** は

> edge admin:50000 は現状 http だが、**将来の TLS 化に備え http/https 両方**を realm と Vault role の
> `allowed_redirect_uris` に登録する

と決めており、**同 ADR の却下代替案が「UI callback を https のみ登録」**である。
**#841 はその http 側を実際に削除した**（realm の `vault` client と `deploy/local/vault/oidc/bootstrap.sh`）。
**却下された代替案が採用形へ反転している。**

ところが `IADR-0220:185` は「Supersedes: **[[IADR-0206]] 決定 4 の 2 命題のみ**」と書いていた。
**「のみ」が過小である。**

**対応**: Supersedes へ **[[IADR-0094]] 決定 2 の「http/https 両登録」→「https のみ」** を併記し、
`IADR-0220` の `related_ids` へ **`IADR-0092` / `IADR-0093` / `IADR-0094` / `IADR-0095`** を追加して
**相互リンクにする**（現状は片方向で、4 件の側だけが `IADR-0220` を持っていた）。

#### **規則 10 で 1 件出た** —— `docs/adr/README.md` の `IADR-0220` 索引行も同じ 過小 だった

**是正後の語で引き直したところ**（`git grep -n 'IADR-0220' | grep -e 'Supersed' -e '改定' -e '覆'`）、
**索引行が「`[[IADR-0206]]` 決定 4 の P2・P3 を改定」としか書いていない**ことが出た。
**本 ADR 本体を直しただけでは、索引を読む人には過小のまま残る。**

**触る判断にした**（**`IADR-0206` の索引行とは別の判断**である。理由は §3 の除外表）。
**200 字上限**が効くため、意味を落とさずに詰めた。

| | 文字数 |
| --- | ---: |
| 変更前 | **195** |
| 「と `[[IADR-0094]]` 決定 2」を足しただけ | **209**（**上限超過**） |
| **採用**（「葉証明書は namespace ごとに置き … のまま」→「葉は ns ごと・…」で 10 字詰めた） | **199** |

**落とした語は本体の決定 3 が持っている**（「葉証明書は namespace ごと」）。**Supersede の射程は本体と一致した。**
**`IADR-0206` の索引行は 200 字ちょうどで 1 字も入らない**ため、**そちらは触らない**（§3）。

> **★ これは #841 が自分で 1 度直した「後半 → P2・P3」と同型の再発である。**
> 同 PR の作業仕様書 §2.6 の ★ ブロックは「`Supersedes` 欄が当初 P3 だけを挙げていた ——
> **Supersede の射程を過小に書いていた**」と自ら記録している。**同じ欄で、同じ向きの過小が 2 回目**である。
> 1 回目は同一 ADR 内（`IADR-0206` 決定 4）の取りこぼし、2 回目は**別 ADR（`IADR-0094`）の取りこぼし**であり、
> **「Supersede と書く前に、覆した相手を全部数える」が効いていない**。
> **「同型の事故が 2 回起きたら検査器を置く」の計数は、これで 2 回目に達した** ——
> ただし**機械化の当たりが無い**（「ある編集が他 ADR の決定を覆したか」は静的に判定できない。
> `docs/how-to/adr-supersede-citation-annex.md` が「機械検査は置いていない」と述べているのと同じ理由である）。
> **検査器は置かず、記録に留める**（`.claude/rules/traceability.repo.md` の追加条件は満たすが、
> **実装可能な検査が無い**ため。`CLAUDE.md`「検査器・規約の追加は『同型の事故が 2 回起きたら』」の趣旨は
> **無検査の規約を増やすことではない**）。

---

### 是正 4 —— `IADR-0206:203-204` が、消えた引用元を指している

```
$ git grep -c '実 TLS 証明書・admin entrypoint の TLS 化' -- deploy/
（0 件。#841 が deploy/local/edge/README.md を書き換えた）
```

同箇所は

> `deploy/local/edge/README.md` の「実 TLS 証明書・admin entrypoint の TLS 化は本オーバーレイのスコープ外（Tier 3）」
> という Tier 境界も、**実 TLS の側だけ**動かす。

と書いているが、**引用元はもう存在せず**、現行の README は

```
deploy/local/edge/README.md:185: **エッジ TLS は Tier 3 から外れた**（IADR-0206・#779）……
deploy/local/edge/README.md:186: **admin(50000) の TLS 化も Tier 3 から外れた**（IADR-0220・#841……）
```

と**両方が動いた**ことを書いている。**「実 TLS の側だけ」は偽である。**
**日付つき追記で是正する**（**本文は書き換えない** —— `IADR-0206` 本文は当時の記録である）。

---

### 是正 5 —— `IADR-0103` 誤帰属に、隣接の目印が無い

`docs/adr/IADR-0206` には `[[IADR-0103]]（admin entrypoint は平文 http）` が **2 箇所**残っている。

```
$ git grep -n 'admin entrypoint は平文 http' -- docs/adr/IADR-0206_local-edge-tls-cert-manager.md
docs/adr/IADR-0206_local-edge-tls-cert-manager.md:36:  …[[IADR-0103]]（admin entrypoint は平文 http）、
docs/adr/IADR-0206_local-edge-tls-cert-manager.md:209:決定 4（…）と [[IADR-0103]]（admin entrypoint は平文 http）も**そのまま**である
```

誤帰属であることは**決定 4 の追記（`:155-182`）に書いてある**が、
**`:36` は §起点・関連＝追記より前**にあり、**導線が届かない**。
`.claude/rules/traceability.repo.md`「**後継 ID は旧 ID の隣に置く**」に照らして揃える。

**対応**: `:36` と `:209` の**直下**へ 1 行ずつ
`［2026-08-17 追記 / 波 11 末クロス監査］この帰属は誤りである（詳細は決定 4 の追記）` を置く（**本文は書き換えない**）。

**「直下」の実装**: `:36` は 3 行にわたる 1 個の箇条書き（`35-37`）の途中、`:209` は 2 行にわたる 1 段落（`209-210`）の
先頭行である。**行の途中に割り込むと箇条書き・段落そのものを壊す**（＝本文の書き換えになる）ため、
**それぞれ当該箇条書き / 段落の直後**に置いた。**本文の文字列は 1 バイトも変えていない。**

---

### 是正 6 —— 「live な ADR 4 件」が母集合を狭く見せる

`IADR-0220:140-145` の ★ ブロックは「**`Accepted` の live な ADR 4 件**」と書いている。
**同じ前提を持つ live な ADR は実測 6 件**であり、4 件は「**同じ形の追記を新規に入れたもの**」の数である。

**実作業に漏れは無い** —— #841 の作業仕様書 §2.7 は
「[[IADR-0091]] と [[IADR-0206]] には追記を入れたのに、同型の前提崩れを持つ 4 件にだけ入れていなかった」と
**6 件すべてを列挙している**。**ADR 単体を読むと 4 件が全数に見える**ことだけが問題である。

**対応**: 「（`IADR-0091` / `IADR-0206` は別扱いで是正済み。**計 6 件**）」を 1 句添える。

---

## 3. 母集合（規則 9・10。走査基準 `7aa09766` ＝本ブランチの分岐元）

**規則 9**（誤りの側の文字列で全走査してから挙げる）を各是正について適用した。
**`head` / `sed` で切っていない**（規則 7）。パス除外は `':!planning' ':!src/ai-stock-trading'` のみ。

**規則 8（時点と引き算）**: 下の生のヒットは **`git grep <軸> 7aa09766`** の値であり、
**本書と本 PR の是正を 1 件も含まない**（`7aa09766` は本ブランチの分岐元＝波 11 の最終コミットである）。
**着地後の working tree で同じ軸を引くと数は増える** —— 実測した増分は次のとおりで、
**増えた分はすべて「本 PR が新しく書いた説明文」であり、是正すべき誤りではない**。

| 軸 | `7aa09766` | 着地後（`git add -A` 済みで実測） | 増分 | 内訳（**すべて本 PR が新しく書いた説明文であり、是正すべき誤りではない**） |
| --- | ---: | ---: | ---: | --- |
| 1 | 4 | **13** | +9 | 本書 7（§2 の `git log` / `git show` の引用と走査基準）／`changelog-overrides.json` 2（`hash` と `reason`）／`IADR-0220:64` は元からある走査基準 |
| 2 | 1 | **6** | +5 | 本書 4／`IADR-0220` 2（§結果 の是正後の文と §関連 の追記表。**「全エンドポイントが https」という主張としては 0 件**になった） |
| 4 | 2 | **6** | +4 | 本書 3／`IADR-0206` 2（**本文 1 ＋ 追記 1**。追記は旧記述を literal に引用するため必ずヒットする） |
| 5 | 7 | **14** | +7 | 本書 5／`IADR-0206` 4（**本文 2 ＋ 目印 2**）／`IADR-0220` 1（元から） |
| 8 | 1 | **7** | +6 | 本書 6（是正 7 の記録そのもの）。**`docs/specs/20260817_issue-834_*:155` の 1 件は書き換えていない** |
| 9 | 60 | **63** ファイル | +3 | 本書・`IADR-0206`・`IADR-0220`（**残件として言及しただけ**。issuer の実体は 1 バイトも触っていない） |

**この面は、`docs/adr/` の追記が旧記述を literal に引用してヒットするのと同じ型**である ——
**「自分の記録が走査に混じる」ことを見越し、時点を明記して引き算を見せる**（規則 8）。
**軸 2 が重要**: 語としては 1 → 6 に増えたが、**「経路B の全エンドポイントが https」という主張は 0 件**になった。
**数ではなく主張を数える。**

| 軸 | 走査コマンド | 生のヒット | 判断 |
| --- | --- | ---: | --- |
| 1 | `git grep -n '5ed54b02'` | **4 件** | `IADR-0220:60`（走査基準の記録）・`docs/specs/` 3 件。**いずれも「走査基準としての sha」であり、件名スコープの主張ではない**。触らない |
| 2 | `git grep -n '全エンドポイント'` | **1 件** | `IADR-0220:158`。**是正 2** |
| 3 | `git grep -n 'NFR-11' \| grep -e 解消 -e 充足 -e 満た -e 達成 -e 解決`（`docs/specs/` 除外） | **4 件** | `IADR-0206:129`・`:132`（**#834 追記が既に撤回済み**。本文は当時の記録）／`IADR-0220:158`・`:159`。**`:158` のみ是正**、`:159` は「適用範囲について実装と計画が逆を向いた状態が解消する」＝**真**なので触らない |
| 4 | `git grep -n '実 TLS 証明書・admin entrypoint の TLS 化'` | **2 件** | `IADR-0206:203`（**是正 4**）／`docs/specs/20260816_issue-779_*:85`（**確定済み。触らない**） |
| 5 | `git grep -n 'admin entrypoint は平文 http'` | **7 件** | `IADR-0206:36`・`:209`（**是正 5**）／`IADR-0220:191`（**誤帰属である旨を説明している側**＝正しい）／`docs/specs/20260817_issue-841_*` 4 件（確定済み） |
| 6 | `git grep -n -e 'live な ADR' -e 'live な権威文書'` | **33 件** | うち `IADR-0220:140-141` **のみが件数を主張**している（**是正 6**）。他 32 件は**母集合の定義**（`.claude/rules/traceability.repo.md` / `docs/adr/IADR-0191` / `docs/how-to/adr-supersede-citation-annex.md` / 確定済み `docs/specs/` / `scripts/scripts.repo.test.js` の固定文字列）であり**件数を持たない** |
| 7 | `git grep -n -e '2 命題' -e '命題のみ' -e '後半'` | **33 件** | `IADR-0220:185`（**是正 3**）／`IADR-0206:174`（**「決定 4 の 2 命題だけ」＝ `IADR-0206` 側の記述としては真**。`IADR-0094` は同 ADR の決定ではないため、この行は過小になっていない）／残り 31 件は無関係（ESO の step 後半・`SyncErrorRedactor` の文字列処理・確定済み仕様書の「前半 / 後半」） |
| 8 | `git grep -n 'check-doc-status\.js'` | **1 件** | `docs/specs/20260817_issue-834_*:155`（**確定済み。是正 7 として本書へ記録**）。**同型の取り違えはこの 1 件だけ**である |
| 9 | `git grep -l 'http://keycloak:8080'`（ファイル単位） | **60 ファイル** | **是正の対象ではない**（issuer の https 化は **#780** の射程。本書は「残件として開いている」ことを ADR に書くだけである）。**1 件も書き換えない** |

### 規則 10 —— **是正後の語で引き直した**（**是正前の語では捕まらない**）

| 引き直しの軸（**是正で新しく書いた語**） | 生のヒット | 判断 |
| --- | ---: | --- |
| `git grep -n -e '残件として開いて' -e '計 6 件' -e '3 entrypoint'` | **10 件** | すべて**本 PR が今回書いた行**（`IADR-0206` 1・`IADR-0220` 5・本書 4）。**他所と矛盾しない** |
| `git grep -n 'IADR-0220' \| grep -e 'Supersed' -e '改定' -e '覆'` | **17 件** | **1 件が過小だった** —— `docs/adr/README.md` の `IADR-0220` 索引行（**是正 3 に取り込んだ**）。`IADR-0206` の P1/P2/P3 表と `docs/specs/20260817_issue-841_*` は**その ADR の側から見た記述として真**であり触らない |
| `git grep -n 'live な ADR 4 件'` | **5 件** | `IADR-0220:145`（**是正 6 で直後に補足を足した**）／`docs/specs/20260817_issue-841_*:271` の見出し（**「4 件へ追記」＝ 実際に新規追記を入れた数なので真**。確定済みでもある）／本書 3 件 |
| `git grep -n 'NFR-11' \| grep -e 満た -e 解消`（`docs/specs/` 除外） | **8 件** | 増えた 4 件はすべて**本 PR が「満たし切ったとは主張しない」と書いた行**。**「満たした」と主張する live な行は 0 件**になった |

**引き直しで実際に 1 件出た**（索引行）。**是正前の語（`全エンドポイント` / `2 命題のみ`）では 1 件も捕まらない**
—— 索引行はどちらの語も持たないためである。**これが規則 10 が要る理由そのものである。**

### 除外したもの（黙って落とさない）

| 対象 | 件数 | 除外理由 |
| --- | ---: | --- |
| 確定済み（`status: done`）の `docs/specs/` | 軸1 3・軸4 1・軸5 4・軸6 多数・軸8 1 | **書いた時点の記録**。後から注記を足すのは記録の改竄（`.claude/rules/traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」）。**是正 7 の対象もここに入る** |
| `docs/adr/README.md` の **`IADR-0206`** 索引行 | 1 | **状態列は動かない**（`Accepted` のまま。覆ったのは決定 4 の 2 命題だけ）。かつ**実測ちょうど 200 字で 1 字も入らない**（下記）。**同行は Supersede の射程を主張していない**（「`IADR-0091` 決定 3 を Supersede」＝本波と無関係で、いまも真）ため**過小になっていない**。**導線は本体の追記ブロックで足りている** |
| `docs/adr/README.md` の **`IADR-0220`** 索引行 | — | **触った**（除外していない）。**「`IADR-0206` 決定 4 の P2・P3 を改定」が是正 3 で過小になる**ため。**195 → 199 字**（上限 200）に詰めて `IADR-0094` 決定 2 を併記した。詳細は §2 是正 3 |
| `deploy/` 配下の `http://keycloak:8080`（issuer） | 軸9 の大半 | **#780 の射程**。本書は残件として開いていることを ADR に書くだけで、実体は触らない |
| `IADR-0091` 決定 5 本体 | 1 | **`Accepted`・live で、本波は改めない。** issuer の最小案維持は #780 が改める |

**索引行の実測**（触らない判断の裏づけ。**上限は 200 字**）:

```
$ node -e "
const fs=require('fs');
const lines=fs.readFileSync('docs/adr/README.md','utf8').split('\n');
for (let i=0;i<lines.length;i++){
  const l=lines[i];
  if(/^\|\s*\[IADR-(0206|0220)\]/.test(l)){
    const title=(l.split('|')[2]||'').trim();
    console.log('line', i+1, 'title列 文字数=', [...title].length);
  }
}"
line 262 title列 文字数= 200
line 276 title列 文字数= 195
```

**`IADR-0206` の索引行は実測ちょうど 200 字で余白が無い**（1 文字も足せない）。
`IADR-0220` も残り 5 字であり、**意味のある注記は入らない**。
**どちらも触らない。** 導線は追記ブロックで足りており、**状態列（`Accepted`）も動かない**。

## 4. 是正 7 —— **確定済み仕様書の誤りを、書き換えずにここへ記録する**

`docs/specs/20260817_issue-834_nfr11-scope-catchup.md`（**`status: done`・#843 でマージ済み**）の
`:155` は検証コマンドとして

```
node scripts/check-doc-status.js
```

を挙げているが、**このファイルは実在しない。**

```
$ ls scripts/check-doc-status*
scripts/check-doc-status-vocabulary.js

$ node scripts/check-doc-status.js
Error: Cannot find module '/home/user/wt-w11fix/scripts/check-doc-status.js'
```

**書かれたとおりに追試すると `Cannot find module` で落ちる** ——
**追試不能な検証手順は、検証したという記録の価値を下げる。**

| | 同書の記述 | 実在するもの |
| --- | --- | --- |
| 検証コマンド 2 行目 | `node scripts/check-doc-status.js` | **`node scripts/check-doc-status-vocabulary.js`** |

**同書は確定済みなので書き換えない**（`.claude/rules/traceability.repo.md`）。**訂正をここへ残す。**
波 10 の [`20260817_wave10-audit-followup.md`](20260817_wave10-audit-followup.md) §3 が採った形と同じである。

**紛れの出どころ**: 計画リポには `/check-status`（実体 `tools/doc-checks/check-doc-status.js`）があり、
**名前が近い**。**実装リポ側に同名は無い。** 同型の取り違えは**この 1 件だけ**であることを軸 8 で実測した。

## 5. 監査が「違反 0 件」と確認した項目（記録）

**是正 7 件は「追随漏れ」であり、以下は欠陥が無いことを確認した項目である。**
**両者を混ぜて「監査で 7 件出た」と読ませない**ために、確認できた側も残す。

| 観点 | 結果 |
| --- | --- |
| 計画 `ADR-0047` / `NFR-11` への**重大な違反** | **0 件**（`adr-guardian` / `traceability-auditor` の双方） |
| `planning/` submodule | **無改変**（未 populate。`git submodule status` が `-767a9d48…`） |
| `src/ai-stock-trading` | **無改変**（同上 `-7f69fb50…`） |
| 確定済み `docs/specs/` の書き換え | **0 件**（波 11 の 3 本はすべて新規追加 `A`） |
| 必読規約の総量 | **50,132 B で増減 0**（`CLAUDE.md` ＋ `.claude/rules/*.md`。予算 51,200 B） |
| `IADR-0206` ↔ `IADR-0220` の**決定 4 の生死割り当て** | **完全一致**（P1 生・P2 死・P3 死。両 ADR の表が同じ 3 命題を同じ判定で持つ） |
| `IADR-0103` への**誤帰属** | **無し**（`IADR-0220` は Supersede しないと明示。`IADR-0103` 本体は無改変） |

**本書はこの 7 観点を 1 つも動かさない** —— 是正は `docs/adr/` 2 本 ＋ `scripts/changelog-overrides.json` ＋
本書の 4 ファイルに閉じ、`.claude/rules/` と `CLAUDE.md` を**触らない**（予算は増減 0 のまま）。

## 6. 対象範囲

**変更するファイル**

| ファイル | 変更 |
| --- | --- |
| `scripts/changelog-overrides.json` | `5ed54b02` の `remap` を 1 件追加（既存 5 件は不変） |
| `docs/adr/IADR-0220_admin-entrypoint-tls-and-http-redirect.md` | frontmatter `related_ids` ＋4／★ ブロックに 1 句／§結果 の射程限定／§検出しないこと に残件 1 項／§関連 Supersedes に `IADR-0094` 決定 2 |
| `docs/adr/IADR-0206_local-edge-tls-cert-manager.md` | 日付つき追記 4 ブロック（`:36` の目印・issuer 残件・Tier 境界の是正・`:209` の目印）。**本文は 1 バイトも書き換えない**（`git diff` の削除行 **0**） |
| `docs/adr/README.md` | **`IADR-0220` の索引行 1 行のみ**（195 → 199 字。`IADR-0094` 決定 2 を併記）。**`IADR-0206` の索引行は触らない** |
| `docs/specs/20260817_wave11-audit-followup.md`（本書） | 新規 |

**変更しないもの**

- `deploy/` `scripts/*.js` `src/` `.claude/` `CLAUDE.md` `planning/` `src/ai-stock-trading` `feedback/`
- 確定済み `docs/specs/`（波 11 の 3 本を含む）
- `docs/adr/README.md` の **`IADR-0206` 索引行**（200 字ちょうどで余白が無く、かつ**過小になっていない**。§3 の除外表）
  —— **`IADR-0220` の索引行は触った**（§2 是正 3）
- **[[IADR-0094]] 本体** —— 是正 3 で本 ADR を `Supersedes` の相手として明記したが、
  **同 ADR 側の導線は既に足りている**（`［2026-08-17 追記 / #841］` が [[IADR-0220]] を引き、
  `related_ids` にも `IADR-0220` が入っている）。
  `.claude/rules/traceability.repo.md` が求めるのは「**旧 ID を残し、後継を併記する**」であって
  「`Supersede` という語を使う」ことではない。**語を揃えるためだけに live な ADR を再度触らない。**
- **[[IADR-0091]] 決定 5** —— `Accepted`・live で、**本波は改めない**。issuer の最小案維持を覆すのは **#780** である
- **新規 IADR**（起こさない。7 件はいずれも既存決定の**記述の射程**の問題であり、決定を 1 つも動かさない）

## 7. 検証（[[IADR-0183]] の順序）

`git add -A` → 検査器 → コミット → HEAD を読む検査器。

```
node scripts/check-doc-links.js
node scripts/check-doc-status-vocabulary.js
node scripts/check-doc-type-vocabulary.js
node scripts/check-cross-repo-refs.js
node scripts/check-plan-id-qualification.js
node scripts/check-adr-numbering.js
node scripts/check-reading-budget.js
node scripts/check-kit-sync.js
node scripts/check-realm-constraints.js
node scripts/k8s-local-up.test.js
node scripts/gen-changelog.js            # ★ remap を入れたので必ず走らせる
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
# コミット後
node scripts/check-doc-updated.js
node scripts/check-commit-messages.js origin/develop..HEAD
```

**★ `changelog-overrides.json` を触ったら `gen-changelog.js` を必ず走らせ、生成物 `CHANGELOG.md` を確認する。**
過去に `3441861` の remap が**生成物経由で `check-cross-repo-refs.js` を赤くした前例**がある。
**生成された行が表記規約に違反しないか**を必ず見る。

**実測**（`node scripts/gen-changelog.js` の標準出力）:

```
$ node scripts/gen-changelog.js | grep '5ed54b02'
- **NFR,IADR-0204**: キット分類 X 4 件を IADR-0204 決定 2 の 3 点突合で再判定する (#840) (5ed54b02)

$ node scripts/gen-changelog.js | grep '^- \*\*ADR-0030\*\*'
（4 行。いずれも #838 / #463 / #839 / #479 であり、5ed54b02 は**もう含まれない**）
```

**表記規約の検査**: 生成物を一時的に `CHANGELOG.md` へ置いて 3 検査を走らせ、**すべて緑**であることを確かめた
（`check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links`）。
生成された行が持つ番号は **`(#840)`（自リポ・裸でよい）** だけで、他リポジトリ参照を含まない。

### **`CHANGELOG.md` は本 PR ではコミットしない**（判断と根拠）

`CHANGELOG.md` は **CI（`.github/workflows/changelog.yml`）が develop への push 時に再生成し、
`github-actions[bot]` の PR で更新する補助成果物**である（`CLAUDE.md`「補助成果物は…**手で書き足さない**」）。

**現時点のコミット済み `CHANGELOG.md` は develop の先端より古く、`5ed54b02` をまだ 1 行も含んでいない。**
そのため本 PR で再生成して差し替えると **481 挿入 / 433 削除**の差分になり、
**remap の 1 行と、bot が未反映のまま溜めた無関係な数十コミットが混ざる** ——
**レビューできる変更単位**（`CLAUDE.md`）を壊す。

**先例の差分が 2〜3 行で済んでいたのは、当時 `CHANGELOG.md` が develop と同期していたからである**
（`78c9753a` = +2/-1、`96c2dbeb` = +3/-0）。**同じ「最小差分」の意図を満たす選択は、いまは
「触らないで bot に任せる」側である** —— 本 remap は **develop へマージされた瞬間に bot が反映する**。

**したがって本 PR の `CHANGELOG.md` の差分は 0 行**であり、remap が効くことは上の**生成物の実測**で示す。

**判定の作法**: **終了コードは判定ではない。判定行を読む。判定行は末尾とは限らない。**
**終了コードをパイプで終端しない**（`cmd > log 2>&1; echo "EXIT=$?"`）。
**走査の出力を `head`/`sed` で切って読まない。**

### `check-kit-sync.js` の赤は**本 PR 由来ではない**（環境由来・実測で切り分けた）

`node scripts/check-kit-sync.js` は本作業の環境で **EXIT=1**（`.github/workflows/pr-title.yml` と
`scripts/scripts.test.js` が「分類 A なのにキットとバイト一致でない」）になる。**これは本 PR の変更とは無関係である。**

```
$ git diff HEAD --stat -- .github/workflows/pr-title.yml scripts/scripts.test.js
（空 —— 本 PR はこの 2 ファイルを 1 バイトも触っていない）

$ git rev-parse --short=8 HEAD
7aa09766                      # ＝ develop の先端。本ブランチはまだコミット 0 本

$ git log -1 --format='%h %s' -- .github/workflows/pr-title.yml
5ed54b02 chore(ADR-0030): キット分類 X 4 件を … 再判定する (#840)
```

**原因は、キットの参照先が pin より古いことである。** `check-kit-sync.js` は
`planning/tools/impl-handoff-kit/repo-template` → `../project-planning/…` の順に探すが、
**本環境では submodule `planning/` が未 populate** のため隣接クローンへ落ちる。その隣接クローンは
**`5e53b9d2`（2026-08-16 04:07）** であり、**submodule の pin `767a9d48` を含んでいない**
（`git cat-file -t 767a9d48` が `Not a valid object name`）。
差分の実体は `pr-title.yml` の `PR_NUMBER:` 5 行であり、**#836 が「計画 pin `767a9d48` でキットが完全実装した」と
記録している当のもの**である —— **キット側が新しく、参照先だけが古い。**

**したがって本 PR は `check-kit-sync` を新たに赤くしていない。**
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` は同検査を companion 試験として実行し
**その地点で abort する**ため、**全件を通すときは `KIT_DIR` を存在しないパスにして skip（fail-open）へ倒して測る**
（`check-kit-sync.js` §環境変数）。**両方の結果を報告に載せる。**

## 8. 受け入れ基準

- [ ] `scripts/changelog-overrides.json` が妥当な JSON で、`overrides` が **6 件**（既存 5 ＋ 新規 1）
- [ ] `node scripts/gen-changelog.js` の**生成物**が `5ed54b02` を **`NFR,IADR-0204`** で出力し、
      `ADR-0030` を名乗る行から当該コミットが消えている（**`CHANGELOG.md` 自体はコミットしない**。§7）
- [ ] `docs/adr/README.md` の `IADR-0220` 索引行が **200 字以内**で `IADR-0094` 決定 2 を併記している
- [ ] `IADR-0220` §結果 が **`NFR-11` を経路B で満たし切ったと主張していない**
- [ ] `IADR-0220` §検出しないこと に **issuer の残件**が挙がっている
- [ ] `IADR-0220` §関連 Supersedes に **`IADR-0094` 決定 2** が併記されている
- [ ] `IADR-0220` の `related_ids` に **`IADR-0092`〜`IADR-0095`** がある（相互リンク成立）
- [ ] `IADR-0206` の**本文の削除行が 0 行**（`git diff` の `-` は frontmatter を含めても 0）
- [ ] 確定済み `docs/specs/` の変更 0 件・`planning/` と `src/ai-stock-trading` の変更 0 件
- [ ] **新規 IADR を起こしていない**・`.claude/rules/` と `CLAUDE.md` の変更 0 件（予算 50,132 B のまま）
- [ ] §3 の走査を**規則 10 で引き直し**、新たな誤りが出ていない

## 9. 検証結果（判定行）

| 検査器 | EXIT | 判定行 |
| --- | ---: | --- |
| `check-doc-links.js` | 0 | `OK: 688 件の Markdown に破損した相対リンクはありません` |
| `check-doc-status-vocabulary.js` | 0 | `OK: 648 件の仕様書の status が値域に収まっています` |
| `check-doc-type-vocabulary.js` | 0 | `OK: 662 件の文書の type が…値域に収まっています` |
| `check-cross-repo-refs.js` | 0 | `OK: 1776 件に他リポジトリ参照の表記違反はありません` |
| `check-plan-id-qualification.js` | 0 | `OK: 1449 件に他プロジェクト ID の修飾違反はありません` |
| `check-adr-numbering.js` | 0 | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です` |
| `check-reading-budget.js` | 0 | `warn Claude Code: 50,132 バイト（予算 51,200 の 97.9%）` —— **増減 0** |
| `check-realm-constraints.js` | 0 | `OK: 1 ファイルに…ADR-0026 からの逸脱はありません` |
| `k8s-local-up.test.js` | 0 | `✓ 75 tests passed` |
| `gen-changelog.js` | 0 | `出力しました` ＋ 生成物に `- **NFR,IADR-0204**: … (#840) (5ed54b02)` |
| `REQUIRE_REPO_TESTS=1 scripts.test.js` | 0 | **`✓ 651 tests passed`**（基準 651 と一致。`KIT_DIR` を存在しないパスにして `check-kit-sync` を skip） |
| `check-kit-sync.js` | **1** | **環境由来・本 PR 由来ではない**（上記「`check-kit-sync.js` の赤は本 PR 由来ではない」） |
