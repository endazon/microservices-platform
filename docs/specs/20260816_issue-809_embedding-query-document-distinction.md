---
title: 作業仕様書 — 埋め込みでクエリと文書を区別して送る（#809）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - ADR-0016
  - ADR-0017
  - ADR-0013
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md"
related_specs: []
---

# 作業仕様書: 埋め込みの用途（クエリ／文書）をプロバイダへ伝える（#809）

## 1. 起点

**#336（Ruri v3 の実配備・nDCG@10 実測）をローカルで進められるか調べる過程で発見した。**
#336 の実測に着手する前に潰しておかないと、**測定そのものが歪む**。

## 2. 事象

用途 `EmbeddingRoutePurpose` は `EmbeddingEndpoints` が算出して**ルーターまでは渡していた**が、
**プロバイダへは落ちていなかった**。`IEmbeddingProvider` に渡す口が無かったためである。

```csharp
// Foundation/Endpoints/EmbeddingEndpoints.cs
var purpose = req.Purpose == EmbedPurpose.Query ? EmbeddingRoutePurpose.Query : EmbeddingRoutePurpose.Index;
var decision = router.Route(new EmbeddingRoutingRequest(sensitivity, purpose));   // ← ここまでは渡る
...
var vector = await provider.EmbedAsync(req.Text, decision.Model, decision.Dimensions, ct);  // ← 捨てている
```

## 3. なぜ問題か

### Ruri v3 はプレフィクスを必須としている（一次資料で確認）

`huggingface.co/cl-nagoya/ruri-v3-310m/raw/main/README.md` を実取得:

```
# Ruri v3 employs a 1+3 prefix scheme to distinguish between different types of text inputs:
# "トピック: "   is used for classification, clustering, and encoding topical information.
# "検索クエリ: " is used for queries in retrieval tasks.
# "検索文書: "   is used for documents to be retrieved.
```

`SelfHostedEmbeddingProvider` は素の text を送っていた。Voyage も `input_type` を送っていなかった。

### ★ この状態で nDCG@10 を測ると、切替の判断を誤る

`ADR-0017` は「**voyage-3.5 比で大幅に劣化する場合は BGE-M3 へ切り替える**」と定めている。
**プレフィクス必須の Ruri のほうが大きく損をする**ので、欠陥を残したまま #336 の実測を行うと
**Ruri が不当に不利に見え、本来不要な切替を選んでしまう。**

## 4. 設計判断: プレフィクスは設定駆動・`input_type` は実装に埋める

**両者は性質が違う**ので扱いを分けた。

| | 何に属するか | 扱い |
| --- | --- | --- |
| Ruri の 1+3 プレフィクス | **モデル固有**。`ADR-0017` が代替に挙げる **BGE-M3 は使わない** | **設定駆動**（`Embedding:SelfHosted:QueryPrefix` / `:DocumentPrefix`）。**既定は空** |
| Voyage の `input_type` | **プロバイダの API 契約**。プロバイダを替えれば意味を失う | 実装に埋める |

**既定を空にする理由**: モデルを差し替えたときにプレフィクスが残ると、
**今度は別の意味で埋め込みが歪む**。プレフィクスは「モデルがそう要求している」ときだけ付ける。
既定が空なので、**設定しなければ現行とバイト等価**である。

`appsettings.json` には Ruri 用の値を設定し、差し替え時に空にする旨をコメントで残した。

## 5. 変更

| ファイル | 変更 |
| --- | --- |
| `Foundation/Ports/IEmbeddingProvider.cs` | `EmbedAsync` に `EmbeddingRoutePurpose purpose` を追加 |
| `Composable/Adapters/SelfHostedEmbeddingProvider.cs` | 設定されたプレフィクスを用途に応じて前置 |
| `Composable/Adapters/VoyageEmbeddingProvider.cs` | `input_type` を `query` / `document` で送る |
| `Foundation/Endpoints/EmbeddingEndpoints.cs` | 算出済みの `purpose` を渡す（1 行） |
| `appsettings.json` | Ruri の 2 プレフィクスを設定 |
| `tests/LlmGateway.Api.Tests/EmbeddingPurposeTests.cs`（新規） | 用途別の送信内容を固定（6 件） |
| 同 `EmbeddingEndpointTests.cs` / `TestWebApplicationFactory.cs` | スタブのシグネチャ追随 |

## 6. 検証

### TDD

先にテストを書き、**インタフェースに口が無いためコンパイルが通らない**ことを確認してから実装した。

```
（実装前）error CS1503: 引数 4: 'EmbeddingRoutePurpose' から 'CancellationToken' へ変換できません（8 箇所）
（実装後）成功! -失敗: 0、合格: 151、合計: 151
```

### 変異試験（`session-handoff.md` §5 型 4）

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| MC1 | セルフホストが purpose を無視し常に文書プレフィクスを付ける | **RED** |
| MC2 | セルフホストがプレフィクスを付けない（元の実装） | **RED** |
| MC3 | Voyage の `input_type` を常に `document` にする | **RED** |
| MC4 | Voyage が `input_type` を送らない（元の実装） | **RED** |
| — | 変異なし | GREEN（6 件） |

> **テストの書き方で 1 度落とし穴を踏んだ。** 当初は送信本文を日本語で部分一致させていたが、
> `System.Text.Json` は既定で非 ASCII を `\uXXXX` へエスケープするため外れた
> （実装は正しく動いていた）。**JSON を解析して値で比べる**形に直した ——
> **表現ではなく内容を検査する。**

## 7. スコープ外

- **`トピック: ` プレフィクス**（分類・クラスタリング用）は現在その用途が無いので入れていない。
  `EmbeddingRoutePurpose` に用途が増えたときに足す。
- **nDCG@10 の実測そのもの**は #336。本 PR はその前提条件を整えるだけである。
- **`Embedding__Voyage__ApiKey` の Helm 配線が無い**問題（compose のみ存在）は別。
  本 PR は送信内容だけを直す。

## 8. 未決事項

なし。
