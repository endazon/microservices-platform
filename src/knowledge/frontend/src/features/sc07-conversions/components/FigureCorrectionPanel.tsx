import { useEffect, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Label,
  Select,
  StatusBadge,
  Textarea,
} from '@platform/ui';
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

  if (image.isPending) {
    return (
      <p role="status" className="text-sm text-[--color-fg-muted]">
        <Trans>画像を読み込み中…</Trans>
      </p>
    );
  }
  // 404（コード化済み・未知の図・ストレージ未解決）は区別しない（[[IADR-0009]]）。
  // **空白にしない**——「読み込み中のまま止まった」と見分けが付かなくなる。
  if (image.isError || !image.url) {
    return (
      <p className="text-sm text-[--color-fg-muted]">
        <Trans>元の画像を表示できません。</Trans>
      </p>
    );
  }
  return (
    <img
      src={image.url}
      alt={t`補正対象の図の元画像`}
      className="max-w-full rounded border border-[--color-border]"
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
        <span className="text-xs text-[--color-fg-muted]">
          <Trans>元の図</Trans>
        </span>
        <FigureImagePane jobId={jobId} figureId={figureId} />
        {caption && <p className="text-xs text-[--color-fg-muted]">{caption}</p>}
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
    <Card className="mb-3">
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <CardTitle>
          <Trans>人手補正（図のコード化）</Trans>
        </CardTitle>
        <Button type="button" size="sm" variant="secondary" onClick={onClose}>
          <Trans>閉じる</Trans>
        </Button>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {figures.isPending && (
          <p role="status" className="text-sm text-[--color-fg-muted]">
            <Trans>図を読み込み中…</Trans>
          </p>
        )}
        {figures.isError && (
          <Alert tone="danger" role="alert" label={t`エラー`}>
            <Trans>図の一覧を取得できませんでした。</Trans>
          </Alert>
        )}
        {figures.isSuccess &&
          (targets.length === 0 ? (
            <p className="text-sm">
              <Trans>補正が必要な図はありません。</Trans>
            </p>
          ) : (
            targets.map((figure) => (
              <FigureEditor
                key={figure.figureId}
                jobId={jobId}
                figure={figure}
                submitting={submitting}
                onSubmit={(input) => onSubmit(figure.figureId, input)}
              />
            ))
          ))}
      </CardContent>
    </Card>
  );
}
