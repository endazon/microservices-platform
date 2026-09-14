# SC-22, NFR-18, ADR-0095 決定 3, IADR-0433 決定 1・2, IADR-0453 決定 9 (#1411):
# BFF が画面（/admin/secrets）から秘密情報を投入するための policy（role `bff-secret-writer` に付与）。
#
# 🔴 項目ごとの**完全一致パス**だけを書く。ワイルドカード（`*` / `+`）を使わない —— `secret/data/msp/*` と
#    書いた瞬間に「SC-22 の項目の集合の外へ書けない」（ADR-0095 決定 3）が成立しなくなる。
# 🔴 data には `read` を与えない（値を読み返す経路を権限の層で断つ）。書き込みは KV v2 の部分更新（`patch`）で、
#    KV が無いときだけ `create`（cas=0）で作る。
# 🔴 `update` を与えない（IADR-0453 決定 10）。`update` は POST による KV の**全置換**を許し、同じ KV に同居する
#    構成（keycloak-smtp の host / port / starttls）や realm と対の値（app-secrets の *-auth-client-*）を
#    BFF のトークンで消せてしまう。コードが POST を全置換に使わないことは統制にならない（IADR-0433 決定 1 の理由）。
# 🔴 metadata は `read` だけ（版・作成時刻。値を持たない）。`list` / `delete` / `destroy` はどこにも与えない。
#
# 項目の集合の単一情報源は deploy/bootstrap/sc22-secret-items.json の `items[]` である。
# 本ファイルの path 集合がそれと完全一致することを Platform.Bff.Tests（SecretItemVaultPolicyTests）が固定する。
# `deferred[]` / `excluded[]` のパスをここへ足さないこと（足すと上記テストが落ちる）。

# msp/llm-provider-credentials（anthropic-api-key / openai-api-key）
path "secret/data/msp/llm-provider-credentials" {
  capabilities = ["create", "patch"]
}
path "secret/metadata/msp/llm-provider-credentials" {
  capabilities = ["read"]
}

# msp/keycloak-smtp（from / user / password。host / port / starttls は構成であり書かない）
path "secret/data/msp/keycloak-smtp" {
  capabilities = ["create", "patch"]
}
path "secret/metadata/msp/keycloak-smtp" {
  capabilities = ["read"]
}

# msp/wikijs-sync（apiKey）
path "secret/data/msp/wikijs-sync" {
  capabilities = ["create", "patch"]
}
path "secret/metadata/msp/wikijs-sync" {
  capabilities = ["read"]
}

# ai-stock-trading/app-secrets（外部 API キーと通知の 7 プロパティ。*-auth-client-* は書かない）
path "secret/data/ai-stock-trading/app-secrets" {
  capabilities = ["create", "patch"]
}
path "secret/metadata/ai-stock-trading/app-secrets" {
  capabilities = ["read"]
}
