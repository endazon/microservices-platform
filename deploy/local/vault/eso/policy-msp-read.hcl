# IADR-0096 (#310): ESO 用の読み取り専用ポリシー。MSP secret（KV v2）の read のみ許可（最小権限）。
path "secret/data/msp/*" {
  capabilities = ["read"]
}
path "secret/metadata/msp/*" {
  capabilities = ["read", "list"]
}
