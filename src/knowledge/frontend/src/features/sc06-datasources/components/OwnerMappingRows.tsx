import { useId, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Button, Input, Label } from '@platform/ui';
import { UNRESOLVED_OWNER } from '../../../lib/abac';

// FR-05, UC-04, SC-06, ADR-0036, ADR-0074 決定 1 (#1194): `owner` の写像表の入力欄。
//
// 計画 ADR-0074 決定 1 は「`owner` の②の写像表は **SC-06 の登録・更新フォームが持つ**。
// データソース単位で『ソース側識別子 → 利用者識別子』の対を並べる欄とし、**既定属性 3 つと
// 同じ面・同じ権限**に置く」と定める。**新しい画面 ID も新しい権限も作らない。**
//
// 🔴 **登録フォームと既定属性フォームの両方がこれを使う。** 2 つ書くと片方が古くなる
// （`department` の登録側と更新側が実際に 2 段階（#767 → #1021）に分かれ、その間ずっと
// 「登録時にしか指定できない」状態が残った）。
//
// **候補の一覧は出さない。** 値域は IdP が持ち、実装が列挙すると退職者・新入社員のたびに
// 画面が古くなる。**入力の正しさは後段が名簿で検証する**（ADR-0074 決定 4）。

/** 画面上の 1 行。**空行を持てることが要件である**（「＋」で足してから入力する）。 */
export type OwnerMappingRow = {
  readonly key: string;
  readonly sourceId: string;
  readonly userId: string;
};

let rowSeq = 0;
const newRow = (sourceId = '', userId = ''): OwnerMappingRow => ({
  key: `omr-${(rowSeq += 1)}`,
  sourceId,
  userId,
});

// 保存済みの写像表を画面の行へ開く。**順序を安定させる**（辞書の列挙順に依存しない）。
// **公開しない** —— 唯一の呼び出し口は `useOwnerMappingRows` である（Knip が未使用 export を数える）。
function toRows(mappings: Record<string, string> | null | undefined): OwnerMappingRow[] {
  return Object.entries(mappings ?? {})
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .map(([sourceId, userId]) => newRow(sourceId, userId));
}

/*
 * 画面の行を送信する地図へ畳む。**公開しない**（呼び出し口は `useOwnerMappingRows` だけ）。
 *
 * 🔴 **両側とも空でない行だけを送る。** 空行は「まだ書いていない」であって「空の写像」ではない。
 * 片側だけ埋まった行は後段が 400 で弾くが、**画面から意図せず送らない**（管理者が「＋」を
 * 押しただけの行で保存が失敗するのは、入力の誤りではなく画面の落ち度である）。
 */
function toMappings(rows: readonly OwnerMappingRow[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (const row of rows) {
    const sourceId = row.sourceId.trim();
    const userId = row.userId.trim();
    if (sourceId.length === 0 || userId.length === 0) continue;
    result[sourceId] = userId;
  }
  return result;
}

export function OwnerMappingRows({
  rows,
  onChange,
  idPrefix,
}: {
  rows: readonly OwnerMappingRow[];
  onChange: (next: OwnerMappingRow[]) => void;
  /** 登録フォームと編集フォームが同時に描かれても id が衝突しないようにする。 */
  idPrefix: string;
}) {
  const { t } = useLingui();
  const hintId = `${idPrefix}-owner-map-hint`;

  const update = (key: string, patch: Partial<OwnerMappingRow>) =>
    onChange(rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));

  return (
    <div>
      <Label htmlFor={`${idPrefix}-owner-map-src-0`}>
        <Trans>所有者の写像（ソース側の利用者 → 基盤の利用者）</Trans>
      </Label>
      <p id={hintId} className="text-xs text-[--color-fg-muted]">
        {/* **「予約値」と書く** —— `system` は「解決できなかったことの記録」であって既定値ではない
            （`lib/abac/owner.ts`）。ただし件数を債務として数えるかは `owner` では別である。 */}
        <Trans>
          写像に無いソース側の利用者は、予約値 {UNRESOLVED_OWNER} になります。基盤の利用者は
          ログイン名で指定してください（存在しない利用者は保存できません）。
        </Trans>
      </p>

      <div className="mt-2 flex flex-col gap-2">
        {rows.map((row, index) => {
          // lingui/no-expression-in-message: メッセージへ埋められるのは**素の変数**だけである
          // （`index + 1` のような式は抽出時に名前を持てない。`DataSourceAttributesForm` と同じ罠）。
          const rowNumber = index + 1;
          return (
            <div key={row.key} className="flex items-center gap-2">
              <Input
                id={`${idPrefix}-owner-map-src-${index}`}
                value={row.sourceId}
                aria-label={t`ソース側の利用者 ${rowNumber}`}
                aria-describedby={hintId}
                placeholder={t`例: hr_system\\tanaka`}
                onChange={(e) => update(row.key, { sourceId: e.target.value })}
              />
              <span aria-hidden="true">→</span>
              <Input
                id={`${idPrefix}-owner-map-user-${index}`}
                value={row.userId}
                aria-label={t`基盤の利用者 ${rowNumber}`}
                placeholder={t`例: tanaka`}
                onChange={(e) => update(row.key, { userId: e.target.value })}
              />
              <Button
                type="button"
                aria-label={t`写像を削除 ${rowNumber}`}
                onClick={() => onChange(rows.filter((r) => r.key !== row.key))}
              >
                <Trans>削除</Trans>
              </Button>
            </div>
          );
        })}
      </div>

      <Button type="button" className="mt-2" onClick={() => onChange([...rows, newRow()])}>
        <Trans>＋ 写像を追加</Trans>
      </Button>
    </div>
  );
}

/** 行の状態を持つフック。**2 つのフォームが同じ初期化・同じ畳み方を使う**ために切り出す。 */
export function useOwnerMappingRows(initial?: Record<string, string> | null) {
  const [rows, setRows] = useState<OwnerMappingRow[]>(() => toRows(initial));
  const idPrefix = useId();
  return { rows, setRows, idPrefix, mappings: () => toMappings(rows) };
}
