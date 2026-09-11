import { useLingui } from '@lingui/react/macro';
import { Copy } from 'lucide-react';
import { notify } from '@foundation/ui/notifications';
import { IconButton } from './IconButton';

// 05_screens §共通シェル（右レール AI チャット）/ SC-01 / 裁定 6（Markdown 描画＋コピー）:
// 回答本文をクリップボードへ写す。
//
// **写すのは Markdown の原文である**（描画後の HTML ではない）。利用者がコピー先に貼るのは
// チャットや文書であり、見出しや箇条書きは Markdown のまま持ち運ぶほうが崩れない。
//
// 結果は `notify`（共通シェルのトースト。アイコン ＋ ラベル ＋ 本文）で伝える——ボタンの色や
// アイコンの一瞬の変化だけに載せない（INDEX 決定 21）。**失敗も伝える**（`navigator.clipboard` は
// 非 HTTPS・権限拒否・フォーカス外で reject する。黙って何も起きないのが最も分かりにくい）。

export interface CopyButtonProps {
  /** クリップボードへ写す本文。 */
  text: string;
  className?: string;
}

export function CopyButton({ text, className }: CopyButtonProps) {
  const { t } = useLingui();
  return (
    <IconButton
      label={t`回答をコピー`}
      icon={Copy}
      className={className}
      onClick={() => {
        navigator.clipboard.writeText(text).then(
          () => notify.success(t`コピーしました`),
          () => notify.error(t`コピーできませんでした`),
        );
      }}
    />
  );
}
