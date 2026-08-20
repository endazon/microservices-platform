---
title: 作業仕様書 — Qdrant コレクションが作られない Options のセクション名を是正する（#806）
type: spec
status: done
related_ids:
  - FR-02
  - ADR-0016
  - IADR-0014
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
related_specs: []
---

# 作業仕様書: `EmbeddingCollectionsOptions` のセクション名是正（#806）

## 1. 起点

**#336（Ruri v3 の実配備・nDCG@10 実測）をローカルで進められるか調べる過程で見つけた、独立の実バグ。**
稼働クラスタで Qdrant のコレクションが 0 件であることを実測したのが発端である。

> **関連する作業仕様書へのリンクは張っていない。** #779（エッジ TLS 終端）の作業仕様書は
> PR #792 が未マージで develop に存在せず、`related_specs` に書くと `check-doc-links` が落ちる。
> #792 の着地後に相互リンクを張ること。

```
$ curl -s http://127.0.0.1:16333/collections
{"result":{"collections":[]},"status":"ok"}          ← 0 件

$ kubectl -n microservices-platform logs deploy/ingestion-service | grep -i qdrant
[04:08:16 INF] Qdrant collections ensured for ingestion index (per-model)   ← 成功ログ
```

**「作った」と言いながら 1 つも作っていない。**

## 2. 真因

`Foundation/Ports/EmbeddingCollectionsOptions.cs`

```csharp
public const string SectionName = "Embedding:Collections";   // ← 誤り
public List<EmbeddingCollectionOptions> Collections { get; set; } = [];
```

`appsettings.json` は `"Embedding": { "Collections": [ … ] }`。
**`Embedding:Collections` は配列そのもの**なので、そこから更にプロパティ `Collections` を探すと
`Embedding:Collections:Collections` を見にいく。**存在しないので空リストのままバインドが成功する**（例外は出ない）。

正しくは `SectionName = "Embedding"`。
隣の `EmbeddingRoutingOptions`（`"Embedding:Routing"` ＋ プロパティ `Endpoints`）は重複しておらず正しく効いている。

### 効き方が 2 つある

1. **索引が作られない。** `QdrantIngestionVectorStore.EnsureCollectionsAsync` の `foreach` が 0 回まわり、
   `QdrantBootstrapHostedService` は例外なしで成功ログを出す。
2. **★ 残存防止削除が無言の no-op になる。** `DeleteByDocumentFromAllAsync` も同じ `_collections` を回すので
   何も消さない。これは機密区分が `public → confidential` へ変わったときに
   **旧コレクション側へチャンクが残る**のを防ぐ仕掛けであり、**ABAC バイパスの潜在欠陥**である。
   現状はコレクションが 0 件なので顕在化していないが、**索引を作った瞬間に有効になる**。

## 3. 母集合（`.claude/rules/traceability.md` 規則 1〜8）

**是正の対象は「セクション名の末尾とプロパティ名が重複している Options クラス」である。**
1 件直して終わりにせず、**同型の誤りが他に無いかを全数で引いた**（規則 7）。

```console
$ git grep -nI "const string SectionName" -- src ':!*/bin/*' ':!*/obj/*'
DataSourceSyncOptions.cs:7:        "DataSourceSync"
EmbeddingCollectionsOptions.cs:9:  "Embedding:Collections"     ← 対象
EmbeddingRoutingOptions.cs:7:      "Embedding:Routing"
LlmRoutingOptions.cs:7:            "Llm:Routing"
IntrospectionOptions.cs:7:         "Introspection"
IntrospectionOptions.cs:23:        "Config"
IntrospectionOptions.cs:47:        "Drift"
PipelineOptions.cs:7:              "Pipeline"
ObjectStorageOptions.cs:8:         "ObjectStorage"
```

**全 9 件のうち、セクション名の末尾がプロパティ名と一致するのは `EmbeddingCollectionsOptions` の 1 件だけ**
（`Embedding:Collections` ＋ `Collections`）。走査スクリプトで機械的に突き合わせて確認した。

- `Embedding:Routing` のプロパティは `Endpoints` —— 重複しない
- `Llm:Routing` のプロパティは `Endpoints` / `Models` / `NonZdrModels` —— 重複しない
- 残り 6 件はセクション名が `:` を含まない単層 —— 構造上この誤りが起きない

**除外**: `planning/`（submodule・pin のみ）と `src/ai-stock-trading`（別プロジェクトの submodule。
本リポから変更しない）、`bin/` / `obj/`（ビルド生成物）。

### 規則 10 —— この是正で新たに誤りになる自分の記述

`"Embedding:Collections"` を文字列として参照している箇所を引き直した結果、
**コード・文書とも 0 件**（`SectionName` 定数経由でしか使われていない）。追随は不要である。

## 4. 変更

| ファイル | 変更 |
| --- | --- |
| `Foundation/Ports/EmbeddingCollectionsOptions.cs` | `SectionName` を `"Embedding"` へ。**なぜ誤るのかをコメントで残す**（同型の再発防止） |
| `tests/IngestionService.Worker.Tests/EmbeddingCollectionsOptionsBindingTests.cs`（新規） | 設定バインドの回帰テスト 3 件 |

**検査器は足していない。** `CLAUDE.md` の運用ガイドは「検査器・規約の追加は**同型の事故が 2 回起きたら**」と
定めており、本件は 1 回目である。**代わりに、型そのものを固定するテスト**
（`セクション名の末尾がプロパティ名と重複していない`）を単体テストとして置いた ——
これは `EmbeddingCollectionsOptions` 1 クラスに閉じる。**2 回目が起きたら全 Options クラスを走査する検査器へ引き上げる。**

## 5. 検証

### TDD（先にテストが落ちることを実測した）

```
（修正前）失敗! -失敗: 3、合格: 0、合計: 3
（修正後）成功! -失敗: 0、合格: 28、合計: 28
```

### 変異試験（`session-handoff.md` §5 型 4）

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| MB1 | `SectionName` を元の誤った値 `"Embedding:Collections"` へ戻す | **RED** |
| MB2 | `SectionName` を無関係な値 `"Embeddings"` へ | **RED** |
| — | 変異なし | GREEN（28 件） |

### 実機（稼働中の k3s）

イメージを再ビルドして `rollout restart` したのち:

```console
$ curl -s http://127.0.0.1:16333/collections
{"result":{"collections":[
  {"name":"knowledge_chunks_ruri_v3"},
  {"name":"knowledge_chunks_voyage_3_5"}]},"status":"ok"}

$ curl -s .../collections/knowledge_chunks_voyage_3_5 | grep -o '"size":[0-9]*'  → "size":1024
$ curl -s .../collections/knowledge_chunks_ruri_v3    | grep -o '"size":[0-9]*'  → "size":768
```

**0 件 → 2 件。次元も `ADR-0016` の宣言どおり（voyage 1024 / ruri 768）。**

## 6. スコープ外

- **Qdrant の永続化**（現在 emptyDir で再起動ごとに消える）は **#787**。本 PR は作成の可否だけを直す。
- **機密区分変更時の残存**を否定形テストで固定することは #438 / #458 の射程。
  本 PR は「削除処理が回る前提（コレクションが存在する）」を復旧させるところまで。

## 7. 未決事項

なし。
