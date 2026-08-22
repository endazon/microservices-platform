import { create } from 'zustand';

// 05_screens §共通シェル「AIチャットパネル（右レール）… **画面別履歴（画面ごとの保持／全消去）**」/
// ADR-0031 §採用技術一覧「クライアント状態 = Zustand」/ IADR-0121 決定 1 の第 4 段（#788）。
//
// **なぜ Zustand か（＝なぜ TanStack Query ではないか）。**
// ここが持つのは「どの画面で何を話したか」と「パネルが開いているか」であり、**サーバーが持つ状態ではない**。
// BFF（`docs/api/openapi.yaml` の `/bff/` 配下）に会話履歴の取得口は無い（実測 2026-08-23）。
// サーバー状態は TanStack Query に一元化する（ADR-0031）という規則は、**サーバーに在る状態**の話であり、
// 出所の無い状態を Query のキャッシュへ載せると「取得できないのに古くなる」矛盾した箱になる。
//
// **なぜ React の state ではないか。** ランチャー（共通シェルのヘッダ）とパネル本体、および
// 画面遷移をまたいで同じ履歴を読む必要があり、持ち回すと共通シェルの各所へ props が伸びる。
//
// **Redux は不採用**（ADR-0031。`src/eslint.config.js` が import を error にする）。

/** 1 往復（質問と、確定した回答）。ストリーム中の途中経過はここへ入らない。 */
export interface AiChatTurn {
  /** 一意キー。確定時に採番する（`answerId` は BFF が返さないこともあるため独立に持つ）。 */
  id: string;
  question: string;
  answer: string;
  /** BFF が `done` で返す回答 ID。フィードバック（FR-08）の紐付け先。未返却なら null。 */
  answerId: string | null;
}

interface AiChatState {
  /** 右レールが開いているか。既定は閉じている（画面の主領域を最初から狭めない）。 */
  open: boolean;
  /** 画面キー（ルートの pathname）ごとの履歴。 */
  historyByScreen: Record<string, AiChatTurn[]>;
  openPanel: () => void;
  closePanel: () => void;
  togglePanel: () => void;
  /** 1 往復を確定して積む。 */
  appendTurn: (screenKey: string, turn: AiChatTurn) => void;
  /** その画面の履歴だけを消す（計画の「画面ごとの保持」の裏返し）。 */
  clearScreen: (screenKey: string) => void;
  /** 全画面の履歴を消す（計画の「全消去」）。 */
  clearAll: () => void;
}

export const useAiChatStore = create<AiChatState>((set) => ({
  open: false,
  historyByScreen: {},
  openPanel: () => set({ open: true }),
  closePanel: () => set({ open: false }),
  togglePanel: () => set((s) => ({ open: !s.open })),
  appendTurn: (screenKey, turn) =>
    set((s) => ({
      historyByScreen: {
        ...s.historyByScreen,
        [screenKey]: [...(s.historyByScreen[screenKey] ?? []), turn],
      },
    })),
  clearScreen: (screenKey) =>
    set((s) => {
      // キーごと落とす（空配列を残すと「消したのに履歴がある」と読める状態が残る）。
      const next = { ...s.historyByScreen };
      delete next[screenKey];
      return { historyByScreen: next };
    }),
  clearAll: () => set({ historyByScreen: {} }),
}));

/** 画面キーの履歴（未登録の画面は空配列）。**呼び出しごとに新しい配列を作らない**——
 *  作ると Zustand の等価判定が毎回外れ、パネルが無限に再描画される。 */
const EMPTY_HISTORY: readonly AiChatTurn[] = [];

export function selectHistory(state: AiChatState, screenKey: string): readonly AiChatTurn[] {
  return state.historyByScreen[screenKey] ?? EMPTY_HISTORY;
}
