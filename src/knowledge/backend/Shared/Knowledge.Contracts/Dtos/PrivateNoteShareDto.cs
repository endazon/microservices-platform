namespace Knowledge.Contracts.Dtos;

// FR-19, UC-11, SC-19 主要素 3, ADR-0036 D-06, ADR-0098 決定 1・2, planning#618:
// 公開範囲の指定先（共有台帳）の契約 DTO。**BFF（Knowledge.Bff.Endpoints）が画面へ配る面の型**である。
//
// 後段 DocumentService の `/documents/{id}/shares*`（共有台帳の管理 API）の応答・要求と一致させる。
// 従前は DocumentService の内部型（`Features/Documents/DocumentShareEndpoints.cs`）であったが、
// BFF が画面へ配る面に出た時点で契約へ移した（名前・項目は変えていない。後段はこの型を使う）。
//
// 🔴 **画面が扱うのは個人指定（`subjectType=user`）だけである**（ADR-0098 決定 2）。台帳と本契約は
// `group` も受け付けるが、`${current_groups}` の束縛が未配備の間、グループ共有は誰にも何も許可しない
// （台帳に入るが評価されない。fail-closed）。**受け付けをやめる改修は行わない** —— 配線が入ったときに
// 再び開く作業が生じる（同 決定 2）。画面に導線が無い以上、通常の利用者は到達しない。
//
// 🔴 **`SubjectId` は取り消しの鍵であり、画面には出さない。** 表示名は `UserSummaryDto`
// （`/bff/users/resolve`）で引く（ADR-0098 決定 1「画面には表示名を出し、識別子は出さない」）。

// FR-19, SC-19 主要素 3: 共有先 1 件（一覧と付与の両方が返す）。`GrantedBy` は付与した所有者。
public record DocumentShareDto(string SubjectType, string SubjectId, string GrantedBy,
    DateTimeOffset CreatedAt);

// FR-19, SC-19 主要素 3: 指定先の追加。`SubjectType` の値集合は `ShareSubjectTypes`。
public record CreateShareRequest(string SubjectType, string SubjectId);

// ADR-0036 D-06, ADR-0098 決定 1: 共有先の種別（契約の値集合。enum にしない。IADR-0131 論点 C）。
// `User` の識別子は `${current_user}` と同じ利用者識別子、`Group` は Keycloak グループ識別子。
public static class ShareSubjectTypes
{
    public const string User = "user";
    public const string Group = "group";
}
