import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-11, FR-15, ADR-0018: 構成情報の照会（サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0129 決定 5: 実効構成・ドリフト・履歴は**別のエンドポイント**であり、独立に扱う。
// 1 本の失敗で画面全体を落とさず、その領域だけを縮退させる（旧実装 #138 / #139 の性質を保つ）。
//
// `/bff/admin/config` 群は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506 の射程）。手書き HTTP クライアントではない
// ——出口は foundation/api の 1 箇所に収束している（IADR-0121 決定 3）。

/** 構成バージョン（適用中の構成定義の Git コミット ID・適用日時・適用者）。 */
export interface ConfigVersion {
  gitCommit?: string | null;
  appliedAt?: string | null;
  appliedBy?: string | null;
}

/** 有効な段（順序・入出力イベント型・所属サービス）。 */
export interface PipelineStage {
  name: string;
  service: string;
  consumer: string;
  input: string;
  outputs: string[];
  enabled: boolean;
}

/** イベント接続（イベント型ごとの発行者・購読者）。 */
export interface EventBinding {
  event: string;
  publishers: string[];
  subscribers: string[];
}

/** ポート実装の選択（差し替えポイントごとの実装名・接続先）。 */
export interface PortSelection {
  port: string;
  implementation: string;
  target?: string | null;
}

/** 登録済みコネクタと有効・無効。 */
export interface Connector {
  name: string;
  enabled: boolean;
}

/** BFF の `EffectiveConfigDto` に対応する実効構成。 */
export interface EffectiveConfig {
  version: ConfigVersion;
  pipeline: PipelineStage[];
  eventBindings: EventBinding[];
  ports: PortSelection[];
  connectors: Connector[];
}

/** 個々の不一致（`DriftFindingDto`）。判定は API 側が行い、本画面は結果を表示するのみ。 */
export interface DriftFinding {
  kind: string;
  severity: string;
  target: string;
  detail: string;
}

/** ドリフト検出結果（`DriftReportDto`）。 */
export interface DriftReport {
  hasDrift: boolean;
  checkedAt: string;
  findings: DriftFinding[];
}

/**
 * 構成バージョン適用履歴の 1 エントリ（`ConfigVersionEntryDto`）。
 *
 * IADR-0046 / 計画 05_observability-ops（2026-08-04 確定・planning#190）: 正データ源は GitOps 層で、
 * API は永続化せず新しい順のスライスを返す。**保持範囲（Git 無期限 ＋ ArgoCD の 10 世代）は
 * GitOps 側が決める**ため、画面は件数を切り詰めない。
 */
export interface ConfigVersionEntry {
  gitCommit?: string | null;
  appliedAt?: string | null;
  appliedBy?: string | null;
  hadDrift?: boolean | null;
}

export const effectiveConfigKey = ['bff', 'admin', 'config'] as const;
export const configDriftKey = ['bff', 'admin', 'config', 'drift'] as const;
export const configHistoryKey = ['bff', 'admin', 'config', 'history'] as const;

/** 実効構成（段・接続・ポート選択・コネクタ・構成バージョン）。 */
export function useEffectiveConfig() {
  return useQuery({
    queryKey: effectiveConfigKey,
    queryFn: () => apiFetch<EffectiveConfig>('/admin/config'),
  });
}

/** ドリフト（宣言 Git との差分）。実効構成と独立に取得する。 */
export function useConfigDrift() {
  return useQuery({
    queryKey: configDriftKey,
    queryFn: () => apiFetch<DriftReport>('/admin/config/drift'),
  });
}

/** 構成バージョン適用履歴（新しい順）。実効構成と独立に取得する。 */
export function useConfigHistory() {
  return useQuery({
    queryKey: configHistoryKey,
    queryFn: async () => (await apiFetch<ConfigVersionEntry[]>('/admin/config/history')) ?? [],
  });
}

/**
 * 3 本の問い合わせを取り直す。
 *
 * `effectiveConfigKey` は他の 2 つの接頭辞でもあるため、**1 回の無効化で 3 本とも当たる**
 * （TanStack Query のキーは前方一致で照合される）。手書きの再取得は持たない。
 */
export function useRefreshConfigViewer() {
  const queryClient = useQueryClient();
  return () => void queryClient.invalidateQueries({ queryKey: effectiveConfigKey });
}
