---
title: アカウントを無効化した利用者の同期トークンを、次の同期要求から 401 で拒否する（#1532）
type: spec
status: completed
related_ids: [FR-20, SC-17, SC-20, UC-11, NFR-14, ADR-0114, ADR-0096, ADR-0037, ADR-0026, ADR-0032, ADR-0029, ADR-0075, IADR-0270, IADR-0428, IADR-0431, IADR-0401, IADR-0379, IADR-0474]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0114_sync-token-rejected-after-account-disable.md
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 仕様書: 無効化した利用者の同期トークンを次の同期要求から拒否する（#1532）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（Obsidian 同期）／非機能 NFR-14（アカウント無効化時の全セッション即時失効）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-17（無効化・再有効化）、SC-20（同期端末）
- 関連 ADR: **ADR-0114**（Accepted・2026-09-26。本作業の直接の裁定）、ADR-0096（フォローアップ 2）、
  ADR-0037（フォローアップ 3）、ADR-0026 / ADR-0032（即時失効）、ADR-0029 / ADR-0075（east-west gRPC）
- 関連 IADR: IADR-0270 決定 3（同期トークンの検証）、IADR-0428（無効化と保持起点）、
  IADR-0431（名簿の `enabled` を DocumentService が読む口）、IADR-0401 決定 2（名簿の狭い読み口）
- 計画書リンク: planning `04d873e`（環流 planning#662 の裁定）

## 目的・背景

SC-17 で無効化しても、同期トークンは期限（30 日）まで `/private-notes/sync/*` を通り続ける（#1532 の実測）。
ADR-0114 は次を定めた。

- 決定 1: 無効化の後の**最初の**同期要求から 401。期限切れを待つ形は「即時」にあたらない。
- 決定 2: 有効・無効を判定できない（名簿を読めない・時間切れ等）なら通さない（fail-closed）。
- 決定 3: 方式は IADR で閉じてよい。IADR には「再有効化で端末が使えるようになるか」と「依存の向き」を書く。
- 決定 4: go-live の前提条件。

## 母集合（着手時に自分で引いた結果と除外）

[[IADR-0141]] 決定 1 に従い、issue 本文の「宣言ファイル領域」を転記せず引き直した。

### 軸 1: 同期トークンを検証している箇所（誤りの側＝「利用者の状態を見ない検証」）

```console
$ git grep -n "HashOf\|TokenHash" -- src ':!src/ai-stock-trading' | grep -v Migrations/
```

- 検証は `ObsidianSyncEndpoints.ResolveDeviceAsync` の **1 か所だけ**（`FirstOrDefaultAsync(d => d.TokenHash == hash)`）。
  他のヒットは発行・再発行・写像の除外・DB 定義であり、検証ではない。
- 呼び出し元は 5 端点（Manifest / Push / Pull / Delete / Move）。`git grep -n ResolveDeviceAsync` で全数。

### 軸 2: 名簿の `enabled` を読める口（配備に配線されているか）

```console
$ grep -rn "AuthorizationServiceGrpc" deploy --include=*.yaml --include=*.yml
```

- 🔴 **document-service には helm（`services.document.extraEnv`）にも compose にも `Services__AuthorizationServiceGrpc` が無い。**
  ヒットは datasource / retrieval / aianalysis / wiki / graph / mcp（helm）と、compose の同じ 6 サービスだけ。
  → IADR-0431 の退職者削除の口も、配備では縮退（常に「引けなかった」）のまま動いていた。
  **fail-closed の門を入れると、配線しない限り全配備で同期が 401 になる。** よって配線を同じ PR に含める。
- document-service の s2s 資格情報（realm の `document-service` client・`platform-service` ロール）と Secret は
  #1255（IADR-0419）で既に配線済み（`serviceToken:` / `ServiceToken__*`）。追加は不要。
- NetworkPolicy は同 Namespace の Pod 間 ingress を許している（`templates/networkpolicy.yaml`）。追加は不要。

### 軸 3: 「無効化しても同期トークンは失効しない／未解決」と書いている文書（誤りの側の文字列）

```console
$ git grep -n "同期トークン" -- ':!src/ai-stock-trading' ':!.ai-context' ':!CHANGELOG.md' ':!**/locales/**' \
    | grep "無効化\|退職\|失効しない\|未解決\|未達\|NFR-14\|フォローアップ"
$ git grep -n "#1532"
$ git grep -n "即時失効\|全セッション\|NFR-14" -- docs src/knowledge src/platform/backend deploy
```

反映先（live な文書）:

| 文書 | 直す理由 |
| --- | --- |
| `docs/security/security.md` §退職 | 「同期トークンが無効化で失効するかは未解決」 |
| `docs/functional/FR-19_private-notes.md` 冒頭 | 「入っていないのは無効化時の同期トークン失効」 |
| `docs/functional/FR-20_obsidian-sync.md` §認証・エラー | 401 の理由に「所有者の無効化・判定不能」が無い |
| `docs/api/FR-20_obsidian-sync.md` | 同上（検証失敗の列挙） |
| `docs/tests/FR-20_obsidian-sync.md` | 新しい試験の写像 |
| `docs/screens/SC-17_user-account-management.md` | 無効化・再有効化が同期トークンに何をするか（ADR-0114 決定 3「利用者と管理者が読めるように」） |
| `docs/how-to/obsidian-plugin-device-check.md` §5 | 期待結果の欄に「同期が成功する（＝未修正の不具合）」 |
| `docs/api/east-west-grpc.md` §4 つ目の面 | 呼び出し元に DocumentService（同期の門・退職者削除）が無い |

除外したもの（理由）:

- `.ai-context/specs/**`・`.ai-context/adr/**`（確定済みの凍結記録。IADR-0431 のフォローアップ 1 も書き換えない。本 IADR が受ける）。
- `CHANGELOG.md`（自動生成）、`src/**/locales/**`（カタログ。文言を変えないので再生成差分も無い）。
- SC-17 画面の表示文言（「無効化（全セッション失効）」等）: 変えない。**同期トークンも無効化の後は拒否される**ので
  「全セッション失効」の表示は偽にならない。再有効化の挙動は画面仕様書に書く（UI の文言追加は計画外）。
- `docs/api/openapi.yaml`: 生成物。401 はもともと同期端点の応答に含まれ、形は変わらない。
- `docs/screens/SC-15_*`・`docs/tests/SC-15_*`・`UC-05`・`bff-session-design.md` の「全セッション」: ブラウザのセッションの話であり同期トークンに触れていない。
- `PrivateNoteMaintenanceService.cs:15` のヒット: 期限予告の段の列挙であり、無効化について何も主張していない。
- `docs/how-to/plan-id-range-history-annex.md:53`: ADR-0114 の題目の転記であり正しい。

## 対象範囲

- 対象:
  - `DocumentService`: 同期トークンの検証（`ResolveDeviceAsync`）へ所有者のアカウント状態の門を足す。新しいポート
    `IOwnerAccountDirectory`（gRPC 実装・未構成時の縮退）と `Program.cs` の登録。
  - 配備: document-service へ `Services__AuthorizationServiceGrpc`（helm values・compose）。
  - 試験（DocumentService.Tests）と上表の文書。
  - IADR-0474（方式・再有効化・依存の向き・fail-closed）。
- 対象外:
  - AuthorizationService（無効化の端点・名簿の gRPC 面）は**変えない**（既存の `GetUserAttributes` が `found` / `enabled` を返す）。
  - 案 B（無効化イベントで端末を全失効）は採らない（IADR-0474 で理由）。
  - 管理者が本人の端末を失効させる経路（ADR-0114 実測 3）は作らない（計画外）。
  - SC-17 / SC-20 の画面文言。

## 設計

- **案 A（同期要求ごとに名簿で有効かを確かめる）を採る。** 依存は knowledge → platform（`Platform.Shared.Contracts` の
  proto と `Platform.Shared.Infrastructure` の `UserDirectoryGrpcClient`）で、依存規則に沿う。
- ポート `IOwnerAccountDirectory.GetStateAsync(ownerId, ct)` → `OwnerAccountState { Unknown=0, Enabled, Disabled, NotFound }`。
  🔴 **通すのは `Enabled` だけ。** `Unknown` を 0 にして、既定値・未知の値が拒否へ倒れるようにする。
- gRPC 実装は `UserDirectoryGrpcClient.GetRetentionStatusAsync`（`GetUserAttributes` を読む）を使い、
  `null` → Unknown、`Found=false` → NotFound、`Enabled=false` → Disabled、それ以外 → Enabled。
  **時間切れ**（ADR-0114 決定 2 が名指し）を閉じるため、呼び出しに上限（5 秒）を掛け、超えたら Unknown。
- 未構成（`Services:AuthorizationServiceGrpc` 無し）は常に Unknown を返す縮退を登録する（＝同期は 401）。
- 門の位置: **端末が有効（未失効・期限内）と確定した後**に名簿を引く。トークンの無い・不正な要求で名簿を引かない。
- 応答は既存どおり**同じ 401**（欠落・不正・期限切れ・失効・無効化・判定不能を区別しない）。
- **キャッシュは持たない**（決定 1 の「最初の要求」を TTL の分だけ遅らせるため）。
- 再有効化: 端末は失効させていないので、**期限内・未失効のトークンは再び通る**（案 A の帰結。IADR-0474 に書く）。

## 受け入れ基準

- [x] Given 有効な同期トークンを持つ利用者 / When 無効化する / Then 次の同期要求は 401（無効化前の同じ要求は 200）
- [x] 5 端点（manifest / push / pull / delete / move）すべてで、無効化された所有者のトークンは 401
- [x] Given 名簿が引けない（Unknown）/ 名簿に居ない（NotFound）/ When 同期要求 / Then 401
- [x] Given 口が構成されていない配備 / When 同期要求 / Then 401（本番の縮退の向き）
- [x] Given 再有効化 / When 同じトークンで同期要求 / Then 200（案 A の帰結）
- [x] 有効な利用者には影響が無い（200・名簿は所有者の ID で 1 回引かれる）
- [x] 不正なトークンでは名簿を引かない
- [x] gRPC 実装の写像（null / Found=false / Enabled=false / 有効 / 時間切れ）
- [x] 変異試験: 門を外す・Unknown を通す・NotFound を通す・名簿を先に引く、のそれぞれで試験が赤になる

## テスト方針

- 結合（`TestWebApplicationFactory`）: 所有者のアカウント状態をスタブで宣言し、端点ごとに 401 / 200 を対で測る。
  スタブの既定は `Enabled`（既存の同期試験を巻き込まない）。**本番の縮退の向きは、スタブへ差し替えない
  ファクトリで別に測る**（既定が試験と本番で逆であることを試験で固定する）。
- 単体: gRPC 実装を偽の `UserDirectoryClient` で測る（DataSourceService の `FakeUserDirectoryClient` と同じ作法）。

## 計画書との差異

- 差異: なし。ADR-0114 決定 3 の委任どおり方式を IADR-0474 で閉じた。
- 付随して判明: IADR-0431 の退職者削除の口は document-service に配線されておらず、配備では縮退していた。
  本 PR の配線で**退職者削除も配備で動き始める**（ADR-0096 決定 1 の意図どおり）。PR 本文と IADR-0474 に明記する。

## 未決事項

- 実クラスタでの疎通（無効化 → 同期 401）は測っていない（live cluster 禁止）。how-to §5 の手順で確かめる。
