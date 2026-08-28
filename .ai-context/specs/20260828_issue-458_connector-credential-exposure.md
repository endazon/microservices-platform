---
title: コネクタ資格情報の未封鎖な露出経路 4 本を塞ぎ、陳腐化した追跡先を是正する
type: spec
status: done
related_ids:
  - FR-01
  - FR-05
  - UC-04
  - SC-06
  - NFR
  - ADR-0005
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/07_adr/ADR-0005_security.md
---

# 仕様書: コネクタ資格情報の露出経路の封鎖（#458）

起票は #458（セキュリティ暫定運用の解消。横断トラッカ）。実装 ADR は IADR-0295。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（データソース登録・同期・カタログ化）、FR-05（ABAC 文書属性）
- ユースケース（UC）: UC-04（データソース同期。例外フロー＝失敗の記録・アラート）
- 画面（SC）: SC-06（データソース管理。応答が画面へそのまま出る）
- 非機能要件（NFR）: セキュリティ（秘密情報管理）。**製品側の採番 NFR には当たらない** ——
  本作業は既存機能の露出面を塞ぐ是正であり、新しい要件を実装するものではない
  （`.claude/rules/traceability.repo.md`「`NFR` の採番」の無採番許容）。
- 関連 ADR（計画）: ADR-0005（セキュリティ）。`06_technical/09_datasource-connectors.md`
  §認証・秘匿情報（「コミット・ログへ出力しない」）と §初期対応の優先順位の表（SaaS の認証は
  「OAuth／APIキー」）が本作業の直接の根拠である。

## 目的・背景

外部コネクタの資格情報は `DataSourceService` の **1 テーブル 2 列**に平文で保存されている ——
`DataSources.Config`（jsonb。`apiToken` / `password` 等）と `DataSources.ConnectionUri`（varchar(2048)）。

**保存の暗号化は本作業では行わない**（理由は IADR-0295 決定 5）。本作業が閉じるのは
**保存済みの平文が外へ出る経路**である。既にマスクは 2 層あるが（応答の `SecretConfigMask.Redact`、
保存時の `SyncErrorRedactor`）、**4 経路が素通しである**。

| 経路 | 実体 | 何が出るか |
| --- | --- | --- |
| (a) | `DataSourceSyncService.cs:86` が `"discover failed: " + ex.Message` を **`SyncErrorRedactor` を通さずに**応答へ載せる。`DataSourceEndpoints.cs:80` → `DataSourceBffEndpoints.cs:92-93` が中継 | `DatabaseConnector.cs:114` が `builder["Password"]` で接続文字列を合成して `OpenAsync` するため、Npgsql の接続失敗例外に**パスワードが載る** |
| (b) | `DataSourceEndpoints.cs:149` が `ConnectionUri` を素で返す。`SecretConfigMask` は `Config` にしか掛からない | `scheme://user:pass@host` 形式・`Host=..;Password=..` 形式。`SyncErrorRedactor.cs:42-43` が同形式を明示的に伏せている＝**コード自身が入り得ることを認めている** |
| (c) | マーカー集合が 2 箇所にあり食い違う。`SecretConfigMask.cs:20-21` は 4 語、`SyncErrorRedactor.cs:27` は 7 種 | `Config` のキーが `apiKey` / `pwd` / `privateKey` だと `Redact` が**マスクしない**。計画が名指しする「APIキー」が現行マーカーで捕まらない |
| (d) | `DataSourceSyncService.cs:143, 167` の `logger.LogWarning(ex, ...)`。`ex` を第 1 引数に渡すと `Exception.ToString()`（メッセージ＋内部例外＋スタック）がログレコードに入る | 共通ログ基盤にスクラビングが**無い**（後述の実測）。コネクタのコメントは「ログ出力しない」と主張するが**例外経由の間接出力**が塞がれていない |

## 対象範囲

- **対象**: `src/knowledge/backend/Services/DataSourceService/` 配下の上記 4 経路と、その回帰テスト。
  および `#310` を「一元追跡」として指す陳腐化した記述の是正。
- **対象外**:
  - **保存の暗号化（列暗号化・封筒暗号化）**。理由は IADR-0295 決定 5。
  - **Vault / ESO への実移行**。go-live 条件であり実クラスタを要する（#458 が `blocked` である理由）。
  - `PostgresAdvisoryLockLeaseCoordinator.cs:44,64` と `DataSourceSyncHostedService.cs:40` の
    `LogWarning(ex, ...)` / `LogError(ex, ...)`。**同型だが対象が違う** —— これらが運ぶのは
    サービス自身の DB 接続文字列（構成注入）であって**コネクタ資格情報ではない**。
    射程を広げると本 PR が「例外ログの全面改修」になる。**発見として報告し、別 issue に委ねる。**
  - `FileSystemConnector.cs:56,80` の `LogWarning(ex, ...)`。運ぶのはローカルパスと IO エラーで、
    `FileSystemConnector` は資格情報を一切読まない（`rootPath` / `ConnectionUri` のみ）。
  - フロントエンド。SC-06 の登録フォームは**新規登録のみ**を配線しており（`DataSourceForm.tsx` に
    PUT/PATCH の口が無い）、応答の `connectionUri` を編集して送り返す経路が画面側に存在しない。

## 母集合の引き方と、引いた結果（`#310` の是正）

> `.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則 9 に従い、
> **他人の数えを検証せず転記していない。** 指示文は 7 箇所を挙げたが、**自分で引き直した。**

### 引き方（実行したコマンドそのもの）

```
git grep -n -I "#310" -- . ':!src/ai-stock-trading'
```

- **拡張子で絞っていない。** `-- '*.md'` で絞ると **26 ファイル**しか出ず、`.sh` / `.yaml` / `.hcl` /
  `.js` にある **19 ファイル**を落とす（実測）。
- `src/ai-stock-trading` は別プロジェクトの submodule のため除外した（走査対象外）。
- `-I` でバイナリを除外した。

### 引いた結果

**98 ヒット / 45 ファイル。** 意味で 2 群に割れる。

| 群 | 内容 | 件数（ファイル） | 処置 |
| --- | --- | --- | --- |
| **A. 現在形の追跡先の主張** | 「**一元追跡: #310**」「#310 に集約して追跡する」「#310 で一元追跡する」「既存トラッカ（#310）配下」 | 8 ファイル | 下表のとおり個別に判断 |
| **B. 当時の作業の出所（史実）** | 「#310（Vault/ESO 本番同等化）の PR-1〜4」等。IADR-0096〜0099 / 同名の作業仕様書 / `deploy/local/vault/**` / `scripts/k8s-local-up.{sh,test.js}` / `CHANGELOG.md` / `.ai-context/adr/README.md` の索引行 / planning pin の仕様書 | 37 ファイル | **触らない。** issue が後に close されても「その作業は #310 の下で行われた」という史実は変わらない。書き換えると出所が消える |

**A 群の内訳（8 ファイル・10 箇所）と処置:**

| # | 箇所 | 主張 | 処置 |
| --- | --- | --- | --- |
| 1 | `.ai-context/adr/IADR-0051` L101 | コネクタ資格情報の一元追跡 | **日付つき追記ブロック**（後述） |
| 2 | `.ai-context/adr/IADR-0053` L83 | 同上 | 同上 |
| 3 | `.ai-context/adr/IADR-0054` L81 | 同上 | 同上 |
| 4 | `.ai-context/adr/IADR-0055` L90 | 同上 | 同上 |
| 5 | `docs/functional/FR-01_data-source-catalog.md` L96 | 同上 | **本文を是正**（live な権威文書） |
| 6 | `docs/security/security.md` L202 | 同上（「#310 に集約」） | 同上 |
| 7 | `docs/security/security.md` L249 | 同上（未決事項） | 同上 |
| 8 | `docs/security/security.md` L14 | trace ブロックの `issues:` | **`#458` を追加**（`#310` は関連 issue として残す） |
| 9 | `.ai-context/adr/IADR-0079` L66 | **基盤 secret**（Keycloak DB 資格情報）の本番 Vault 移行が #310 配下 | **触らない。**主題が違い、かつ**その作業は #310 配下で実際に一巡している**（IADR-0099「これで #310 の secret 移行は一巡（PR-1〜4）」）。主張は史実として正しく、陳腐化しているのは issue の open/closed だけである |
| 10 | `deploy/helm/microservices-platform/values.yaml` L392 | **外部 LLM API キー**の Secret 供給が「docs/security・#310 の一元追跡」 | **触らない。**理由は #9 と同じ（ESO PR-1 が `llm-provider-credentials` を実配線済み）。**発見として報告する** |

> **指示文の 7 箇所との差**: 指示文が挙げなかった **`docs/security/security.md` L14（trace ブロック）**が
> A 群に含まれる。また指示文の行番号（194 / 241 / 187）は**実体と 8 行ずれていた**
> （実測は 202 / 249 / 194）。**行番号引用は腐る** —— 本作業では行番号での引用を節名へ置き換える。

### 凍結記録（`.ai-context/adr/`）への追随のしかたと、その判断

`.claude/rules/traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」と
「凍結の射程」を読んだうえで、**#1〜4 は本文を書き換えず、日付つき追記ブロックを足す**と判断した。

**理由:**

1. 規約は **適用先を「live な権威文書とコード（`.ai-context/adr/` に限らない）」**とし、
   **書き換えないものとして名指すのは `.ai-context/specs/` と `.ai-context/superpowers/` だけ**である。
   `.ai-context/adr/` は「後から注記を足してはならない」対象**ではない**。
2. 同規約は「**注記そのものへ起票 ID を書き**、`updated:` を前進させる（決定を変える追記は
   日付つき追記ブロック `［YYYY-MM-DD 追記 / #NNN］`）」と、**追記の書式まで定めている**。
3. **先例が支配的である。** `.ai-context/adr/` 配下で `［YYYY-MM-DD 追記 / #NNN］` 書式の
   ブロックを持つファイルは **127 件**（実測）。例外的な運用ではない。
4. **本文の書き換えは採らない。** 当時「#310 で追跡する」と書いたのは史実であり、
   消すと「なぜ追跡先が変わったのか」を後から追えない（IADR-0166 決定 2 の「既存本文の
   書き換え・削除は不可。訂正が要るなら追記で行う」と同じ線）。

**書く内容**: 旧 ID（#310）を残し、close された事実・日付・理由（`duplicate`）と、後継
（#447 が吸収・横断は #458）を併記する。`updated:` を 2026-08-28 へ前進させる。

## 設計

### (c) マーカー集合の統合 —— **先にこれを行う**（(b) が依存する）

**新設 `Domain/SecretMask.cs` に「秘密キーのマーカー」と「自由文のマスク」を集約する。**

- `SecretMask.KeyMarkers`（`const string`。正規表現の選択肢）が**唯一の情報源**である。
  値は現行 2 集合の**和**に、実測で判明した欠落を足したもの:
  `password|pwd|token|secret|api[-_]?key|private[-_]?key|credential|authorization`
  - `api[-_]?key`: **計画が名指しする形式**（09_datasource-connectors の表「OAuth／APIキー」）。
  - `private[-_]?key`: 秘密鍵。どちらの現行集合でも捕まらなかった。
  - `pwd` / `authorization`: `SyncErrorRedactor` 側にだけあった。
- **`key` 単独はマーカーにしない。** `spaceKey`（Confluence の空間キー）を誤マスクする ——
  現行コメントが「`spaceKey` / `listPath` / `rootPath` 等は誤マスクしない」と明示している。
- 向きは **1 方向**にする: `SecretConfigMask.IsSecretKey` と `SyncErrorRedactor` の正規表現が
  **ともに `SecretMask.KeyMarkers` を読む**。`[GeneratedRegex]` は定数式を要求するため、
  **定数補間文字列**（C# 10 以降）で組み立てる。
- `SyncErrorRedactor` の公開面（`Redact` / `MaxLength`）は変えない。中身を
  `SecretMask.RedactText`（**切り詰めなし**）＋ 500 文字への切り詰めに分解する。
  切り詰めは**マスクの後**という現行の順序を保つ（既存テストが固定している）。

### (a) 手動同期 API の応答

`Message: "discover failed: " + ex.Message` を
`Message: SyncErrorRedactor.Redact("discover failed: " + ex.Message)` にする。
**文字列を組み立ててから通す** —— そうすれば保存される `LastSyncError` と応答が同じ規則
（マスク＋500 文字上限）で揃う。

**`fetch` 側に同型は無い**（実測）: `RunAsync` は fetch 失敗時に `Message: null` を返し、
`AlertOnFailure(source, "N/M 件の取得に失敗", null)` は `ex` を渡さない。
すなわち **fetch の例外メッセージは応答に載らない。** 載るのは**ログだけ**であり、それが (d) である。

### (b) `ConnectionUri` のマスクと、書き込み時の扱い

**読み（応答）**: `ToResponse` で `SecretMask.RedactText(ds.ConnectionUri)` を通す。
1 つの規則で両形式を伏せられる —— `scheme://user:pass@host` は URI 規則、
`Host=..;Password=..` は キー=値 規則が捕まえる（`DatabaseConnector` は `ConnectionUri` を
ADO.NET 接続文字列の土台として使う。`DbConnectionStringBuilder { ConnectionString = baseConn }`）。

**書き（検証）**: **資格情報つきの `ConnectionUri` は 400 で弾く。**

「弾く」か「警告する」かは**影響を実測してから**決めた。

- **実測**: リポジトリ内の `connectionUri` の値は **14 種すべてが資格情報を持たない**
  （`smb://share/docs` / `https://wiki.example.com` / `file://x` 等。テスト・フロントの faker・
  統合テストを横断して実測）。**弾いても壊れる既存データ・既存テストは 1 件も無い。**
- **契約が既にそう定めている**: `DatabaseConnector.cs:18` は「`ConnectionUri`（パスワードを含めない）
  ＋ `Config["password"]`」と書いている。**欠けていたのは強制だけ**である。
- **警告を採らない理由**: 警告は「平文が DB に入る」ことを**止めない**。しかも警告の出口はログであり、
  それは (d) で塞ごうとしている経路そのものである。**塞ぐ先へ警告を流すのは筋が通らない。**
- **`filesystem` の逃げ道が塞がる件**: `FileSystemConnector` は資格情報を読まない
  （`rootPath` / `ConnectionUri` のみ）。共有のマウント資格情報は PVC 側の関心事であり
  （FR-01 の follow-up「SMB/NFS マウント手順（PVC）」）、アプリの列に置くものではない。

**判定規則は 1 本にする**: 「**マスクを掛けて値が変わるなら、それは資格情報を運んでいる**」。
第 2 のマーカー集合を作らない（それこそが (c) の defect である）。

**読んで書き戻す往復を壊さないための 2 段**（IADR-0148 決定 6 が `Config` について確立した形を
`ConnectionUri` へ広げる）:

1. **無変更の書き戻しは受理し、既存値を保つ。** 送られた値が「既存値をマスクしたもの」と
   一致するなら、それは GET の結果をそのまま返しただけである → 検証を通し、
   `Update` / `Patch` は**既存の実値を保つ**。
2. **マスク値を編集して送り返したら 400 で弾く。** `postgresql://***@new-host/db` は
   マスク規則に掛からない（`***` に `:` が無いため URI 規則が当たらない）ので、
   1 も 3 もすり抜けて**そのまま保存され資格情報が黙って消える**。
   `***` を含む値は明示的に弾き、「資格情報は `config` へ移せ」と案内する。

### (d) 例外をそのままログへ渡すのをやめる

`logger.LogWarning(ex, ...)` を、**例外の型名 ＋ マスク済みメッセージ**を渡す形にする。

```
logger.LogWarning("… : {ErrorType}: {Error}", …, ex.GetType().FullName, SyncErrorRedactor.Redact(ex.Message));
```

- **スタックトレースは落とす。** `ex` を第 1 引数に渡すと `ILogger` は `Exception.ToString()` を
  LogRecord へ載せる。これは**メッセージ＋内部例外のメッセージ＋スタック**であり、
  Npgsql のように内部例外が接続文字列を運ぶ実装では**内部例外側から漏れる**。
  スタックだけを安全に残す方法は無い（`ex.ToString()` を丸ごとマスクに通す手はあるが、
  上限が無い文字列を可観測性基盤へ流すことになる。**別の判断が要るので本作業ではやらない**）。
- **例外の型名は残す。** 型名は資格情報を運ばず、切り分けの主要な手掛かりである。
- **共通ログ基盤にスクラビングは無い**（再実測: `Foundation/` を
  `redact|scrub|sanitiz|mask` で走査して **0 件**）。すなわち渡したものはそのまま出る。

## 受け入れ基準

- [x] (a) `POST /datasources/{id}/sync` の応答 `message` が、discover 例外に含まれるパスワードを含まない
- [x] (a) 同じ値が `LastSyncError`（保存側）にも含まれない（既存の守りが退行していない）
- [x] (b) `GET /datasources/{id}` と一覧の `connectionUri` が、資格情報つき URI・接続文字列の秘密を伏せる
- [x] (b) 資格情報つき `connectionUri` での登録・更新が 400 で拒否される
- [x] (b) マスク済み `connectionUri` をそのまま書き戻しても、保存された実値が壊れない
- [x] (b) マスク済み `connectionUri` を**編集して**送り返すと 400 で拒否される（黙って壊さない）
- [x] (c) `Config` のキーが `apiKey` / `pwd` / `privateKey` でも応答でマスクされる
- [x] (c) 非秘密キー（`spaceKey` / `listPath` / `rootPath`）は誤マスクされない
- [x] (d) fetch 失敗時のログに例外メッセージ由来の平文パスワードが載らない
- [x] (d) discover 失敗時のログについて同上
- [x] 是正: `#310` を現在形の追跡先として指す A 群 8 箇所が是正されている（凍結記録は追記で）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る
- [x] `scripts.test.js`（`REQUIRE_REPO_TESTS=1`）・trace ブロック・doc links・ADR 採番・
      doc updated・knowledge graph の各検査が通る

## テスト方針

**「実データで緑」は検出力の証拠にならない。変異試験で検出力を示す。**

4 経路それぞれに**秘密を実際に通す陽性対照**を置き、**マスクを外す変異でそのテストが落ちること**を
実測する。テストは既存の `Tests/DataSourceSecretRedactionTests.cs` の隣に置き、既存の作法
（xUnit・`TestContext.Current.CancellationToken`・`AwesomeAssertions`）に従う。

| 経路 | テスト | 通す秘密 | 変異 |
| --- | --- | --- | --- |
| (a) | 応答 `message` の陽性対照 | 例外メッセージ内の `Password=…` | `Redact` 呼び出しを外す |
| (b) 読 | 応答 `connectionUri` の陽性対照 | `postgresql://svc:…@host/db` | `RedactText` 呼び出しを外す |
| (b) 書 | 400 拒否・往復保護・編集検出 | 同上 | 検証呼び出しを外す／保護を外す |
| (c) | `apiKey` / `pwd` / `privateKey` の陽性対照 | 各キーの値 | マーカーを旧 4 語へ戻す |
| (d) | ログ捕捉の陽性対照 | 例外メッセージ内の `Password=…` | `LogWarning(ex, …)` へ戻す |

(d) は `ILoggerProvider` を差し込んで LogRecord を捕捉し、**例外オブジェクトが載っていないこと**と
**整形済みメッセージに平文が無いこと**の両方を見る（片方だけだと `ex` 経由の間接出力を見逃す）。

## 計画書との差異

- 差異: **あり（ただし環流不要）**。計画 `09_datasource-connectors.md` §認証・秘匿情報は
  「すべての接続情報は **HashiCorp Vault** で集中管理し、コネクタは実行時に取得する」と定めるが、
  実装は `Config` / `ConnectionUri` からの直接取得である。**これは既知の暫定状態**であり、
  #458（横断・`blocked`。実クラスタが要る）が既に持っている。**新たな環流は起票しない**
  （`CLAUDE.md`「起票前に同件の既存 issue を必ず検索する」）。
- 本作業はその暫定状態の**残余リスクを減らす**ものであって、計画の To-Be を変えない。

## 未決事項

- **`ex.ToString()` を丸ごとマスクに通してスタックを残す**案の是非（上限をどう置くか）。
  本作業では採らず、必要になった時点で別途判断する。
- **サービス自身の DB 接続文字列**を運ぶ例外ログ（`PostgresAdvisoryLockLeaseCoordinator` /
  `DataSourceSyncHostedService`）。同型だが対象が違うため射程外。**報告に留める。**
- **保存の暗号化**。IADR-0295 決定 5 が「今やらない理由」と「やるときの設計」を残す。
