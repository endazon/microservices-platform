// FR-09, FR-16, SC-17, SC-12, ADR-0062 決定 2 (#2126): **集合値の利用者属性**（`tags` / `projects`）を
// 1 キー 1 値の契約へ載せるときの規則の、画面側の写し。
//
// 🔴 **正本は後段の契約プロジェクトの `UserAttributeEncoding`（C#）である。** 規則（対象キー・区切り・分割）を
// 変えるときは両方を同時に変える。契約は `Dictionary<string,string>` のままで、集合はカンマで連結して載せる ——
// Keycloak への書き込み・辞書照合・ABAC の評価・MCP の部分集合の判定はどれも、この規則で集合として読む。
// 🔴 `clearance` / `department` は単一値であり、ここへ足してはならない（連結すると階段ポリシーが壊れる）。

/** 集合として読むキー。**ここに無いキーは 1 値である。** */
const SET_VALUED_USER_ATTRIBUTE_KEYS: readonly string[] = ['tags', 'projects'];

/** 集合値を契約の線上表現へ連結するときの区切り。 */
const USER_ATTRIBUTE_SEPARATOR = ',';

export function isSetValuedUserAttribute(key: string): boolean {
  return SET_VALUED_USER_ATTRIBUTE_KEYS.includes(key.toLowerCase());
}

/** 線上表現を要素へ分割する（カンマ・空白・タブで切り、空を落とす。並びは入力のまま、重複は 1 つ）。 */
export function splitUserAttribute(value: string | undefined | null): string[] {
  const out: string[] = [];
  for (const part of (value ?? '').split(/[,\s]+/)) {
    const v = part.trim();
    if (v !== '' && !out.includes(v)) out.push(v);
  }
  return out;
}

/** 要素を線上表現へ連結する（空白だけの要素は落とす）。 */
export function joinUserAttribute(values: readonly string[]): string {
  return values
    .map((v) => v.trim())
    .filter((v) => v !== '')
    .join(USER_ATTRIBUTE_SEPARATOR);
}

/**
 * 既存の値へ 1 要素を足す。**集合値のキーなら和を取り、単一値のキーなら置き換える。**
 * 既に在る要素は足さない。
 */
export function addUserAttributeValue(
  key: string,
  current: string | undefined,
  value: string,
): string {
  if (!isSetValuedUserAttribute(key)) return value;
  const values = splitUserAttribute(current);
  return values.includes(value) ? joinUserAttribute(values) : joinUserAttribute([...values, value]);
}

/**
 * 値の要素のうち、許可値に無いもの（大文字小文字を無視して照合する。後段の `UserAssignmentValidation` と同じ）。
 * #2126: 辞書から外れた既存の値を黙って落とさず、画面に示すために使う。
 */
export function unknownUserAttributeValues(
  value: string | undefined | null,
  allowedValues: readonly string[],
): string[] {
  return splitUserAttribute(value).filter(
    (v) => !allowedValues.some((a) => sameUserAttributeValue(a, v)),
  );
}

/**
 * #2126（#2340 の再レビュー）: 要素の照合は**大文字小文字を区別しない**（後段の `UserAssignmentValidation` は
 * `StringComparer.OrdinalIgnoreCase`、`UserAttributeEncoding.Split` も同じ比較子）。画面の照合はすべてこれを使う。
 */
export function sameUserAttributeValue(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}
