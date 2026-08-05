import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
  Tag,
} from '@platform/ui';
import { Alert } from '@platform/ui';
import {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from '../abac/confidentiality';
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';

// SC-05, UC-03, FR-06/FR-09: 文書の登録／編集フォーム（hi-fi の「編集フォーム」パネル）。
// 1 つの部品で新規登録と編集の両方を担う——項目（タイトル・機密区分・タグ）が同一であり、
// 別々に書くと片方だけバリデーションが漏れる。編集時のみ版と変更メモを持つ。
//
// UC-03 例外フロー「必須属性が未設定の場合は保存を拒否する」を、必須項目が埋まるまで
// 保存ボタンを無効にする形で満たす（サーバも 400 で拒否する。多層防御）。

const MAX_TITLE = 200;
const MAX_CHANGE_NOTE = 200;

export interface DocumentFormValues {
  title: string;
  attributes: Record<string, string>;
  tags: string[];
  changeNote: string | null;
}

export function DocumentForm({
  editing,
  submitting,
  onSubmit,
  onCancel,
}: {
  /** 編集対象。`null` なら新規登録。 */
  editing: DocumentDto | null;
  submitting: boolean;
  onSubmit: (values: DocumentFormValues) => void;
  onCancel: () => void;
}) {
  const { t } = useLingui();
  // 編集対象が変わったらフォームを作り直す（呼び出し側が key を渡す）。
  const [title, setTitle] = useState(editing?.title ?? '');
  const [confidentiality, setConfidentiality] = useState(
    editing?.attributes?.[CONFIDENTIALITY_KEY] ?? DEFAULT_CONFIDENTIALITY,
  );
  const [tags, setTags] = useState<string[]>(editing?.tags ?? []);
  const [tagDraft, setTagDraft] = useState('');
  const [changeNote, setChangeNote] = useState('');

  const canSubmit = title.trim().length > 0 && !submitting;
  // `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである
  // （`editing.version` のようなプロパティ参照はカタログの ID を壊す）。
  const version = editing?.version;

  function addTag() {
    const value = tagDraft.trim();
    // 空・重複は足さない（重複したタグは意味を持たず、削除操作も曖昧になる）。
    if (!value || tags.includes(value)) return;
    setTags([...tags, value]);
    setTagDraft('');
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          {editing ? (
            <Trans>文書を編集（v{version}）</Trans>
          ) : (
            <Trans>文書を登録</Trans>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <form
          aria-label={editing ? t`文書編集` : t`文書登録`}
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!canSubmit) return;
            onSubmit({
              title: title.trim(),
              // 既存の属性を保ったまま機密区分だけ差し替える（部門などを落とさない）。
              attributes: { ...editing?.attributes, [CONFIDENTIALITY_KEY]: confidentiality },
              tags,
              changeNote: changeNote.trim() || null,
            });
          }}
        >
          <div>
            <Label htmlFor="doc-title" requiredHint={t`（必須）`}>
              <Trans>タイトル</Trans>
            </Label>
            <Input
              id="doc-title"
              value={title}
              maxLength={MAX_TITLE}
              onChange={(e) => setTitle(e.target.value)}
            />
          </div>

          <div>
            {/* 05_screens §SC-05: 機密区分（ABAC属性）は必須・**定義済み区分のみ**。
                値そのものは翻訳しない（表示名が計画に無い値がある。abac/confidentiality.ts 参照）。 */}
            <Label htmlFor="doc-conf" requiredHint={t`（必須）`}>
              <Trans>機密区分（ABAC属性）</Trans>
            </Label>
            <Select
              id="doc-conf"
              value={confidentiality}
              onChange={(e) => setConfidentiality(e.target.value)}
            >
              {CONFIDENTIALITY_VALUES.map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </Select>
          </div>

          <div>
            <Label htmlFor="doc-tag">
              <Trans>タグ</Trans>
            </Label>
            {tags.length > 0 && (
              <span className="mb-1 flex flex-wrap gap-1">
                {tags.map((tag) => (
                  <span key={tag} className="inline-flex items-center gap-1">
                    <Tag tone="neutral">{tag}</Tag>
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      aria-label={t`タグ ${tag} を削除`}
                      onClick={() => setTags(tags.filter((x) => x !== tag))}
                    >
                      ✕
                    </Button>
                  </span>
                ))}
              </span>
            )}
            <span className="flex gap-2">
              <Input
                id="doc-tag"
                value={tagDraft}
                maxLength={100}
                onChange={(e) => setTagDraft(e.target.value)}
              />
              <Button type="button" onClick={addTag} disabled={tagDraft.trim().length === 0}>
                <Trans>追加</Trans>
              </Button>
            </span>
          </div>

          {editing && (
            <div>
              <Label htmlFor="doc-note">
                <Trans>変更メモ</Trans>
              </Label>
              <Input
                id="doc-note"
                value={changeNote}
                maxLength={MAX_CHANGE_NOTE}
                onChange={(e) => setChangeNote(e.target.value)}
              />
            </div>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" variant="primary" disabled={!canSubmit}>
              <Trans>保存</Trans>
            </Button>
            <span className="text-xs text-[--color-fg-muted]">
              <Trans>→ 取り込み・Wiki同期をトリガ</Trans>
            </span>
            {editing && (
              <Button type="button" onClick={onCancel}>
                <Trans>キャンセル</Trans>
              </Button>
            )}
          </div>
        </form>

        {/* 05_screens §SC-05 の注記。静的な注記なので role は付けない。 */}
        <Alert tone="info" className="mt-3" label={t`注記`}>
          <Trans>必須属性が未設定の場合は保存を拒否します（UC-03 例外フロー）。</Trans>
        </Alert>
      </CardContent>
    </Card>
  );
}
