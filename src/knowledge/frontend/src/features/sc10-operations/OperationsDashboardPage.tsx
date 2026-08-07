import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { Link } from '@tanstack/react-router';
import {
  Alert,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Label,
  Select,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { appConfig } from '@foundation/config/runtimeConfig';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
import { opsTools } from './opsTools';
import { DAYS_OPTIONS, useDashboardSummary } from './useDashboardSummary';
import type { DaysOption } from './useDashboardSummary';
// SC-10, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type {
  DashboardSummaryDto,
  SearchTrendDto,
  UsagePointDto,
} from '@foundation/api/generated/bff.schemas';

// SC-10, UC-05, FR-10: 運用ダッシュボード（05_screens: ルート /admin/ops）。
// 利用状況・検索傾向・回答品質のサマリと、専用ツール（Grafana / Kiali / Jaeger・Tempo）・
// 構成ビューア（SC-11）への入口を提供する参照専用画面。
//
// 実装しない要素（画面仕様書 docs/screens/SC-10_operations-dashboard.md §hi-fi モックアップとの対応）:
//   - **SLO カード（達成率・検索 p95）・LLM コストカード**: 契約（DashboardSummaryDto）に該当項目が無い。
//     レイテンシとコストは可観測性基盤（Grafana）側にあり BFF は集約していない。
//   - **「人/日」（一意利用者数）**: 契約が返すのはイベント件数（UsagePointDto）であり利用者の一意性を持たない。
//     実装は検索総数・回答総数を出す（**部分未実装**として画面仕様書に明記した）。
//   - **「ナレッジ健全性」節**（孤立文書・未解決リンク・未要約クラスタ・陳腐化文書、辺の型ごとの
//     使用件数、フォールバック警告、個人資料を除く旨の注記）: **着手保留**（IADR-0119。本画面の
//     実装時点 = 2026-08-05）。計画自身が「起案・2026-08-01。Phase 3」とし、指標は ADR-0033
//     （当時の状態 Proposed）に由来する。
//     **［2026-08-07 / #586］この保留理由は失効した**——ADR-0033 は Accepted へ移り（planning 3e58b97
//     = PR planning#244〔裁定依頼 planning#237〕）、IADR-0119 の保留は FR-17 / FR-18 について解除された。**本節を足すかどうかは
//     #504 / #452 の作業仕様書で判断する**（#586 は pin 更新と事実の追随に限り、UI は変更していない）。
//     根拠は IADR-0129 の 2026-08-07 追記。
//   いずれも feedback/20260805_sc09-11-admin-ops-contract-gaps.md に記録した（起票は親）。
//
// IADR-0129 決定 3: 403（権限不足）と 404（不在／秘匿）は**同一の中立文言**へ寄せる。
// 旧実装は出し分けており、画面の文言から権限の有無が読めた（IADR-0009 の趣旨に反する）。

/**
 * 契約の利用イベント種別（`UsageEventType` の 2 値）を表示名へ写す。
 *
 * **未知の値は握り潰さない**——`—` や「不明」へ丸めず生値をそのまま出す。契約が 2 値と定めていても、
 * サーバが将来 3 つ目を返したときに画面が異常へ気付ける状態にしておく。
 */
const USAGE_EVENT_LABELS: Record<string, MessageDescriptor> = {
  search: msg`検索`,
  answer: msg`AI 回答`,
};

function usageEventLabel(eventType: string): string {
  const label = USAGE_EVENT_LABELS[eventType];
  return label ? i18n._(label) : eventType;
}

export function OperationsDashboardPage() {
  const { t } = useLingui();
  const [days, setDays] = useState<DaysOption>(7);
  const summary = useDashboardSummary(days);

  // IADR-0009 / IADR-0129 決定 3: 権限不足（403）と不在（404）を区別しない。
  const unavailable =
    summary.error instanceof ApiError &&
    (summary.error.kind === 'forbidden' || summary.error.kind === 'notFound');

  const tools = opsTools(appConfig().opsLinks);

  return (
    <section>
      <div className="mb-3 flex flex-wrap items-end justify-between gap-2">
        <div>
          <h1 className="text-lg font-semibold text-[--color-fg]">
            <Trans>運用ダッシュボード</Trans>
          </h1>
          {/* モックの副題は「SLO・利用状況・コスト」だが、SLO とコストは契約に無い。
              出さないものを名乗ると読み手を誤らせるため、出すものだけを書く。 */}
          <p className="text-xs text-[--color-fg-muted]">
            <Trans>利用状況・検索傾向・回答品質（SLO・コストは Grafana で参照）</Trans>
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Label htmlFor="ops-days" className="shrink-0">
            <Trans>集計期間</Trans>
          </Label>
          <Select
            id="ops-days"
            selectSize="sm"
            value={days}
            onChange={(e) => setDays(Number(e.target.value) as DaysOption)}
          >
            {DAYS_OPTIONS.map((d) => (
              <option key={d} value={d}>
                {t`直近 ${d} 日`}
              </option>
            ))}
          </Select>
        </div>
      </div>

      {summary.isPending && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}

      {summary.isError && unavailable && (
        <p className="text-sm">
          <Trans>運用ダッシュボードは利用できません。</Trans>
        </p>
      )}
      {/* 5xx・ネットワーク断は中立化しない。系の状態であって資源の存在ではなく、秘匿の対象ではない
          ——区別しないと運用者が「権限が無い」と誤読して障害を見逃す。 */}
      {summary.isError && !unavailable && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(summary.error, t`運用サマリを取得できませんでした。`).join(' / ')}
        </Alert>
      )}

      {summary.isSuccess && summary.data && (
        <SummaryView summary={summary.data} />
      )}

      <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
        <Trans>詳細ツール</Trans>
      </h2>
      {tools.length === 0 ? (
        <p className="text-sm">
          <Trans>外部ツールの導線は未設定です。</Trans>
        </p>
      ) : (
        <ul className="flex flex-wrap gap-2">
          {tools.map((tool) => (
            <li key={tool.id}>
              <a
                href={tool.url}
                target="_blank"
                rel="noreferrer"
                className="inline-flex flex-col rounded-[--radius-control] border border-[--color-border] bg-[--color-surface] px-3 py-2 text-sm text-[--color-fg] hover:bg-[--color-surface-muted]"
              >
                <span className="font-medium">{tool.name} ↗</span>
                <span className="text-xs text-[--color-fg-muted]">{i18n._(tool.description)}</span>
              </a>
            </li>
          ))}
        </ul>
      )}

      {/* IADR-0129 決定 4: 導線を権限で出し分けない。本画面へ到達できるのは platform-admin だけであり、
          platform-admin は ConfigViewer（admin または operator）の部分集合であるため、
          ロール判定は**この画面では常に真**になる（到達しない分岐を作らない）。
          SC-10 の閲覧ロールが広がる時点で、そのとき必要な出し分けを書く。 */}
      <p className="mt-3 text-sm">
        <Link to="/admin/config-viewer" className="text-[--color-brand] hover:underline">
          <Trans>構成ビューア →</Trans>
        </Link>
      </p>

      <Alert tone="info" className="mt-3" label={t`運用ツール`}>
        <Trans>
          運用面は専用ツールで提供します: Grafana（メトリクス・コスト）・Kiali（サービスメッシュ）・Jaeger / Tempo（分散トレース）。本画面はこれらへの入口です。
        </Trans>
      </Alert>
    </section>
  );
}

/**
 * KPI カード ＋ 一覧。
 *
 * 独立の部品にしてあるのは `lingui/no-expression-in-message` のためである——
 * 補間には**素の変数だけ**を置く（`${summary.quality.up}` のような式を入れると、
 * 抽出されたメッセージから元の値が読めなくなり、翻訳者が文脈を判断できない）。
 */
function SummaryView({ summary }: { summary: DashboardSummaryDto }) {
  const { t } = useLingui();
  const up = summary.quality.up;
  const down = summary.quality.down;
  return (
    <>
      <div className="mb-3 grid gap-3 sm:grid-cols-3">
        <Kpi label={t`検索総数`} value={String(summary.totalSearches)} />
        <Kpi label={t`回答総数`} value={String(summary.totalAnswers)} />
        <Kpi
          label={t`満足率`}
          value={`${Math.round(summary.quality.satisfactionRate * 100)}%`}
          note={t`👍 ${up} / 👎 ${down}`}
        />
      </div>

      <div className="mb-3 grid gap-3 lg:grid-cols-2">
        <UsageTrendTable points={summary.usageTrend} />
        <SearchTrendTable terms={summary.topSearchTerms} />
      </div>
    </>
  );
}

function Kpi({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <Card>
      <CardHeader className="mb-1">
        <CardTitle className="text-xs uppercase tracking-wide text-[--color-fg-muted]">
          {label}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="text-2xl font-semibold text-[--color-fg]">{value}</div>
        {note && <div className="text-xs text-[--color-fg-muted]">{note}</div>}
      </CardContent>
    </Card>
  );
}

function UsageTrendTable({ points }: { points: UsagePointDto[] }) {
  const { t } = useLingui();
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <Trans>利用状況（日次）</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {points.length === 0 ? (
          <p>
            <Trans>期間内の利用はありません。</Trans>
          </p>
        ) : (
          <Table>
            <TableCaption>{t`利用状況（日次）の一覧`}</TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell>
                  <Trans>日付</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>種別</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>件数</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {points.map((p, i) => (
                <TableRow key={`${p.date}-${p.eventType}-${i}`}>
                  <TableCell>{p.date}</TableCell>
                  <TableCell>{usageEventLabel(p.eventType)}</TableCell>
                  <TableCell>{p.count}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}

function SearchTrendTable({ terms }: { terms: SearchTrendDto[] }) {
  const { t } = useLingui();
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <Trans>検索傾向（上位語）</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {terms.length === 0 ? (
          <p>
            <Trans>検索傾向はまだありません。</Trans>
          </p>
        ) : (
          <Table>
            <TableCaption>{t`検索傾向（上位語）の一覧`}</TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell>
                  <Trans>検索語</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>件数</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {terms.map((term) => (
                <TableRow key={term.term}>
                  <TableCell>{term.term}</TableCell>
                  <TableCell>{term.count}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
