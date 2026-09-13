// ADR-0066 決定 4 / IADR-0262 決定 4 と同じ作法: **公開面はこのファイルだけ**である。
// 外から `./useResolvedUsers` のような内部パスを直接 import しない。
export { useResolvedUsers } from './useResolvedUsers';
