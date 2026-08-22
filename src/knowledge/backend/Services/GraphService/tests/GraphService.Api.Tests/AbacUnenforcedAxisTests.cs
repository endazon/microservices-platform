using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-17, ADR-0034 決定 6・8・9, ADR-0036, IADR-0239 フォローアップ:
// 🔴 **現時点で強制できていない認可軸を固定する。**
//
// ADR-0034 決定 6・8・9（個人資料の境界）と ADR-0036（所有者ベースの裁量制御）は、
// 実装の現状では**機能しない**。理由は 3 つあり、いずれも GraphService の外にある:
//
//   1. 実データ上、必須とされる文書属性のうち `owner` / `department` / `lifecycle` が
//      0% 充足である（実効的な認可軸は `confidentiality` のみ。#516）
//   2. ADR-0036 が定めた `read` の 3 分岐 OR（属性ベース / 所有者ベース / 共有ベース）を
//      表現する構造が `AccessScopeResponse` に無い（複数ポリシーはキー単位の和で AND に畳まれる）
//   3. `/authz/scope` は `PolicyAction.Read` をサーバ側でハードコードしており、
//      `AccessScopeRequest` に Action フィールドが無い
//
// **本テストは「まだ強制していない」ことを明示的に固定するものである。**
// 上記が是正されると本テストは赤くなる —— それが狙いであり、そのとき初めて
// 「所有者で見え方が変わる」実装を足してよい。**赤くなったら消すのではなく、
// 強制されるようになったことを確かめる形へ書き換えること。**
public class AbacUnenforcedAxisTests
{
    // #516 / ADR-0036: owner だけが異なる 2 文書は、現状のスコープでは区別されない。
    [Fact]
    public void Owner_attribute_is_NOT_yet_enforced_documents_differing_only_by_owner_are_indistinguishable()
    {
        // 「自分が所有者」と「他人が所有者」。ADR-0036 D-02 が意図した判定なら差が出るはずである。
        var mine = GraphDocument.Create(Guid.NewGuid(), "mine",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "test-user" },
            null, DateTimeOffset.UtcNow);
        var theirs = GraphDocument.Create(Guid.NewGuid(), "theirs",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "someone-else" },
            null, DateTimeOffset.UtcNow);

        // 認可サービスが現実に返す形。owner の分岐はここに現れない。
        var scope = new AccessScopeResponse("test-user",
            [new AttributeFilter("confidentiality", ["internal"])], true);

        AbacNodeFilter.Matches(mine, scope).Should().BeTrue();
        AbacNodeFilter.Matches(theirs, scope).Should()
            .BeTrue("owner に基づく判定は現時点で強制されていない（#516）。"
                  + "ここが false になったら 3 分岐 OR が表現できるようになった合図であり、"
                  + "ADR-0034 決定 6・8・9 の実装に着手してよい");
    }

    // ADR-0036 D-03: 動的束縛（${current_user}）はスコープの語彙に存在しない。
    [Fact]
    public void Dynamic_binding_placeholders_are_NOT_interpreted()
    {
        var doc = GraphDocument.Create(Guid.NewGuid(), "d",
            new Dictionary<string, string> { ["owner"] = "test-user" },
            null, DateTimeOffset.UtcNow);

        // 仮に認可サービスが束縛前の文字列を返しても、述語は素の文字列比較しかしない。
        var scope = new AccessScopeResponse("test-user",
            [new AttributeFilter("owner", ["${current_user}"])], true);

        AbacNodeFilter.Matches(doc, scope).Should()
            .BeFalse("動的束縛は認可サービス側で解決されるべきものであり、"
                   + "述語がプレースホルダを解釈すると認可の判断が 2 箇所へ散る");
    }
}
