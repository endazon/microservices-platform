import type {
  AttributeDefinitionDto,
  PlatformUserDto,
  UserDepartmentDto,
} from '@foundation/api/generated/bff.schemas';

// SC-17, UC-05, FR-05, FR-09: 本画面の語彙と入力規則（純関数）。
//
// **判定を DOM から切り離してある。** 値集合と必須判定そのものを描画なしで試験するためである
// （#503 の変異試験が「値集合から 1 値落としても画面テストは 1 件も落ちない」ことを実測した。
// IADR-0129 決定 6）。

/**
 * 計画 05_screens §SC-17:「ABAC属性（機密区分上限）＝**必須**／（タグ）＝**任意**」。
 *
 * 🔴 **値域の正本はサーバ側**（`UserAssignmentValidation.RequiredUserAttributeKeys`）である。
 * ここに写しを置くのは、送る前に画面で理由を示すためだけであり、**認可の実効境界ではない**。
 * この写しが緩んでも後段が 400 で断るので**漏れる向きには壊れない**
 * （逆に、ここを厳しくしすぎると保存できない画面になる ——
 * だから必須は増やさず、計画が名指ししたキーに閉じる）。
 *
 * ［2026-09-27 / #1610・計画 ADR-0116 決定 1］🔴 **部門は必須から外した。** 部門は属性ではなく部門グループの所属で変え、
 * 「部門なし」も選べる（すべての部門グループから外す）。
 */
export const REQUIRED_ATTRIBUTE_KEYS = ['clearance'] as const;

/**
 * 一覧の「部門」列は ABAC 属性 `department` そのものである（別項目として複写しない）。
 * ［#1610］部門欄（編集）は属性ではなく部門グループの所属を扱う（`UserDepartmentDto`）。属性は部門の同期が追いつく。
 */
export const DEPARTMENT_KEY = 'department';

/**
 * 属性の差し替えの下書きから部門を落とす（#1610・計画 ADR-0116 決定 1）。
 * 🔴 **SC-17 の経路から属性 `department` を書く要求を出さない**（後段も 400 で拒む）。
 */
export function withoutDepartment(
  attributes: Readonly<Record<string, string>>,
): Record<string, string> {
  return Object.fromEntries(
    Object.entries(attributes).filter(([key]) => key.toLowerCase() !== DEPARTMENT_KEY),
  );
}

/**
 * 部門欄の状態（#1610）。**色だけで意味を持たせない**（画面は `StatusBadge` で色 ＋ アイコン ＋ 文言を出す）。
 * - `synced`: 属性が部門グループに追随済み（部門なしで属性も無いを含む）
 * - `pending`: 属性がまだ部門グループと違う（部門の同期が追いつく）
 * - `multiple`: 部門グループが 2 つ以上（この画面では変えない。後段も 409 で拒む）
 */
export type DepartmentState = 'synced' | 'pending' | 'multiple';

export function departmentState(department: UserDepartmentDto): DepartmentState {
  const groups = department.departmentGroups;
  if (groups.length > 1) return 'multiple';
  const attribute = department.departmentAttribute ?? null;
  return (groups[0] ?? null) === attribute ? 'synced' : 'pending';
}

/**
 * いまの部門（部門欄の初期値）。`''` は部門なし、`null` は 2 つ以上で 1 つに決められない。
 */
export function currentDepartment(department: UserDepartmentDto): string | null {
  const groups = department.departmentGroups;
  if (groups.length > 1) return null;
  return groups[0] ?? '';
}

/**
 * 部門の変更の要求を送るか。**いまの所属から変えたときだけ送る**（変えていない保存で所属に触れない。
 * 2 つ以上の人には送らない）。`draft` が null なら部門欄は触られていない。
 */
export function departmentChangeToSend(
  department: UserDepartmentDto | undefined,
  draft: string | null,
): string | null | undefined {
  if (!department || draft === null) return undefined;
  const current = currentDepartment(department);
  if (current === null || current === draft) return undefined;
  return draft === '' ? null : draft;
}

/**
 * 割当に使える辞書項目。
 *
 * **利用者スコープの属性だけを出す。** 文書スコープの属性（文書側に付く値）を利用者へ
 * 割り当てると意味が反転する。許可値を持たない項目も出さない（選べる値が無い項目を
 * 選択肢に置かない）。
 *
 * 🔴 **画面へ値集合を焼き込まない。** 焼き込むと辞書を増やしても選べず、逆に辞書から
 * 消えた値を選べてしまう。値域の正は辞書側（SC-09 の属性体系）であり、画面は引くだけである。
 *
 * ［#1610］🔴 **`department` は出さない。** 部門は属性の割当ではなく部門グループの所属で変える（部門欄は別に描く）。
 */
export function assignableAttributes(
  definitions: readonly AttributeDefinitionDto[],
): AttributeDefinitionDto[] {
  return definitions.filter(
    (d) =>
      d.scope === 'user' && d.allowedValues.length > 0 && d.key.toLowerCase() !== DEPARTMENT_KEY,
  );
}

/** 必須の属性（機密区分上限）だけを、辞書に定義されている順で返す。 */
export function requiredAttributes(
  definitions: readonly AttributeDefinitionDto[],
): AttributeDefinitionDto[] {
  return assignableAttributes(definitions).filter((d) =>
    (REQUIRED_ATTRIBUTE_KEYS as readonly string[]).includes(d.key),
  );
}

/** 任意の属性（計画の「タグ」に当たる）。**必須集合の補集合であり、独立に列挙しない。** */
export function optionalAttributes(
  definitions: readonly AttributeDefinitionDto[],
): AttributeDefinitionDto[] {
  return assignableAttributes(definitions).filter(
    (d) => !(REQUIRED_ATTRIBUTE_KEYS as readonly string[]).includes(d.key),
  );
}

/** 一覧のフィルタ条件。`''` は「すべて」。 */
export interface UserFilter {
  department: string;
  role: string;
}

/** 05_screens §SC-17 主要素 1:「部門／ロールのフィルタ」。両方指定なら AND。 */
export function filterUsers(
  users: readonly PlatformUserDto[],
  filter: UserFilter,
): PlatformUserDto[] {
  return users.filter(
    (u) =>
      (filter.department === '' || u.attributes[DEPARTMENT_KEY] === filter.department) &&
      (filter.role === '' || u.roles.includes(filter.role)),
  );
}

/** フィルタの選択肢は**実データから引く**（辞書に在っても誰も所属していない部門は出さない）。 */
export function departmentsInUse(users: readonly PlatformUserDto[]): string[] {
  return [
    ...new Set(
      users.map((u) => u.attributes[DEPARTMENT_KEY]).filter((d): d is string => Boolean(d)),
    ),
  ].sort();
}

/** 入力規則を満たさない理由の識別子（**文言は持たない**。呼び出し側が写す）。 */
export type AssignmentIssue = 'roles-required' | 'required-attribute-missing';

/**
 * 保存前の検証（満たさない理由の識別子を返す。空なら妥当）。
 *
 * - ロール割当は**必須**（複数選択・併任可）。空集合は「権限を全部剥がす」であって未入力ではない。
 * - 機密区分上限は**必須**。**タグは任意**（過剰拒否をしない）。部門は必須ではない（#1610。「部門なし」を選べる）。
 *
 * `definitions` は辞書であり、**辞書に無い必須キーは「入力できない」ので理由に数えない**
 * （画面が出せない項目を「未入力」と責めない。辞書側の未整備は後段が 400 で述べる）。
 */
export function validateAssignment(input: {
  roles: readonly string[];
  attributes: Readonly<Record<string, string>>;
  definitions: readonly AttributeDefinitionDto[];
}): AssignmentIssue[] {
  const issues: AssignmentIssue[] = [];
  if (input.roles.length === 0) issues.push('roles-required');

  const missing = requiredAttributes(input.definitions).some(
    (d) => !input.attributes[d.key] || input.attributes[d.key].trim().length === 0,
  );
  if (missing) issues.push('required-attribute-missing');

  return issues;
}
