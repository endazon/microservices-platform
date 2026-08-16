---
title: 作業仕様書 — 大玉 17 件の着手可否をこの環境で測り直す（前回の棚卸しは別環境の値だった）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0119
  - IADR-0120
  - IADR-0142
  - IADR-0179
  - IADR-0180
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
  - "../../planning/docs/ai-implementation-workflow-guide.md"
related_specs:
  - "../adr/IADR-0180_blocked-judgments-expire.md"
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
  - "../adr/IADR-0142_fr19-20-scoped-release-by-overturn-range.md"
---

# 作業仕様書: 大玉 17 件の着手可否の再検証

## 1. 起点となる ID（トレーサビリティ）

- 起点 ID: **`NFR`**（無採番。工程の統制＝メタ作業であり、計画側の非機能要件表は射程を「稼働する製品」に限る。
  `.claude/rules/traceability.md`「起点 ID の種別」の**場合 2**。**環流しない**）
- 起点 issue: **無し。** 利用者から「表に挙がっていない issue を計画・実施せよ」との指示があり、
  その補集合の大半が**着手できない大玉**だったため、**規約が要求する再検証**を成果物として残す。
- 規約上の根拠: `CLAUDE.md`「**blocked（AI だけでは完結しない）判定は棚卸しごとに再検証する**」／
  [IADR-0180](../adr/IADR-0180_blocked-judgments-expire.md)（**環境依存の判定に前回値を据え置かない**）

## 2. なぜ測り直すのか —— **前回の棚卸しは別環境の値だった**

`docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md`（**確定済み。書き換えない**）は OPEN 38 件を
全数棚卸しし、大玉群を波 5（XL 群）として `blocked` と判定した。

その後 **2026-08-16 に `#442` / `#455` が「3 軸すべて ○」へ更新された**。しかしこれは
**Windows + Rancher Desktop k3s + `dotnet 10.0.301` を持つ別セッションの環境で測った値**である。

**同じ issue が、環境によって着手可にも着手不可にもなる。** これは `IADR-0180` が扱った型そのもので、
**値を据え置くと「着手できるはずなのにできない」「できないはずなのにできた」の両方が起きる。**

## 3. 測定環境と時点（**これが本書の核心**）

**時点**: 2026-08-16 / `origin/develop` = **`90ba652a`** / `planning` pin = **`8cae89d`**

```console
$ for c in dotnet kubectl helm k3d kustomize node pnpm docker; do printf "%-10s " "$c"; command -v "$c" || echo "(NOT FOUND)"; done
dotnet     (NOT FOUND)
kubectl    (NOT FOUND)
helm       (NOT FOUND)
k3d        (NOT FOUND)
kustomize  (NOT FOUND)
node       /opt/node22/bin/node
pnpm       /opt/node22/bin/pnpm
docker     /usr/bin/docker

$ docker info >/dev/null 2>&1 && echo "reachable" || echo "UNREACHABLE"
UNREACHABLE
```

**`docker` はバイナリが在るだけでデーモンが動いていない。** したがって Testcontainers も
`docker compose up` も使えない。**フロントは実走する**（`pnpm run typecheck` が 5 ワークスペースで Done）。

## 4. 再検証の結果 —— **このコンテナでは 17 件すべて着手不可**

判定軸は 4 つ。**「何が無いから不可か」を必ず 1 つ以上挙げる**（挙げられないなら不可ではない）。

| # | issue | 不可の理由 | 種別 |
| --- | --- | --- | --- |
| 1 | **#455** バックエンド層標準 | `dotnet` 不在。**11 件の先行にあたる隘路** | 環境 |
| 2 | #438 認証認可（ABAC） | `dotnet` ＋ Keycloak 稼働。realm 改名は既に着地済みで残りは backend | 環境 |
| 3 | #439 BFF セッション方式 | `dotnet`。先行 #438。**フロントだけ先に切ると認証が 2 系統同居し [[IADR-0121]] 決定 6 の「1 度だけ切替」に反する** | 環境 ＋ 先行 |
| 4 | #440 LLM ゲートウェイ | `dotnet`。加えて **計画 `ADR-0038` が `Proposed`**（着手条件は `Accepted`。[[IADR-0119]]） | 環境 ＋ ADR |
| 5 | #441 メッセージング | `dotnet`。先行 #455 | 環境 ＋ 先行 |
| 6 | **#442** エッジ・実行基盤 | `kubectl` / `helm` / `kustomize` / `k3d` 不在 ＋ **docker デーモン停止**。子 5 件（#779〜#783）も同じ | 環境 |
| 7 | #443 可観測性・運用 | `dotnet` ＋ クラスタ。#455 と #442 の合流点 | 環境 ＋ 先行 |
| 8 | #444 構成変更容易性 | `dotnet`。**SC-11 画面は実装済み**（`sc11-config` 実在）で残りは backend | 環境 |
| 9 | #445 MCP サーバー統合 | `dotnet`。先行 #455。SC-12 画面は本 issue の完了待ち | 環境 ＋ 先行 |
| 10 | #446 SPA 基盤 | **自身は閉じられない**（完了条件が #452 の旧 13 画面削除）。残段の実体は #788 / #493 | 先行 |
| 11 | #447 取り込み・変換 | `dotnet`。先行 #455 | 環境 ＋ 先行 |
| 12 | #448 検索・RAG | `dotnet`。先行 #455 と #440 | 環境 ＋ 先行 |
| 13 | #449 文書管理・Wiki 閲覧 | `dotnet`。**前提検証は決着した**（下記 §5） | 環境 |
| 14 | #450 知識グラフ・AI 提案 | `dotnet`。**保留は解除された**が **`ADR-0039`（SC-18 描画ライブラリ）が `Proposed`** | 環境 ＋ ADR |
| 15 | #451 個人資料・Obsidian 同期 | `dotnet`。**上流に未決着 1 点**（下記 §5） | 環境 ＋ 上流 |
| 16 | #452 全 21 画面 | **SC-01〜11 の 11 画面は実装済み。残 10 画面はすべて契約または backend 待ち**（下記 §6） | 先行 |
| 17 | #453 退行防止テスト基盤 | **基盤はほぼ全部在る。残射程は #466（統合スタック E2E）1 本**で、それが #442 に従属 | 先行 |

**`#454` は親のトラッキングであり、それ自体には作業が無い**（本書の対象外。本文の是正だけ別途行った）。

### 前回の棚卸しとの差分

| issue | 前回（別環境・2026-08-16） | 本書（このコンテナ・2026-08-16） |
| --- | --- | --- |
| **#442** | **3 軸すべて ○**（実クラスタも統合スタックも稼働） | **×**（kubectl / helm / kustomize / k3d 不在・docker デーモン停止） |
| **#455** | **○**（`dotnet 10.0.301`） | **×**（`dotnet` 不在） |

**どちらの値も正しい。違うのは環境である。** これを書き分けないと、次に読む人が
「着手できると書いてあるのにできない」という再現しない記録を掴む。

## 5. 判断が変わった 4 件（**前回と結論が違う**）

| issue | 何が変わったか |
| --- | --- |
| **#449** | **前提検証が決着した。** #602 で実施され、計画 **`ADR-0046` が `Accepted`**（個人資料は Wiki.js へ同期せず、本文編集は Obsidian 経路に限る）。**表題から「前提検証を含む」を外した。** 検証の結論は「閲覧は成立、編集は成立しない」 |
| **#450** | **着手保留は解除された。** `ADR-0033` / `0034` / `0035` / `0036` / `0037` が**全件 `Accepted`**（[[IADR-0119]] の 2026-08-07 追記「これにより FR-17 / FR-18 の保留を解除した」）。**ただし `ADR-0039`（SC-18 の描画ライブラリ）は `Proposed`** で別軸が残る |
| **#451** | **着手保留は部分解除。** 2026-08-07 のコメントが「SC-19 の『本文を編集』導線ただ 1 つを除いて着手可能」と明記。`ADR-0046` がその導線を Obsidian 経路へ確定させた。**ただし [[IADR-0119]] の 2026-08-15 追補（保留継続）と計画 `ADR-0037` の注記（留保は外れた）が同日で逆を向いており、上流に未決着が 1 点ある** |
| **#442** | **3 軸 ○ は別環境の値。** このコンテナでは × に戻る（§4 の 6 行目） |

**計画 ADR の実測**（走査基準 planning `8cae89d`）:

```console
$ grep '^- 状態:' planning/projects/microservices-platform/07_adr/ADR-00{33,34,35,36,37,38,39,46}_*.md
ADR-0033 Accepted   ADR-0034 Accepted   ADR-0035 Accepted   ADR-0036 Accepted
ADR-0037 Accepted   ADR-0046 Accepted
ADR-0038 Proposed   ADR-0039 Proposed
```

## 6. `#452` の残 10 画面が着手できない理由（画面ごと）

**SC-01〜SC-11 の 11 画面は実在する**（`src/knowledge/frontend/src/features/` を実測）。

| 画面 | 塞いでいるもの |
| --- | --- |
| SC-12 | FR-16 = **#445**（backend） |
| SC-13〜17 | Keycloak テーマ = **#438**（`loginTheme` / `accountTheme` 未設定・テーマ実体 0 件） |
| SC-18・SC-21 | 保留は解除済みだが **`ADR-0039` が `Proposed`** ＋ GraphService の契約が無い |
| SC-19・SC-20 | backend ＋ **`IADR-0119` と計画 `ADR-0037` の同日矛盾**（§5） |

**したがって #452 の薄いスライスは「画面を足すこと」ではない。** `src/knowledge/frontend/**` 単独で
完結する現存作業は **#785**（Bulletproof React の feature 内部分割）だが、**これは本書の射程外**である
（利用者が別セッション向けとした表に含まれる）。

## 7. 隘路は 2 つに集約される

```
#455（backend 層標準・dotnet 必須）
  └─ 先行として塞いでいるもの: #438 #440 #441 #443 #444 #445 #447 #448 #449 #450 #451  ＝ 11 件

#442（エッジ・実行基盤・クラスタ必須）
  └─ 先行として塞いでいるもの: #443 #466（→ #453）  ＋ 子 5 件（#779〜#783）
```

**17 件のうち 13 件が、この 2 つのどちらかに従属する。** 残る 4 件（#446 / #452 / #453 / #442 自身）も
自前の先行を持つ。**個別に崩す方法は無く、`dotnet` が使える環境とクラスタが要る。**

## 8. やらなかったことと理由

| やらなかったこと | 理由 |
| --- | --- |
| `templates/unit-template/backend` の `Tests` 2 → 1 プロジェクト化（#455 のコメント D が「独立して直せる」と提案） | `.csproj` の編集自体は dotnet 不要だが、**`dotnet build` / `dotnet test` が無く統合後にビルドが通るか確かめられない**。`CLAUDE.md` は `/verify` を PR 前必須としている。**#455 の本文へ理由つきで記録した** |
| 17 件それぞれへ個別コメント | 利用者の決定（集約 1 本 ＋ 判断が変わった分だけ個別）。GitHub のノイズを抑えつつ追跡可能にする |
| 確定済み棚卸し仕様書（`20260815_issue-454_*`）の書き換え | `status: done` の `docs/specs/` は書き換えない（`.claude/rules/traceability.repo.md` §Superseded の書式） |
| `#454` 本文のチェックボックスの機械的追随 | [[IADR-0120]]: PR 番号・open/closed・CI 状態は GitHub が正で、書けば二重管理になる。**直したのは誤った判断の記述だけ** |

## 9. 母集合

**走査基準**: `origin/develop` = `90ba652a` / `planning` = `8cae89d`。

| 軸 | 走査 | 件数 |
| ---: | --- | ---: |
| 1 | open issue の全数（GitHub API） | 41 |
| 2 | うち利用者の表に挙がったもの | 21 |
| 3 | **補集合**（本書の射程） | **20** |
| 4 | うち大玉（#454 の子群） | 18 |
| 5 | 本書が再検証した件数（#454 を除く） | **17** |

**除外と理由**:

| 除外 | 理由 |
| --- | --- |
| `#454` | 親のトラッキングで**それ自体に作業が無い**。本文の是正のみ別途実施 |
| `#600` | 補集合に含まれるが**着手できる薄いスライスが有る**（別途 `#600` 側で実装する）。本書は「着手不可」の記録なので対象外 |
| `#801` | 補集合に含まれるが**実装は完了しており**、残るのは CI 観測のみ。再オープンのまま待つ |
| 表の 21 件 | 利用者が別セッション向け・裁定待ちとした |

## 10. 検証

| 検査 | 結果 |
| --- | --- |
| `check-doc-links` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` | PR 本文に記録 |
| `check-cross-repo-refs` / `check-plan-id-qualification` | 同上（`planning#NNN` の修飾を含む） |
| `check-adr-numbering` / `check-reading-budget` / `check-kit-sync` | 同上 |
| `check-doc-updated` / `check-commit-messages` | **コミット後**に実行（HEAD を読むため。[[IADR-0183]]） |

**必読規約は 1 バイトも触っていない。** コードも触っていない（本書 1 ファイルのみ）。

## 11. ★ 同じコミット・同じ日に、別コンテナでは揃っていた（**本書の主張の実例**）

**本書のレビューが、本書の前提をその場で反証した。** これは本書を誤りにするものではなく、
**[[IADR-0180]] が言う「環境依存の判定に賞味期限がある」ことの、最も強い実例**である。

`origin/develop` = `90ba652a`・`planning` = `8cae89d`・**同じ 2026-08-16**。それでも:

| ツール | 本書の実装コンテナ | **AI レビューの実行コンテナ** |
| --- | --- | --- |
| `dotnet` | **不在** | **在り**（`10.0.400` / `/usr/share/dotnet/dotnet`。`dotnet --info` と restore 成功まで確認） |
| `kubectl` | 不在 | **在り**（`/usr/bin/kubectl`） |
| `helm` | 不在 | **在り**（`/usr/local/bin/helm`） |
| `kustomize` | 不在 | **在り**（`/usr/local/bin/kustomize`） |
| `k3d` | 不在 | 不在 |
| `pnpm` | **在り** | **不在** |

**`pnpm` は逆向きである** —— 一方にしか無いのは片側だけではない。

### 本書の測定は正しい（`command -v` の穴を潰して再確認した）

レビューが `/usr/share/dotnet/dotnet` という**PATH 外の絶対パス**で見つけたため、
**`command -v` が PATH しか見ないことによる見落としを疑って引き直した。**

```console
$ for p in /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/local/bin/dotnet /opt/dotnet/dotnet \
           /usr/bin/kubectl /usr/local/bin/helm /usr/local/bin/kustomize; do
    printf "%-32s " "$p"; [ -x "$p" ] && echo "EXISTS+EXEC" || echo "-"; done
/usr/share/dotnet/dotnet         -
/usr/lib/dotnet/dotnet           -
/usr/local/bin/dotnet            -
/opt/dotnet/dotnet               -
/usr/bin/kubectl                 -
/usr/local/bin/helm              -
/usr/local/bin/kustomize         -

$ ls -d /usr/share/dotnet /usr/lib/dotnet /opt/dotnet 2>/dev/null || echo "(no dotnet dirs)"
(no dotnet dirs)

$ find / -maxdepth 4 -name "dotnet" -type f 2>/dev/null | head -5
（0 件）
```

**このコンテナには本当に無い。** 両方の測定が正しく、**違うのはジョブである。**

### 同じことが検査器の挙動にも起きた（2 例目）

レビューは `check-doc-updated` について、**自分の環境では skip した**と報告した。

```console
（レビュー実行コンテナ = shallow clone）
[check-doc-updated] skip: origin/develop との merge-base を引けませんでした（shallow clone か base 未取得）
EXIT=0

（本書の実装コンテナ = full clone）
[check-doc-updated] OK: 変更された docs/ の Markdown 1 件に updated: の据え置きはありません。
EXIT=0
```

**終了コードは同じ 0 で、意味は違う。** 検査器は skip をちゃんと出しているので**検査器の欠陥ではない** ——
**`EXIT=0` だけを転記した報告の側が、`pass` と `skip` を潰していた。**

これは「ツールの有無」とは別の面だが、**同じ「判定はジョブに紐づく」型**である。
**検査器の終了コードではなく、検査器が出した判定の行を読むこと。**

### したがって判定の単位は「リポジトリ」でも「日付」でもなく **ジョブ**である

- **`command -v` だけで「無い」と断じない。** PATH 外に置かれている構成があり得る（本節がその発見の経緯）
- **CI の `build-and-test` は現に両ユニットの backend をビルドしている。** 「このリポジトリはビルドできない」ではない
- **#455 / #442 は、レビュー実行コンテナ相当のツールチェーンを持つジョブなら現時点でも崩せる可能性がある。**
  隘路は**リポジトリの性質ではなく、割り当てられたジョブの性質**である

## 12. 次にこの判定を使う人へ

**本書の判定は 2026-08-16 の「実装セッションのコンテナ」の値である。据え置かないこと**（[[IADR-0180]]）。

**着手するときは、まず §3 と §11 の両方のコマンドで自分のジョブを測り直し、本書との差分を書くこと。**
`dotnet` とクラスタが揃うジョブでは、**#455 と #442 が先に崩せる**。そこが崩れれば 13 件の従属が解ける。
