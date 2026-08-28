---
title: IADR-0286 既定資格情報はイメージへ焼かず、未注入は起動失敗にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0002
  - ADR-0008
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0286 既定資格情報はイメージへ焼かず、未注入は起動失敗にする

## 起点・課題

#1012。`Program.cs` の `?? "Host=…;Username=kp;Password=kp"` が「構成の注入漏れ」を
**起動失敗ではなく既定の資格情報での接続成功**へ倒していた。

🔴 **着手時の実測で、issue の前提が 1 つ覆った。** `??` は**実際には到達しない** ——
**同じ既定値が `appsettings.json`（イメージへ焼かれる）にも入っていた**ためである。
したがって:

- k8s が helm で注入せずに動いていた理由は「コード既定」ではなく **`appsettings.json`** だった。
- #1009 が McpServer / NotificationService へ入れた fail-fast も**同じ理由で不発**だった
  （throw は書かれているが appsettings が値を供給する）。
- **`Program.cs` だけを直すと「直したように見えて欠陥は残る」。**

## 決定

### 決定 1 — 撤去するのは「イメージへ焼かれる本番既定」である

`appsettings.json`（Development を除く）から `ConnectionStrings` を撤去する。
**`appsettings.Development.json` は残す** —— イメージの本番既定ではなく、`dotnet run` の
ローカル利便であり、撤去しても守るものが無く開発者の手元だけ壊れる。

### 決定 2 — 合成ルートは 1 サービス 1 解決点にし、未設定なら落とす

`var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException(...)`
を**ヘルスチェック登録より前**に置き、ヘルスチェックと `DbContext` の両方で使い回す
（従前は同じリテラルが 2 箇所に重複していた）。**共有ヘルパは作らない** ——
1 行の `??` に抽象を被せるのは過剰であり、#1009 の先例と形を揃える方が読み手に親切である。

### 決定 3 — 配備側の注入が先、コードの撤去は後

**helm には `ConnectionStrings` の注入が 1 件も無かった**（実測）。撤去だけ先に入れると k8s が
起動不能になる。`global.db`（host / port / user / existingSecret / passwordKey）と
per-service `database` を足し、**パスワードは Secret から `DB_PASSWORD` として入れ、
接続文字列は k8s の `$(VAR)` 補間で組む**（env の値へ平文パスワードを描画しない）。
⚠️ `$(VAR)` は**同一 container 内で先に定義した env しか参照できない** —— 順序を崩さないこと。

> ［2026-08-28 追記 / #1012］🔴 **「配備側」は 1 本ではなく 2 本ある。** 決定 3 は helm への注入だけを
> 数えており、**ローカル k8s の secret 供給に既定（手動 apply）と ESO（Vault→ExternalSecret）の
> 2 経路がある**ことを落としていた。`postgres-app` を `k8s-local-up.sh` の `ESO != 1` ブロックへ
> 置いた時点で「ESO=1 では別経路が供給する」と約束したことになるが、その
> `externalsecret-postgres-app.yaml` を作っていなかったため、**ESO=1 では供給元が 1 つも無く**
> DB を持つ 8 サービスが起動しない状態でコミットされた（着地後のレビューが 2 回続けて検出）。
>
> **決定 3 を次のように読むこと: 「配備側の注入が先」の“配備側”は、その環境で secret を供給し得る
> 経路すべてである。** 手当ては `.ai-context/specs/20260828_issue-1012_default-credentials.md`
> の「事後に見つけた欠陥」節に記録した（ExternalSecret 新設・seed・apply・対の試験 2 本＋変異試験）。
> 決定 1〜5 そのものは変えていない。

### 決定 4 — テストは「実配備と同じ経路」で注入する

`WebApplicationFactory.ConfigureAppConfiguration` では**間に合わない**（実測）。トップレベル文の
`builder.Configuration.GetConnectionString(...)` は `builder.Build()` より前に評価されるため、
ホスト構築時のコールバックは既に読まれた後に適用される。`[ModuleInitializer]` で**環境変数**へ
入れる（`TestDatabaseConfiguration.cs`）。DbContext は InMemory へ差し替わるので、
**資格情報を持たない値**（`Host=localhost;Database=<svc>_test`）で足りる。

### 決定 5 — 再混入は前方一方向のラチェットで止める

`scripts/check-default-credentials.js` が `Program.cs` と本番 `appsettings.json` を走査し、
**`Username=` / `Password=` を伴う接続文字列**と**資格情報つき `amqp://user:pass@`** を落とす。
**ホストと DB だけの値は落とさない** ——「秘密を書かせない」検査であって
「設定を書かせない」検査ではない。既知の残件（RabbitMQ 13 箇所）は baseline に凍結し、
**増やせないが減らすのは自由**（#1022 で解消する）。

## 結果

- 未注入は起動時に落ちる（変異 M1 で実測）。再混入は CI が止める（変異 M2 で実測）。
- 両ユニットの全テストが緑（knowledge 12 / platform 7 プロジェクト）。
- RabbitMQ の同型欠陥は **#1022** へ分けた（ブローカ側の資格情報変更を伴うため）。

## 関連

- #1012（本 ADR の起点）・#1022（RabbitMQ 側の残件）・#1009（先例。本 ADR が不発だった理由を記録）
- 作業仕様書: `.ai-context/specs/20260828_issue-1012_default-credentials.md`
