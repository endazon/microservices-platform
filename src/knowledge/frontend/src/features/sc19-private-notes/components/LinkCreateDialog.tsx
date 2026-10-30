import { useMemo, useRef, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  Select,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import type { PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import { useCreateLink, useLinkCandidates, useLinkEdgeTypes } from '../api/useLinkCreate';
import { linkFailureOf } from '../types/linkCreate';

// FR-17, SC-19, ADR-0034 決定 2・8, [[IADR-0655]] (#2343): **リンク作成 UI**（個人資料から他の文書へ、関連文書を明示的に付与する）。
//
// ■ 🔴 **補完候補は作成者の ABAC スコープ内に限る**（決定 8）。絞るのは後段（辺の作成と同じ read スコープ・同じ述語。
//   個人資料は自分のものだけ）。画面は**起点の資料（自分自身）を除くだけ**で、候補を推し量って足さない。件数は出さない（後段も返さない）。
// ■ 🔴 **失敗の文言**: 404 は「見つかりません。」だけ（権限外・不存在・書き込み権限なしを区別しない。「権限」の語を出さない）。
//   409 `edge_exists` と 400 `unknown_edge_type` は利用者が直せる理由なので言い分ける。
// ■ 成功したら両端の近傍（SC-18 の照会）を引き直させる（`useCreateLink`）。作った辺は SC-18 のグラフに出る。
// ■ 開いている間だけ描く（`ShareTargetsDialog` と同じ作法）。初期フォーカスは**リンクの種類**の選択へ置く
//   （型を先に決め、次に相手を探す順に並べてある）。

export interface LinkCreateDialogProps {
  note: PrivateNoteDto;
  onClose: () => void;
}

export function LinkCreateDialog({ note, onClose }: LinkCreateDialogProps) {
  const { t } = useLingui();
  const typeFieldRef = useRef<HTMLDivElement>(null);

  const edgeTypes = useLinkEdgeTypes();
  const create = useCreateLink();

  const [edgeTypeId, setEdgeTypeId] = useState('');
  const [term, setTerm] = useState('');
  const [picked, setPicked] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const lookup = useLinkCandidates(term);
  // 起点の資料（自分自身）は候補から除く（自分へのリンクは後段も 400 で拒む）。
  const candidates = useMemo(
    () => (lookup.query.data ?? []).filter((c) => c.documentId !== note.id),
    [lookup.query.data, note.id],
  );

  const failure = create.isError ? linkFailureOf(create.error) : null;
  const failureMessage =
    failure === 'notFound'
      ? t`見つかりません。`
      : failure === 'exists'
        ? t`このリンクはすでにあります。`
        : failure === 'unknownType'
          ? t`選んだリンクの種類は使えません。種類を選び直してください。`
          : failure === 'other'
            ? toMessages(
                create.error,
                t`リンクを作成できませんでした。時間をおいて再度お試しください。`,
              ).join(' ')
            : null;

  function submit() {
    if (picked === null || edgeTypeId === '') return;
    setDone(false);
    create.reset();
    create.mutate(
      { data: { sourceDocumentId: note.id, targetDocumentId: picked, edgeTypeId } },
      { onSuccess: () => setDone(true) },
    );
  }

  // ［2026-08-30 / #1078］翻訳文へ差し込む値は**単純な変数**として渡す。
  const noteTitle = note.title;

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <DialogContent
        initialFocus={() => typeFieldRef.current?.querySelector('select') ?? null}
        className="w-[min(560px,calc(100%-2rem))]"
      >
        <DialogTitle>{t`「${noteTitle}」からリンクを張る`}</DialogTitle>

        <p className="text-sm text-fg-muted">
          <Trans>
            リンク先に選べるのは、あなたが閲覧できる文書と、あなたの個人資料のうちナレッジグラフに表示しているものです。
          </Trans>
        </p>

        {done && (
          <Alert tone="success" label={t`完了`} role="status">
            {t`リンクを作成しました。`}
          </Alert>
        )}
        {failureMessage !== null && (
          <Alert tone="danger" label={t`エラー`} role="alert">
            {failureMessage}
          </Alert>
        )}

        <div ref={typeFieldRef} className="flex flex-col gap-1">
          <Label htmlFor="link-edge-type">{t`リンクの種類`}</Label>
          <QueryState
            query={edgeTypes}
            isEmpty={() => (edgeTypes.data ?? []).length === 0}
            loadingLabel={t`リンクの種類を読み込み中…`}
            errorTitle={t`リンクの種類を取得できませんでした。`}
            empty={<EmptyState title={t`使えるリンクの種類がありません。`} />}
          >
            {(types) => (
              <Select
                id="link-edge-type"
                selectSize="sm"
                value={edgeTypeId}
                onChange={(e) => setEdgeTypeId(e.target.value)}
              >
                <option value="">{t`選択してください`}</option>
                {types.map((type) => (
                  <option key={type.id} value={type.id}>
                    {type.name}
                  </option>
                ))}
              </Select>
            )}
          </QueryState>
        </div>

        <div className="flex flex-col gap-1">
          <Label htmlFor="link-target-search">{t`リンク先の文書を表題で探す`}</Label>
          <Input
            id="link-target-search"
            value={term}
            onChange={(e) => {
              setTerm(e.target.value);
              setPicked(null);
            }}
          />
          {/* 2 文字未満では問い合わせない。**なぜ候補が出ないのかを書く**（黙って空にしない）。 */}
          {!lookup.enabled && (
            <p className="text-xs text-fg-muted">
              <Trans>2 文字以上入力すると候補が出ます。</Trans>
            </p>
          )}
        </div>

        {lookup.enabled && (
          <QueryState
            query={lookup.query}
            isEmpty={() => candidates.length === 0}
            loadingLabel={t`文書を検索中…`}
            errorTitle={t`文書を検索できませんでした。`}
            empty={<EmptyState title={t`該当する文書がありません。`} />}
          >
            {() => (
              <fieldset
                role="radiogroup"
                className="flex max-h-48 flex-col gap-1 overflow-y-auto rounded-md border border-border p-2"
              >
                <legend className="sr-only">{t`リンク先の候補`}</legend>
                {candidates.map((candidate) => (
                  <label
                    key={candidate.documentId}
                    className="inline-flex items-center gap-2 text-sm"
                  >
                    <input
                      type="radio"
                      name="link-target"
                      checked={picked === candidate.documentId}
                      onChange={() => setPicked(candidate.documentId)}
                    />
                    <span>{candidate.title}</span>
                    {candidate.isPrivateNote && (
                      <span className="text-xs text-fg-muted">{t`（自分の個人資料）`}</span>
                    )}
                  </label>
                ))}
              </fieldset>
            )}
          </QueryState>
        )}

        <DialogActions>
          <DialogClose render={<Button variant="secondary" disabled={create.isPending} />}>
            {t`閉じる`}
          </DialogClose>
          <Button
            variant="primary"
            disabled={picked === null || edgeTypeId === '' || create.isPending}
            onClick={submit}
          >
            <Trans>リンクを作成</Trans>
          </Button>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}
