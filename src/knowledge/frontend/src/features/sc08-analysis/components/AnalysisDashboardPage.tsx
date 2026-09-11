import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
// ADR-0031 §採用技術一覧「フォーム = React Hook Form / Zod / @hookform/resolvers」/ #788:
// 3 つの useState ＋ 手書きの送信可否判定を置き換える。検証規則の実体は
// `types/analysisFormSchema.ts`（Zod）が持ち、**文言はスキーマではなくここが持つ**
// （スキーマに日本語を書くと Lingui の抽出から外れる）。
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { Link } from '@tanstack/react-router';
import {
  Button,
  EmptyState,
  ErrorState,
  Input,
  Label,
  LoadingState,
  Note,
  Panel,
  Select,
  Textarea,
} from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/utils/apiErrors';
import { AnalysisTaskRequestTaskType } from '@foundation/api/generated/bff.schemas';
import type { AiAnswerDto, CitationDto } from '@foundation/api/generated/bff.schemas';
import {
  buildAnalysisRequest,
  MAX_INSTRUCTION_LENGTH,
  MAX_RANGE_QUERY_LENGTH,
  TASK_TYPES,
  taskTypeLabel,
} from '../types/analysisRange';
import { ANALYSIS_FORM_ERRORS, analysisFormSchema } from '../types/analysisFormSchema';
import type { AnalysisFormError, AnalysisFormValues } from '../types/analysisFormSchema';
import { useAnalysisTask } from '../api/useAnalysisTask';
import type { AnalysisOutcome } from '../api/useAnalysisTask';
import { FormDevTools } from './FormDevTools';
import { EMPTY_SELECTION, ScopeFilter } from '../../../lib/scope-filter';
import type { ScopeSelection } from '../../../lib/scope-filter';

// **［2026-09-12 / UI/UX 改善］hi-fi モック（sc-08）の構造へ寄せた。**
//   `.ttl`「AI分析依頼」＋`.sub`、`.g2`（2 列）の `Panel`「分析対象（権限内に限定）」／「分析内容」、
//   結果の `Panel`、末尾の `.note`。`Card` は**一覧に並ぶ独立した単位**の部品であり、
//   本文の**区画**はモックの `.panel`＝`Panel` である。
//
//   🔴 **三部品（待ち・空・失敗）は使うが、`QueryState` は使わない。** 本画面の結果は
//   **`useQuery` ではなく `useMutation`**（`/bff/analysis/analyze` は POST で、利用者の
//   「分析実行」が引き金である）であり、`QueryState` が受けるのは `UseQueryResult` である。
//   **形の合わないものを無理に通さず、同じ 3 部品（`LoadingState` / `EmptyState` /
//   `ErrorState`）を直接使って見た目と語彙だけを揃える** —— 統一したいのは
//   「待ち・空・失敗の描き分けと文言」であって、`QueryState` という部品そのものではない。
//   再試行の導線を付けないのも同じ理由である（押すべきは「分析実行」であり、
//   自動で投げ直すと**利用者が意図しない課金と送信**が起きる）。
//
// SC-08, UC-02, FR-07/FR-11/FR-05: AI分析ダッシュボード（05_screens: ルート /analyze）。
// 範囲を指定して分析（比較・抽出を含む）を依頼し、結果と出典を確認する。出典から SC-03 へ遷移する。
// ロール限定は無い（05_screens §共通シェル: 利用者グループは ABAC の権限内で全利用者が使える）。
//
// **［#539］分析対象のチップ（タグ・部門・プロジェクト）を実装した。**
// 従前ここには「planning#197 の裁定を待つ」と書いてあったが、裁定（2026-08-05 Q1・Q3・Q9）が着地し、
// 権限内候補の照会口も #540 で着地した。**「フォルダ」は保留ではなく不採用である**（Q9）。
// チップは SC-01 と同じ部品（`lib/scope-filter`）を使う——同じ操作が画面ごとに違うと、
// 利用者は操作を覚え直すことになる。

/** 検証エラーの符号を表示文言へ写す。**符号ごとに 1 文だけ**を持つ（画面ごとに言い回しを割らない）。 */
function useErrorText() {
  const { t } = useLingui();
  return (code: string | undefined, max: number): string | null => {
    if (!code) return null;
    // **符号の値域は `ANALYSIS_FORM_ERRORS` が正本である。** 素の型注釈（`code as AnalysisFormError`）で
    // 済ませると、スキーマに無い文字列も既知の符号として扱えてしまう。実在を照合してから分岐する。
    const known = ANALYSIS_FORM_ERRORS.find((c): c is AnalysisFormError => c === code);
    if (known === 'required') return t`入力してください。`;
    if (known === 'tooLong') return t`${max} 文字以内で入力してください。`;
    // 未知の符号は握り潰さない（スキーマを増やしたのに文言を足し忘れたことが見える）。
    return code;
  };
}

export function AnalysisDashboardPage() {
  const { t } = useLingui();
  const errorText = useErrorText();
  // FR-07, SC-08, #539: 分析対象のチップ（タグ・部門・プロジェクト）。
  // **チップは RHF の管理下に置かない**——値は配列の集合であり、`register` の対象になる
  // フォーム入力ではない（Controller で包んでも得るものが無い）。
  const [scope, setScope] = useState<ScopeSelection>(EMPTY_SELECTION);
  const { outcome, run } = useAnalysisTask();

  const {
    control,
    register,
    handleSubmit,
    formState: { errors, isValid },
  } = useForm<AnalysisFormValues>({
    resolver: zodResolver(analysisFormSchema),
    // 打鍵のたびに赤くしない。**1 度触れてから**（blur）検証し、以後は入力に追随させる。
    mode: 'onTouched',
    defaultValues: {
      instruction: '',
      taskType: AnalysisTaskRequestTaskType.Analyze,
      rangeQuery: '',
    },
  });

  const running = outcome.kind === 'running';
  const canSubmit = isValid && !running;

  return (
    <section>
      {/* hi-fi `.ttl` / `.sub`。 */}
      <h1 className="text-[17px] font-medium text-fg">
        <Trans>AI分析依頼</Trans>
      </h1>
      <p className="mb-n4 text-xs text-fg-muted">
        <Trans>範囲指定の分析依頼・結果・出典</Trans>
      </p>

      {/* SC-08: 分析対象の指定（チップ）。**候補は権限内に限る**（#540 の口）。 */}
      <div className="mb-n3">
        <ScopeFilter selection={scope} onChange={setScope} disabled={running} />
      </div>

      {/* hi-fi の `.g2`（2 列）。 */}
      <form
        className="grid gap-n3 md:grid-cols-2"
        onSubmit={handleSubmit((values) => {
          if (running) return;
          run(buildAnalysisRequest(values.instruction, values.taskType, values.rangeQuery, scope));
        })}
      >
        {/* 「（権限内に限定）」は存在秘匿の説明そのものであり省略しない。 */}
        <Panel className="mb-0" heading={<Trans>分析対象（権限内に限定）</Trans>}>
          <div>
            <Label htmlFor="range-query">
              <Trans>検索条件で追加</Trans>
            </Label>
            <Input
              id="range-query"
              maxLength={MAX_RANGE_QUERY_LENGTH}
              aria-invalid={errors.rangeQuery ? true : undefined}
              placeholder={t`省略時は分析内容を流用します`}
              {...register('rangeQuery')}
            />
            <FieldError text={errorText(errors.rangeQuery?.message, MAX_RANGE_QUERY_LENGTH)} />
          </div>
        </Panel>

        <Panel className="mb-0" heading={<Trans>分析内容</Trans>}>
          <div className="flex flex-col gap-n3">
            <div>
              <Label htmlFor="instruction" requiredHint={t`（必須）`}>
                <Trans>分析内容（指示）</Trans>
              </Label>
              <Textarea
                id="instruction"
                rows={3}
                maxLength={MAX_INSTRUCTION_LENGTH}
                aria-invalid={errors.instruction ? true : undefined}
                placeholder={t`例: Q1の営業報告を部門別に比較し、共通する失注要因を抽出して`}
                {...register('instruction')}
              />
              <FieldError text={errorText(errors.instruction?.message, MAX_INSTRUCTION_LENGTH)} />
            </div>
            <div>
              {/* FR-07「分析・比較・抽出」。選択肢が無いと比較・抽出へ到達できない。 */}
              <Label htmlFor="task-type" requiredHint={t`（必須）`}>
                <Trans>タスク種別</Trans>
              </Label>
              {/* `Controller` で包むのは、`Select` が `@platform/ui` のプリミティブであり
                  `register` の ref 転送に依存させたくないためである（プリミティブ側の
                  実装詳細にフォームの動作を結び付けない）。 */}
              <Controller
                control={control}
                name="taskType"
                render={({ field }) => (
                  <Select id="task-type" {...field}>
                    {TASK_TYPES.map((type) => (
                      <option key={type} value={type}>
                        {i18n._(taskTypeLabel(type))}
                      </option>
                    ))}
                  </Select>
                )}
              />
            </div>
            <div>
              <Button type="submit" variant="primary" disabled={!canSubmit}>
                <Trans>分析実行</Trans>
              </Button>
            </div>
          </div>
        </Panel>
      </form>

      {/* 開発時のみ RHF DevTools を載せる（ADR-0031 の「RHF DevTools」。本番の初期ロードへは入れない）。 */}
      <FormDevTools control={control} />

      {/* 待ち・空・失敗・本体を 1 か所で描き分ける（`QueryState` と同じ判定順・同じ 3 部品。
          冒頭コメントのとおり、本画面の結果は照会ではなく**利用者が引いた操作**の結果である）。 */}
      <div className="mt-n3">
        <AnalysisOutcomeView outcome={outcome} />
      </div>

      {/* hi-fi の `.note`（05_screens §SC-08 の注記）。静的な注記なので role は付けない。
          **`Alert` から `Note` へ寄せた** —— `Alert` は「いま起きたこと」の器であり、
          常にそこに在る補足を同じ強さで描くと、本当の通知が埋もれる。 */}
      <Note>
        <Trans>
          機密区分の高いデータは外部 API へ送信せずセルフホスト LLM で処理します（UC-02 代替フロー・
          データ越境ポリシー）。権限外のデータは黙って対象外になります（存在秘匿）。
        </Trans>
      </Note>
    </section>
  );
}

/**
 * 分析結果の**待ち・空・失敗・本体**の描き分け。
 *
 * 判定順は `QueryState` と同じ **失敗 → 待ち → 空 → 本体** である。**0 件と失敗を混同しない**
 * —— 失敗を「該当なし」へ寄せると、利用者は送り直せば成功し得ることに気づけない
 * （`useAnalysisTask` の `isHiddenByPolicy` が 403 / 404 だけを中立へ寄せているのと対になる）。
 *
 * **失敗に再試行ボタンを置かない。** 押すべきは「分析実行」であり、
 * 部品が勝手に投げ直すと利用者が意図しない送信（と課金）が起きる。
 */
function AnalysisOutcomeView({ outcome }: { outcome: AnalysisOutcome }) {
  const { t } = useLingui();
  if (outcome.kind === 'failed') {
    return (
      <ErrorState
        title={t`分析を実行できませんでした。`}
        description={toMessages(outcome.error, t`分析を実行できませんでした。`).join(' / ')}
      />
    );
  }
  if (outcome.kind === 'running') {
    // 応答はストリーミングではなく 1 回の POST なので、出せる進行はここまでである
    // （途中経過を持たない相手に偽の進捗を描かない）。
    return <LoadingState label={t`分析を実行中…`} />;
  }
  if (outcome.kind === 'empty') {
    // UC-02 例外フロー: 権限外は対象から除外し、権限の有無を開示しない（存在秘匿）。
    // 空回答・403・404 をすべて同じ中立文言へ寄せる。
    return (
      <EmptyState
        title={t`該当する情報が見つかりませんでした。`}
        description={t`対象範囲を広げるか、分析内容を書き換えて実行してください。`}
      />
    );
  }
  if (outcome.kind === 'idle') return null;

  const answer = outcome.answer;
  return (
    <Panel heading={<Trans>結果</Trans>}>
      <p className="whitespace-pre-wrap text-sm">{answer.answer}</p>

      {(answer.citations ?? []).length > 0 && (
        <p className="mt-n2 flex flex-wrap items-center gap-n2 text-sm text-fg-muted">
          <Trans>出典:</Trans>
          {(answer.citations ?? []).map((citation, index) => (
            <CitationLink key={citation.chunkId ?? index} citation={citation} />
          ))}
        </p>
      )}

      {/* FR-11（モデル振り分けの利用面）/ IADR-0111: model が空なのは「AI へ送信していない縮退」
          （ABAC 不許可・機密区分による送信拒否・ゲートウェイ不達）を意味する。空のままだと
          「モデル: 」がぶら下がって読めないため、未送信であることを明示する。 */}
      <ModelFootnote answer={answer} />
    </Panel>
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
    <p className="mt-2 text-xs text-fg-muted">
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
      className="text-brand hover:underline"
    >
      {citation.documentTitle}
    </Link>
  );
}

/** 項目の検証エラー。**色だけで意味を持たせない**（INDEX 決定 21）——文言そのものを出す。 */
function FieldError({ text }: { text: string | null }) {
  if (!text) return null;
  return (
    <p role="alert" className="mt-1 text-xs text-danger">
      {text}
    </p>
  );
}
