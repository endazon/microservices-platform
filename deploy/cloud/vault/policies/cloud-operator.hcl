# NFR-18, IADR-0689 決定 2: オーナー用の AppRole（cloud-operator。ループバックからだけ入れる）の権限。
#   - 値を作る・足す（seed-secrets.sh・gen）: cloud/ の KV の作成・読み取り（在るかの確かめ）・patch。消さない。
#   - root の作り直し（root-token.sh。unseal の鍵も要る）。
#   policy・認証・マウントは変えられない（それは root の仕事）。
path "cloud/data/*"     { capabilities = ["create", "read", "update", "patch"] }
path "cloud/metadata/*" { capabilities = ["read", "list"] }
path "sys/generate-root-token/*" { capabilities = ["create", "read", "update", "delete", "sudo"] }
