import { useEffect, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Button,
  EmptyState,
  Label,
  LoadingState,
  Panel,
  Select,
  StatusBadge,
  Textarea,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import type { ConversionFigureDto } from '@foundation/api/generated/bff.schemas';
import { useFigureImageUrl, useJobFigures } from '../api/useConversionJobs';

// SC-07, UC-06, FR-12, #651: 人手補正の 2 ペイン編集（**Phase 1 = 図のコード化のやり直し**）。
//
// 計画（`05_screens:312`・2026-08-05 確定）:
//   「Phase 1 の 2 ペインは『**左＝図コード（Mermaid / PlantUML）のエディタ・右＝元の図画像
//    （画像保持中）**』である。対象は失敗した図ブロックであり、投稿するのはコード片である。」
//
// **変換結果 Markdown 全体の編集欄は置かない**（`05_screens:330` が Phase 2 へ繰り延べている。
// 本口〔`POST .../correction`〕もコード片しか受け付けない）。
//
// **権限の出し分けは呼び出し側が行う**。本部品は「開かれたら描く」ことに専念する——
// 権限判定を部品の内側へ入れると、テストが「出ない理由」を権限と対象の不在で切り分けられない。

/** 計画が挙げた図コードの言語（`05_screens:312`「Mermaid / PlantUML」）。 */
const LANGUAGES = ['mermaid', 'plantuml'] as const;

/**
 * 右ペイン: 元の図画像。
 *
 * `imageUri`（`storage://…`）は**ブラウザから解決できない**ので使わない。BFF の `/image` から
 * Blob で取り、オブジェクト URL にして表示する（[[IADR-0154]] 決定 2）。
 */
function FigureImagePane({ jobId, figureId }: { jobId: string; figureId: string }) {
  const { t } = useLingui();
  const image = useFigureImageUrl(jobId, figureId);

  // 🔴 **`QueryState` は使えない。** `useFigureImageUrl` が返すのは `UseQueryResult` ではなく
  // `{ url, isPending, isError }` である（Blob をオブジェクト URL へ変える段が挟まる）。
  // **形の合わないものを無理に通さず、同じ 3 部品を直接使って語彙と見た目だけを揃える。**
  if (image.isPending) {
    return <LoadingState label={t`画像を読み込み中…`} />;
  }
  // 404（コード化済み・未知の図・ストレージ未解決）は区別しない（[[IADR-0009]]）。
  // **空白にしない**——「読み込み中のまま止まった」と見分けが付かなくなる。
  // **`ErrorState` にはしない** —— 右ペインは補正作業の補助であり、左ペイン（コード編集）の
  // 妨げになる `role="alert"` を割り込ませる相手ではない（画像が無くてもコードは書ける）。
  if (image.isError || !image.url) {
    return <EmptyState title={t`元の画像を表示できません。`} />;
  }
  return (
    <img
      src={image.url}
      alt={t`補正対象の図の元画像`}
      className="max-w-full rounded border border-border"
    />
  );
}

/** 左ペイン ＋ 右ペイン。1 つの図に対する編集単位。 */
function FigureEditor({
  jobId,
  figure,
  submitting,
  onSubmit,
}: {
  jobId: string;
  figure: ConversionFigureDto;
  submitting: boolean;
  onSubmit: (input: { language: string; code: string }) => void;
}) {
  const { t } = useLingui();
  const [language, setLanguage] = useState<string>(figure.language ?? LANGUAGES[0]);
  const [code, setCode] = useState<string>(figure.code ?? '');

  // 図を切り替えたら編集内容を引き継がない（前の図のコードを別の図へ投稿してしまうため）。
  useEffect(() => {
    setLanguage(figure.language ?? LANGUAGES[0]);
    setCode(figure.code ?? '');
  }, [figure.figureId, figure.language, figure.code]);

  const figureId = figure.figureId;
  const caption = figure.caption;

  return (
    <div className="grid gap-3 md:grid-cols-2">
      {/* 左＝図コードのエディタ */}
      <div className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <Label htmlFor={`fig-lang-${figureId}`} className="shrink-0">
            <Trans>記法</Trans>
          </Label>
          <Select
            id={`fig-lang-${figureId}`}
            selectSize="sm"
            value={language}
            onChange={(e) => setLanguage(e.target.value)}
          >
            {LANGUAGES.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </Select>
          {/* INDEX 決定 21: 色だけで意味を持たせない（StatusBadge が tone ごとの固定アイコンを付ける）。 */}
          {figure.corrected && <StatusBadge tone="success">{t`補正あり`}</StatusBadge>}
        </div>
        <Textarea
          aria-label={t`図コード: ${figureId}`}
          rows={10}
          value={code}
          onChange={(e) => setCode(e.target.value)}
          className="font-mono text-xs"
        />
        <div>
          <Button
            type="button"
            size="sm"
            variant="primary"
            disabled={submitting || code.trim() === ''}
            onClick={() => onSubmit({ language, code })}
          >
            <Trans>補正して再登録</Trans>
          </Button>
        </div>
      </div>

      {/* 右＝元の図画像 */}
      <div className="flex flex-col gap-2">
        <span className="text-xs text-fg-muted">
          <Trans>元の図</Trans>
        </span>
        <FigureImagePane jobId={jobId} figureId={figureId} />
        {caption && <p className="text-xs text-fg-muted">{caption}</p>}
      </div>
    </div>
  );
}

export function FigureCorrectionPanel({
  jobId,
  submitting,
  onSubmit,
  onClose,
}: {
  jobId: string;
  submitting: boolean;
  onSubmit: (figureId: string, input: { language: string; code: string }) => void;
  onClose: () => void;
}) {
  const { t } = useLingui();
  const figures = useJobFigures(jobId);

  // **補正の対象は「コード化できなかった図」だけ**である（`coded === false`）。
  // コード化済みの図を混ぜると、Phase 1 の範囲（縮退のやり直し）を超える。
  const targets = (figures.data ?? []).filter((f) => !f.coded);

  return (
    <Panel heading={<Trans>人手補正（図のコード化）</Trans>}>
      <div className="mb-n2 flex justify-end">
        <Button type="button" size="sm" variant="secondary" onClick={onClose}>
          <Trans>閉じる</Trans>
        </Button>
      </div>
      {/* 待ち・空・失敗は `QueryState` の 1 本に統一する。**空は「補正の対象が無い」**であって
          失敗ではないので、再試行ではなく閉じる導線へ委ねる。 */}
      <QueryState
        query={figures}
        loadingLabel={t`図を読み込み中…`}
        errorTitle={t`図の一覧を取得できませんでした。`}
        isEmpty={() => targets.length === 0}
        empty={
          <EmptyState
            title={t`補正が必要な図はありません。`}
            description={t`このジョブの図はすべてコード化済みです。`}
          />
        }
      >
        {() => (
          <div className="flex flex-col gap-n4">
            {targets.map((figure) => (
              <FigureEditor
                key={figure.figureId}
                jobId={jobId}
                figure={figure}
                submitting={submitting}
                onSubmit={(input) => onSubmit(figure.figureId, input)}
              />
            ))}
          </div>
        )}
      </QueryState>
    </Panel>
  );
}
