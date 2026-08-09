import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
  Textarea,
} from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
import { AnalysisTaskRequestTaskType } from '@foundation/api/generated/bff.schemas';
import type { AiAnswerDto, CitationDto } from '@foundation/api/generated/bff.schemas';
import {
  buildAnalysisRequest,
  isSubmittableInstruction,
  MAX_INSTRUCTION_LENGTH,
  MAX_RANGE_QUERY_LENGTH,
  TASK_TYPES,
  taskTypeLabel,
} from './analysisRange';
import type { TaskType } from './analysisRange';
import { useAnalysisTask } from './useAnalysisTask';
import { ScopeFilter } from '../scope-filter/ScopeFilter';
import { EMPTY_SELECTION } from '../scope-filter/scopeFilter';
import type { ScopeSelection } from '../scope-filter/scopeFilter';

// SC-08, UC-02, FR-07/FR-11/FR-05: AI分析ダッシュボード（05_screens: ルート /analyze）。
// 範囲を指定して分析（比較・抽出を含む）を依頼し、結果と出典を確認する。出典から SC-03 へ遷移する。
// ロール限定は無い（05_screens §共通シェル: 利用者グループは ABAC の権限内で全利用者が使える）。
//
// **［#539］分析対象のチップ（タグ・部門・プロジェクト）を実装した。**
// 従前ここには「planning#197 の裁定を待つ」と書いてあったが、裁定（2026-08-05 Q1・Q3・Q9）が着地し、
// 権限内候補の照会口も #540 で着地した。**「フォルダ」は保留ではなく不採用である**（Q9）。
// チップは SC-01 と同じ部品（`features/scope-filter`）を使う——同じ操作が画面ごとに違うと、
// 利用者は操作を覚え直すことになる。

export function AnalysisDashboardPage() {
  const { t } = useLingui();
  const [instruction, setInstruction] = useState('');
  const [taskType, setTaskType] = useState<TaskType>(AnalysisTaskRequestTaskType.Analyze);
  const [rangeQuery, setRangeQuery] = useState('');
  // FR-07, SC-08, #539: 分析対象のチップ（タグ・部門・プロジェクト）。
  const [scope, setScope] = useState<ScopeSelection>(EMPTY_SELECTION);
  const { outcome, run } = useAnalysisTask();

  const canSubmit = isSubmittableInstruction(instruction) && outcome.kind !== 'running';

  return (
    <section>
      <h1 className="text-lg font-semibold text-[--color-fg]">
        <Trans>AI分析依頼</Trans>
      </h1>
      <p className="mb-4 text-sm text-[--color-fg-muted]">
        <Trans>範囲指定の分析依頼・結果・出典</Trans>
      </p>

      {/* SC-08: 分析対象の指定（チップ）。**候補は権限内に限る**（#540 の口）。 */}
      <div className="mb-3">
        <ScopeFilter selection={scope} onChange={setScope} disabled={outcome.kind === 'running'} />
      </div>

      <form
        className="grid gap-3 md:grid-cols-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (!canSubmit) return;
          run(buildAnalysisRequest(instruction, taskType, rangeQuery, scope));
        }}
      >
        <Card>
          <CardHeader>
            {/* 「（権限内に限定）」は存在秘匿の説明そのものであり省略しない。 */}
            <CardTitle>
              <Trans>分析対象（権限内に限定）</Trans>
            </CardTitle>
          </CardHeader>
          <CardContent>
            <Label htmlFor="range-query">
              <Trans>検索条件で追加</Trans>
            </Label>
            <Input
              id="range-query"
              value={rangeQuery}
              maxLength={MAX_RANGE_QUERY_LENGTH}
              onChange={(e) => setRangeQuery(e.target.value)}
              placeholder={t`省略時は分析内容を流用します`}
            />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>
              <Trans>分析内容</Trans>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            <div>
              <Label htmlFor="instruction" requiredHint={t`（必須）`}>
                <Trans>分析内容（指示）</Trans>
              </Label>
              <Textarea
                id="instruction"
                rows={3}
                value={instruction}
                maxLength={MAX_INSTRUCTION_LENGTH}
                onChange={(e) => setInstruction(e.target.value)}
                placeholder={t`例: Q1の営業報告を部門別に比較し、共通する失注要因を抽出して`}
              />
            </div>
            <div>
              {/* FR-07「分析・比較・抽出」。選択肢が無いと比較・抽出へ到達できない。 */}
              <Label htmlFor="task-type" requiredHint={t`（必須）`}>
                <Trans>タスク種別</Trans>
              </Label>
              <Select
                id="task-type"
                value={taskType}
                onChange={(e) => setTaskType(e.target.value as TaskType)}
              >
                {TASK_TYPES.map((type) => (
                  <option key={type} value={type}>
                    {i18n._(taskTypeLabel(type))}
                  </option>
                ))}
              </Select>
            </div>
            <div>
              <Button type="submit" variant="primary" disabled={!canSubmit}>
                <Trans>分析実行</Trans>
              </Button>
            </div>
          </CardContent>
        </Card>
      </form>

      {outcome.kind === 'running' && (
        <p role="status" className="mt-3 text-sm text-[--color-fg-muted]">
          <Trans>分析を実行中…</Trans>
        </p>
      )}

      {/* UC-02 例外フロー: 権限外は対象から除外し、権限の有無を開示しない（存在秘匿）。
          空回答・403・404 をすべて同じ中立文言へ寄せる。 */}
      {outcome.kind === 'empty' && (
        <p className="mt-3 text-sm">
          <Trans>該当する情報が見つかりませんでした。</Trans>
        </p>
      )}

      {outcome.kind === 'failed' && (
        <Alert tone="danger" role="alert" className="mt-3" label={t`エラー`}>
          {toMessages(outcome.error, t`分析を実行できませんでした。`).join(' / ')}
        </Alert>
      )}

      {outcome.kind === 'answered' && (
        <Card className="mt-3">
          <CardHeader>
            <CardTitle>
              <Trans>結果</Trans>
            </CardTitle>
          </CardHeader>
          <CardContent>
            <p className="whitespace-pre-wrap">{outcome.answer.answer}</p>

            {(outcome.answer.citations ?? []).length > 0 && (
              <p className="mt-2 flex flex-wrap items-center gap-2 text-sm text-[--color-fg-muted]">
                <Trans>出典:</Trans>
                {(outcome.answer.citations ?? []).map((citation, index) => (
                  <CitationLink key={citation.chunkId ?? index} citation={citation} />
                ))}
              </p>
            )}

            {/* FR-11（モデル振り分けの利用面）/ IADR-0111: model が空なのは「AI へ送信していない縮退」
                （ABAC 不許可・機密区分による送信拒否・ゲートウェイ不達）を意味する。空のままだと
                「モデル: 」がぶら下がって読めないため、未送信であることを明示する。 */}
            <ModelFootnote answer={outcome.answer} />
          </CardContent>
        </Card>
      )}

      {/* 05_screens §SC-08 の注記。静的な注記なので role は付けない。 */}
      <Alert tone="info" className="mt-3" label={t`注記`}>
        <Trans>
          機密区分の高いデータは外部 API へ送信せずセルフホスト LLM で処理します（UC-02 代替フロー・
          データ越境ポリシー）。権限外のデータは黙って対象外になります（存在秘匿）。
        </Trans>
      </Alert>
    </section>
  );
}

/**
 * モデルとトークン数の脚注。
 *
 * FR-11（モデル振り分けの利用面。02_requirements トレーサビリティ表）/ IADR-0111:
 * `model` が空なのは「**AI へ送信していない縮退**」（ABAC 不許可・機密区分による送信拒否・
 * ゲートウェイ不達）を意味する。空のままだと「モデル: 」がぶら下がって読めないため、
 * 未送信であることを明示する。
 *
 * `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけなので、
 * プロパティ参照はここで局所変数へ落としてから渡す。
 */
function ModelFootnote({ answer }: { answer: AiAnswerDto }) {
  const model = answer.model;
  const inputTokens = answer.inputTokens ?? 0;
  const outputTokens = answer.outputTokens ?? 0;
  return (
    <p className="mt-2 text-xs text-[--color-fg-muted]">
      {model ? <Trans>モデル: {model}</Trans> : <Trans>モデル: 未使用（AI へ送信なし）</Trans>}
      {' / '}
      <Trans>
        入力 {inputTokens} ・出力 {outputTokens} トークン
      </Trans>
    </p>
  );
}

/**
 * 出典 1 件。
 *
 * 型は **`CitationDto`**（後段 `AiAnswerDto.Citations` の実体）である。#506 / IADR-0131 で
 * openapi.yaml の `AiAnswerDto.citations` を `SearchResultDto[]` から実体へ是正したため、
 * IADR-0127 決定 3 が採っていた「両者に共通するフィールドだけを使う」回避は**不要になった**。
 * 表示に使うのは引き続き `documentId` / `documentTitle` / `chunkId` だけである
 * ——hi-fi の出典表示（タイトルのリンクのみ）がそれ以上を求めないためで、回避の名残ではない。
 */
function CitationLink({ citation }: { citation: CitationDto }) {
  if (!citation.documentId) {
    return <span>{citation.documentTitle}</span>;
  }
  return (
    <Link
      to="/docs/$id"
      params={{ id: citation.documentId }}
      className="text-[--color-brand] hover:underline"
    >
      {citation.documentTitle}
    </Link>
  );
}
