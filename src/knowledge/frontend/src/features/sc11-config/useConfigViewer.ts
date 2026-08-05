import { useQueryClient } from '@tanstack/react-query';
import {
  getBffConfigDriftQueryKey,
  getBffConfigEffectiveQueryKey,
  getBffConfigHistoryQueryKey,
  useBffConfigDrift,
  useBffConfigEffective,
  useBffConfigHistory,
} from '@foundation/api/generated/config/config';
import { okData } from '@foundation/api/orvalSelect';
import type {
  ConfigVersionEntryDto,
  DriftReportDto,
  EffectiveConfigDto,
} from '@foundation/api/generated/bff.schemas';

// SC-11, FR-15, ADR-0018: 構成情報の照会（サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0129 決定 5: 実効構成・ドリフト・履歴は**別のエンドポイント**であり、独立に扱う。
// 1 本の失敗で画面全体を落とさず、その領域だけを縮退させる（旧実装 #138 / #139 の性質を保つ）。
//
// IADR-0135 決定 1（#519）: 3 本とも **orval 生成フック**で呼ぶ（`/bff/admin/config` 群は
// docs/api/openapi.yaml に**在る**——載せ替え前のコメントは「無く」と書いていたが誤りだった。#506 §実測 4）。
// 応答封筒は `okData` で剥がすため、画面が読む形は載せ替え前と同じ（本文そのもの）である。
// **404 秘匿の扱いは変えない**——非 2xx は `apiRequest` が `ApiError` を投げ、画面が `notFound` を
// 中立表示へ寄せる（IADR-0009 / IADR-0129 決定 3）。

/** 実効構成（段・接続・ポート選択・コネクタ・構成バージョン）。 */
export function useEffectiveConfig() {
  return useBffConfigEffective<EffectiveConfigDto, unknown>({
    query: { queryKey: getBffConfigEffectiveQueryKey(), select: okData },
  });
}

/** ドリフト（宣言 Git との差分）。実効構成と独立に取得する。 */
export function useConfigDrift() {
  return useBffConfigDrift<DriftReportDto, unknown>({
    query: { queryKey: getBffConfigDriftQueryKey(), select: okData },
  });
}

/**
 * 構成バージョン適用履歴（新しい順）。実効構成と独立に取得する。
 *
 * IADR-0046 / 計画 05_observability-ops（2026-08-04 確定・planning#190）: 正データ源は GitOps 層で、
 * API は永続化せず新しい順のスライスを返す。**保持範囲（Git 無期限 ＋ ArgoCD の 10 世代）は
 * GitOps 側が決める**ため、画面は件数を切り詰めない。
 */
export function useConfigHistory() {
  // `?? []` は残す（IADR-0132 決定 3）。契約上は必須でも、実行時に本文を検証する層は無い
  // ——`bffFetch` は本文が空なら `{}` を返す（`orvalMutator.ts`）。
  return useBffConfigHistory<ConfigVersionEntryDto[], unknown>({
    query: { queryKey: getBffConfigHistoryQueryKey(), select: (res) => okData(res) ?? [] },
  });
}

/**
 * 3 本の問い合わせを取り直す。
 *
 * **3 本を明示的に無効化する。** 生成キーは URL 1 要素（`['/bff/admin/config']` /
 * `['/bff/admin/config/drift']` / `['/bff/admin/config/history']`）であり、TanStack Query の部分一致は
 * **配列の要素単位**なので、`['/bff/admin/config']` は他の 2 本に当たらない
 * （載せ替え前の階層キー `['bff','admin','config']` が持っていた前方一致は成立しない。IADR-0135 決定 3）。
 * 手書きの再取得は持たない（IADR-0127 決定 5）。
 */
export function useRefreshConfigViewer() {
  const queryClient = useQueryClient();
  return () => {
    for (const queryKey of [
      getBffConfigEffectiveQueryKey(),
      getBffConfigDriftQueryKey(),
      getBffConfigHistoryQueryKey(),
    ]) {
      void queryClient.invalidateQueries({ queryKey });
    }
  };
}
