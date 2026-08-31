---
title: 作業仕様書 — ユニット直下の .gitkeep 枠 24 件の扱いを決め、実体のあるディレクトリの死んだ枠を撤去する（#1122）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - ADR-0065
  - IADR-0262
  - IADR-0321
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed。planning#378 → planning#445）
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 4
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted)
related_specs:
  - ./20260831_issue-1100_gitkeep-empty-frames.md
---

# 作業仕様書: ユニット直下の `.gitkeep` 枠の扱い（#1122）

起点: 実装 issue #1122（#1100 → PR #1125 の作業中に自分で検出して分割起票したもの）。

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`e1ccfc18`**（`git rev-parse --is-shallow-repository` = **false**）。
**#1122 本文の「24 件」は 2026-08-31 に自分が数えた値だが、その後 `develop` が 2 本進んでいる**
（#1119 / #1125）。**測り直した。**

### 軸 1 — ファイル名（`.gitkeep`）

```console
$ git ls-files | grep -cE '(^|/)\.gitkeep$'
39
```

### 軸 2 — 名前を変えた同型（`.keep` / `.placeholder` / `.empty` / `PLACEHOLDER`）

```console
$ git ls-files | grep -E '(^|/)\.(keep|placeholder|empty)$|(^|/)PLACEHOLDER'
（0 件）
```

### 軸 3 — 中身（追跡下の 0 バイトファイル）

**#1100 でこの軸だけが `gitkeep.hbs` を出した。今回も引く。**
1 ファイルずつ `test -s` を回すと Windows では 2 分で終わらないので、**空 blob の SHA で引いた**
（`git ls-files -s` の blob が `e69de29bb2d1d6434b8b29ae775ad8c2e48c5391` なら中身が空である）。

```console
$ git ls-files -s | grep -c e69de29bb2d1d6434b8b29ae775ad8c2e48c5391
39
$ git ls-files -s | grep e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 | awk '{print $4}' | grep -v '\.gitkeep$'
（0 件）
```

🔴 **陽性対照つきの 0 件である** —— 同じ検索が `.gitkeep` 39 件を確かに拾っており、軸 1 の 39 と
一致する。**「引っかからなかった」ではなく「空ファイルは `.gitkeep` 以外に無い」と言える。**
`src/plop-templates/feature/gitkeep.hbs` は #1125 で削除済みで、**この軸の穴は塞がっている。**

### 🔴 軸 4 — ディレクトリの中身（本作業の発見はここから出た）

**「`.gitkeep` があること」と「`.gitkeep` が何かを keep していること」は別である。**
39 件それぞれについて、同じディレクトリに追跡下の実体が何件あるかを数えた。

| 実体件数 | 件数 | 意味 |
| ---: | ---: | --- |
| **1 件以上** | **11** | 🔴 **ディレクトリは実体で存在しており、`.gitkeep` は何も keep していない（死んだ枠）** |
| 0 件 | 28 | `.gitkeep` がディレクトリを存在させている |

死んでいた 11 件（括弧内は同ディレクトリの追跡下ファイル数）:
`.ai-context/specs`（**549**）／ `docs/tests`（53）／ `docs/screens`（21）／ `docs/functional`（18）／
`docs/data`（11）／ `docs/how-to`（10）／ `docs/api`（5）／ `docs/tech`（5）／
`docs/observability`（4）／ `docs/authz`（2）／ `docs/migration`（1）

**#1100 の私はこの軸を引かず、`docs/` と `.ai-context/specs/` の 15 件を「出力先だから残す」と
一括で扱った。それは粗すぎた**（§5 で自分の記述を是正する）。

### 軸 5 — 文言（`gitkeep` / 空枠 / 枠のみ / 空フォルダ / 枠置き / 枠だけ）

live な記述で本作業が偽にし得るもの:
`src/platform/frontend/README.md`（「枠のみ（.gitkeep。**消さない**）」）／
`templates/unit-template/README.md`（#1125 で私が「#1122 で判断する」と書いた）／
🔴 **`.ai-context/adr/IADR-0262` 決定 3**（「中身の無い区分もフォルダと `.gitkeep` で枠を残す。
**使わない区分のフォルダを消さない**」）／ `.ai-context/adr/IADR-0321` §影響。
`src/README.md`・`docs/tech/tech-requirements.md` はバックエンドの話で偽にならない。

### 🔴 軸 6 — 規則 10（この変更で新たに誤りになる「自分の記述」）

**#1125 で私が触った記録を引き直した。取りこぼしが 1 件見つかった。**

| 記録 | 状態 |
| --- | --- |
| `IADR-0262` 決定 3 | 🔴 **#1125 の時点で既に偽になっていた。** 同決定は枠の対象に **「各 feature 配下の `hooks/` `stores/`」を明記**しており、**#1125 はそれを撤去したのに同決定へ何も書かなかった。** 本作業で是正する |
| `IADR-0321` §影響「`docs/<種別>/.gitkeep`（14 件）と `.ai-context/specs/.gitkeep` は残す」 | 軸 4 の測定により**粗すぎた**。11 件は死んだ枠である。日付つき追記で是正する |
| `IADR-0321` §影響「ユニット直下の枠 24 件は射程外（#1122 へ分けた）」 | 真のまま |
| `.ai-context/specs/20260831_issue-1100_…` §2 群 E | 同上。`.ai-context/specs/` は経過追記が可（`traceability.repo.md` §Superseded の凍結の射程）なので追記する |

### 母集合と #1122 本文の数えとの差

**ユニット直下は 24 件のままで一致した**（`develop` が 2 本進んだが `.gitkeep` は動いていない）。
**差は「24 件の外」に出た** —— 軸 4 が `docs/` 側の死んだ枠 11 件を新たに出した。

## 2. 対象ごとの判断

### 群 α: ユニット直下 24 件 → **残す。ただし理由を差し替える**

**一律に消さない。消せない理由が 24 件に共通してある。**

🔴 **planning#445（`blocked-impl` の裁定・2026-08-22）は、この 24 件のディレクトリ名を名指しで
「存在しない」と指摘し、「ツリー全体への適合が必須」と裁定した。**

> 2026-08-22 の実測では、`src/platform/frontend/src/` 直下は `App.tsx` / `features` / `foundation` /
> `main.tsx` / `test` だけであり、**上のツリーが列挙する `app/` `assets/` `components/` `hooks/`
> `lib/` `stores/` `types/` `utils/` `locales/` は 1 つも存在しない。**
> …**必須とするのはツリー全体への適合である。名前だけを揃える対応は採らない。**

**いま枠を消すと、planning#445 が非適合と裁定したその状態へ戻る。** feature 内部（#1100 / #1125）は
「6 分割まで含む」という一般則の適用だったのに対し、**こちらは裁定がディレクトリ名を列挙している。
射程が違う。**

**同時に、枠が適合の証拠にならないことも同じ裁定が言っている**（「名前だけを揃える対応は採らない」）
—— 🔴 **空の `assets/` は名前だけを揃えた状態そのものである。planning#445 はどちらの側も支えない。**

**したがって本作業は「消す/消さない」を実装側で決め切らない。**
`IADR-0309` 決定 3 が feature 内部について採ったのと同じ形で、**裁定へ委ねる**
（planning#510 が既に開いている。**新しい issue を立てず、同じ裁定へ論点を足す**）。
**そのかわり、枠が担っていた「適合の見え方」を文書の側から剥がす**（§3）。

24 件それぞれが空である理由（PR 本文に 1 件ずつ載せる）は §2 の表を正とする。

| ユニット | 区分 | なぜ空なのか（実測） |
| --- | --- | --- |
| platform | `assets/` | 自己ホストのフォント・画像が 0 件。**08_data-egress-policy が外部 CDN と Web フォントを禁じた結果、フォントはシステムフォント・アイコンは lucide-react（パッケージ）になっており、置くものが無い** |
| platform | `hooks/` | 横断フックは存在するが**関心の隣に置いてある**（`lib/auth/useAuth.ts` / `components/notifications/useNotifications.ts` / `components/ai-chat/useAiChatStream.ts`）。Bulletproof React の `lib/` は「アプリ向けに設定済みの再利用ライブラリ」であり、認証フックがそこに居るのは逸脱ではない |
| platform | `stores/` | Zustand ストアは **1 本だけ**（`components/ai-chat/aiChatStore.ts`）。**参照元 4 ファイルはすべて同じ `ai-chat/` 配下**で、アプリ横断の状態ではない。`src/stores/` へ出すと唯一の利用者から遠ざかる |
| platform | `types/` | 表示に使う型は**契約（OpenAPI）から生成した DTO**（`lib/api/generated/bff.schemas.ts`。`IADR-0135` 決定 1）。横断の手書き型が 0 件 |
| platform | `utils/` | 純粋関数は存在するが `components/ui/` に居る（`formatDateTime.ts` / `apiErrors.ts`）。**移送はエイリアス `@foundation/ui/*` の面を動かすため本 issue の射程外** → 別 issue（§4） |
| knowledge | `app/` | **アプリシェル・ルータ・プロバイダはアプリホスト（platform）が持つ**。可変ユニットが持たないのは意図的な不在 |
| knowledge | `assets/` | platform と同じ（置くものが無い） |
| knowledge | `hooks/` | 横断フックが 0 件。feature 固有の状態は #1125 の判断どおり各画面に閉じている |
| knowledge | `locales/` | **カタログの実体は platform 側**（`platform/frontend/src/locales/` に 4 件）。lingui の抽出範囲は両ユニット全体だが出力は 1 箇所である |
| knowledge | `stores/` | Zustand ストアが 0 件（唯一の 1 本は platform 側） |
| knowledge | `testing/` | テストハーネスは platform が公開している（`@foundation/testing` → `platform/src/testing/renderUnitRoute.tsx` ほか 3 件） |
| knowledge | `types/` | platform と同じ（生成 DTO を使う） |
| knowledge | `utils/` | 横断ユーティリティが 0 件。ECharts ローダ等は `components/` に居る（描画部品に付随するため） |
| unit-template | 11 区分 | **雛形は「新しいユニットが取るべき形」を見せるものである。** 実体を置けばそれが本物だと誤解され、区分ごと消せば新規ユニットは何も手掛かりが無い。**群 α の裁定が出るまで雛形だけ先に動かさない**（雛形と実装で形が割れる。#1125 が feature 側でわざわざ揃えたばかりである） |

### 群 β: 実体のあるディレクトリの `.gitkeep` 11 件 → 🔴 **撤去する**

**判断に裁定は要らない。** `.ai-context/specs/` には追跡下のファイルが **549 件**あり、
`docs/tests/` には **53 件**ある。**ディレクトリは実体によって存在しており、`.gitkeep` は
何も keep していない。** 消してもディレクトリは消えず、`/new-spec` の出力先も変わらない。

**これは群 α の論点（適合の見え方）とは別の、単なる残骸である。**
`.gitkeep` を置いた当時は空だったものが、実体が入った後も残り続けた。

### 群 γ: 実体の無い `docs/` 種別フォルダ 4 件 → **残す**

`docs/batch` / `docs/errors` / `docs/infra` / `docs/integration`。
**`docs/README.md` が 19 種別の出力先として宣言しているディレクトリ**であり
（`integration` / `batch` / `error` / `infra` の 4 行を実測で確認）、**消すとフォルダごと消える。**
`ADR-0065` 決定 4 が撤回したのは「**構成要素が揃って見える**」枠であって、
**まだ 1 件も書かれていない種別の出力先**ではない —— **誰もこれを数えて適合を主張しない。**

## 3. 文書の是正（受け入れ基準 2）

**枠そのものより、枠を根拠にした記述のほうが有害である。**

| ファイル | いま何が問題か | どう直すか |
| --- | --- | --- |
| `src/platform/frontend/README.md` | 「`src/` は計画のツリーに適合」と書いた直後に「枠のみ（`.gitkeep`。**消さない**）」と並べており、**空の枠を適合の一部として提示している** | 空であることと、**なぜ空か**、**撤去の可否が planning#510 の裁定待ちである**ことを書く。無条件の「消さない」を条件つきに改める |
| `templates/unit-template/README.md` | #1125 で私が「この 24 件の扱いは #1122 で判断する」と書いた | 判断の結果（残す・理由・裁定待ち）へ差し替える |
| `.ai-context/adr/IADR-0262` 決定 3 | 🔴 **根拠が撤回済み**（バックエンドの同型規範）で、**射程に「各 feature 配下の `hooks/` `stores/`」を含むが #1125 で撤去済み** | 日付つき追記で部分改定する。**旧 ID を残し、改定者（新 IADR）を併記する** |
| `.ai-context/adr/IADR-0321` §影響 | `docs/` 15 件を一括で「残す」と書いた（軸 4 を引いていなかった） | 日付つき追記で是正する |
| `.ai-context/specs/20260831_issue-1100_…` §2 群 E | 同上 | `［YYYY-MM-DD 追記 / #NNN］` の経過追記（`.ai-context/specs/` は可） |

## 4. 起票するもの

1. **`utils/` 相当の純粋関数が `components/ui/` に居る**（`formatDateTime.ts` / `apiErrors.ts`）。
   移送は `@foundation/ui/*` というエイリアスの**公開面**を動かす（利用 12 ファイル。submodule は
   `@foundation/ui/*` を使っていないことを実測で確認済み）。**「消す」と「直す」を混ぜない**ため別 issue。→ **#1131 を起票した。**
2. **planning#510 へ論点を足す**（新規 issue を立てない）。feature 内部と同じ裁定で、
   **ユニット直下 24 件についても答えが要る**ことを述べる。あわせて #1125 の改番
   （`IADR-0320` → `IADR-0321`）を訂正する。

## 5. 新しい IADR を書くか、`IADR-0321` へ追記するか

**両方やる。種類が違うためである。**

- **`IADR-0321` への追記**: 同 IADR §影響 の「`docs/` 15 件は残す」は**事実の粗さ**であり、
  決定そのものではない。`traceability.repo.md` §Superseded は「決定を変える追記は日付つき追記
  ブロック」とし、**frontmatter の状態欄は凍結の対象外**（#717 / `IADR-0191` 決定 2）と定める。
  `.ai-context/adr/` は書き換え禁止の列挙（`specs/` / `superpowers/`）に入らない live な権威文書
  である。→ **日付つき追記で是正し、`updated:` を前進させる。**
- **新 IADR（`IADR-0325`）**: 群 α を「消さずに待つ」と決めること、その**トリガー条件**、
  `IADR-0262` 決定 3 の部分改定は、**新しい決定**である。追記では射程が読めない。

## 6. 受け入れ基準（#1122 から写像）

- [x] 24 件を 1 件ずつ確かめ、**実体があるか／区分ごと無いか／「残す理由」が文書化されている**
- [x] 2 つの README の `.gitkeep` 記述が実態と一致し、**撤回済みの根拠を引いていない**
- [x] `pnpm run lint` / `typecheck` / `test` / `build` / `format:check` が通る
- [x] `check-doc-links` / `gen-knowledge-graph --check` ほかが通る

## 7. ［2026-08-31 追記 / #1122］実行結果

### 撤去した 11 件と、残した 28 件

```console
$ git ls-files | grep -cE '(^|/)\.gitkeep$'
28                                   ← 着手時 39。11 件を撤去
```

残 28 件 = ユニット直下 24（群 α。裁定待ちで残す）＋ 実体 0 件の `docs/` 種別フォルダ 4（群 γ）。
**撤去後もディレクトリはすべて存在することを実測した**（`ls -d docs/api docs/tests .ai-context/specs …`）。

### 起票したもの

- **#1131** —— 描画しない純粋関数（`components/ui/apiErrors.ts` / `formatDateTime.ts`）が
  `components/` に居る件。移送は `@foundation/ui/*` の公開面を動かすため分けた。
- **planning#510 へコメント** —— 新規 issue を立てず、同じ裁定へユニット直下の論点を足した。
  あわせて #1125 の改番（`IADR-0320` → `IADR-0321`）を訂正した。

### 通した検査

`pnpm run lint`（error 0 / warning 10・既存）／ `typecheck` ／ `test` ／ `build` ／ `format:check`、
`check-route-manifest` ／ `check-chunk-budget`（床 617.16 kB のまま）／ `check-i18n-catalogs` ／
`check-adr-numbering` ／ `check-commit-messages` ／ `check-trace-blocks` ／ `check-doc-links` ／
`check-doc-updated` ／ `check-doc-type-vocabulary` ／ `check-doc-status-vocabulary` ／
`check-plan-id-qualification` ／ `check-cross-repo-refs` ／ `gen-knowledge-graph --check` ／
`REQUIRE_REPO_TESTS=1 scripts.test.js`（668 件）。**すべて緑。**

`check-adr-numbering` は索引行の追加時に 1 度だけ先に叩いており（`IADR-0325` の追加直後）、
`check-doc-links` は `IADR-0262` / `IADR-0321` から新 IADR へのリンクを検証している
—— **#1125 でリンクの取り残しに 1 度捕まっているため、コミット前に個別に叩いた。**

### 落ちたテスト

`platform/frontend/src/lib/api/orvalMutator.test.ts` の 1 件のみ（Node 24 の環境差）。

```console
$ git diff --name-only origin/develop HEAD -- src/
src/platform/frontend/README.md
```

🔴 **`src/` 配下で `develop` と違うのは Markdown 1 件だけ**であり、当該試験と依存の
全ソースが `origin/develop` とバイト単位で同一である。**基点の内容をそのまま走らせている。**
CI は Node 22 で緑になる。

### 測れなかったもの

- **E2E（Playwright）と バックエンド（`dotnet build` / `dotnet test`）は走らせていない。**
  本 PR の変更は 0 バイトのファイル 11 件の削除と Markdown だけであり、
  **C# と E2E の入力に 1 バイトも触れていない**（上の `git diff` が全量である）。CI に委ねる。
- **planning#510 の裁定そのもの。** 群 α の可否は本 PR では決まらない（決定 1 がそう決めた）。
