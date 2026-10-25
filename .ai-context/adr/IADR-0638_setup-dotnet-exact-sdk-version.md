---
title: IADR-0638 CI の setup-dotnet は SDK を厳密な版（10.0.403）で指定し、全ワークフローで同じ値にする。x 指定は配布元の障害で全 CI を止めるため使わず、aka.ms の復旧を確かめたら戻すかを判断する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0581]
author: claude
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_2266_setup-dotnet-exact-sdk-version.md
---

# IADR-0638: setup-dotnet の SDK を厳密な版で指定する（#2266）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#2266。版と範囲はコーディネーターの依頼 2026-10-26: P1・最優先の hotfix）

## 起点・関連

- 起点 issue: #2266。
- 関連: [IADR-0581](./IADR-0581_actions-sha-pin-and-dependabot.md)（actions の SHA 固定と Dependabot）。
- 作業仕様書: [20261026_2266_setup-dotnet-exact-sdk-version](../specs/20261026_2266_setup-dotnet-exact-sdk-version.md)。

## コンテキストと課題

- setup-dotnet の 11 か所は、すべて `dotnet-version: "10.0.x"` を指定していた。
- x 指定では、install script が `--channel 10.0` で呼ばれる。版は aka.ms 経由で `dotnetorigin.blob.core.windows.net` から解決される。
- 2026-10-26 19:09Z ごろから、そこが HTTP 409「Public access is not permitted on this storage account」を返している。develop を含む全 PR の CI が赤になった。
- 厳密な版（`--version`）では、install script は primary の `builds.dotnet.microsoft.com` と `ci.dot.net` を引く。こちらは通る（コーディネーターが `dotnet-install.sh --dry-run` で確認した）。

## 決定

### 決定 1: setup-dotnet の SDK は厳密な版 `10.0.403` で指定する

- 10.0.403 は、障害の前に CI が `10.0.x` から解決していた SDK である。手元と CI の挙動を変えない。

### 決定 2: 値は各所に同じものを書き、揃っていることを試験で見る

- workflow をまたいで値を 1 か所に置く手段が無い。
  - `env` は workflow ごとに閉じる。
  - `global-json-file` は、global.json が `8.0.0` + `rollForward: latestMajor` で SDK の版を決めないので使えない。
- そこで、`scripts/scripts.repo.test.js` が全 workflow の setup-dotnet の step を読み、次を確かめる。
  - すべてに `dotnet-version` がある。
  - その値が厳密な版（`数.数.数`）である。
  - 全箇所で値が同じである。
  - `global-json-file` を使っていない。
- 変異試験（x 指定・値の不一致・版の欠落・`global-json-file`）で、どれも検出することを確かめた。
- 先例は k3d の版（`K3D_VERSION`）で、2 つの workflow の値を同じ試験ファイルが突き合わせている。

### 決定 3: aka.ms の復旧を確かめたら、x 指定へ戻すかを判断する

- 戻す条件: aka.ms の `--channel 10.0` による版の解決が、409 を返さなくなったことを確認する。
- 判断の比較:
  - 厳密な版のままにすると、SDK の更新を手で追う費用がかかる。Dependabot は `dotnet-version` を更新しない。
  - x 指定へ戻すと、配布元の障害で再び全 CI が止まる危険がある。
- 判断の結果は、本 IADR への日付つき追記か、改訂する IADR で残す。

## 結果

- SDK の導入が配布元の 409 に依らなくなる。この PR の CI が、そのことを確かめる。
- SDK の更新（10.0.4xx の新しい版）は手作業になる。上げるときは、11 か所を同時に替える（試験が片側だけの更新を止める）。
- 残余:
  - `scripts/setup.sh`（SessionStart hook）も `--channel` を使う。失敗しても継続する作りで、CI を止めないので範囲外とした。
  - submodule の ai-stock-trading にも同型の指定がある。AST 側で別途判断する。
