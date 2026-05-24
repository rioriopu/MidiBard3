// Copyright (C) 2022 akira0245
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
//
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System.Linq;

using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace MidiBard.Managers.Ipc;

public static class PartyListExtensions
{
    public static IPartyMember? GetMeAsPartyMember(this IPartyList partyList) => partyList.IsInParty() ? partyList.FirstOrDefault(i => i.ContentId == api.Player.ContentId) : null;
    public static IPartyMember? GetPartyLeader(this IPartyList partyList) => partyList.IsInParty() ? partyList[(int)partyList.PartyLeaderIndex] : null;
    public static bool IsInParty(this IPartyList partyList) => partyList?.Length > 1;
    public static bool IsPartyLeader(this IPartyMember member) => api.PartyList.IsInParty() && member != null && member.ContentId == api.PartyList.GetPartyLeader()?.ContentId;
    public static bool IsPartyLeader(this IPartyList partyList) => partyList.IsInParty() && api.Player.ContentId == partyList.GetPartyLeader()?.ContentId;
    public static IPartyMember? GetPartyMemberFromCid(this IPartyList partyList, ulong cid) => partyList.FirstOrDefault(i => i.ContentId == cid);
    public static string NameAndWorld(this IPartyMember member) => $"{member?.Name}·{member?.World.ValueNullable?.Name.ToDalamudString().TextValue}";

    public static (ulong Cid, string Name, string World) GetPartyMemberData(this IPartyMember member)
    {
        var name = member?.Name.ToString() ?? "";
        var world = member?.World.ValueNullable?.Name.ToDalamudString().TextValue ?? "";
        var cid = member.ContentId;

        return (cid, name, world);
    }

    // ─── アライアンス対応拡張 (api13 fork より移植) ───────────────────────────────
    // Dalamud の IPartyList はアライアンス中、最大20人(A=0-7/B=8-15/C=16-19)を ContentId 付きで列挙する。

    /// <summary>アライアンス中か (Dalamud 標準プロパティ)。</summary>
    public static bool IsInAlliance(this IPartyList partyList) => partyList?.IsAlliance == true;

    /// <summary>パーティーまたはアライアンスに属しているか。</summary>
    public static bool IsInAllianceOrParty(this IPartyList partyList) =>
        partyList.IsInAlliance() || partyList.IsInParty();

    /// <summary>
    /// アンサンブル操作パネルを開く権限があるか。
    /// 通常パーティ=リーダーのみ / アライアンス=Party A(先頭8人)所属なら可 /
    /// クロスワールド等で Length=0 かつ IsAlliance=false の場合は制限を外して許可。
    /// </summary>
    public static bool IsAllianceOrPartyLeader(this IPartyList partyList)
    {
        if (partyList == null) return true;

        // Dalamud がパーティを認識できないクロスワールド構成 → 操作許可。
        if (partyList.Length == 0 && !partyList.IsAlliance)
            return true;

        var myCid = api.Player.ContentId;

        // アライアンス: Party A (先頭8スロット) に自分がいれば制御権あり。
        if (partyList.IsAlliance)
        {
            int slot = 0;
            foreach (var m in partyList)
            {
                if (slot >= 8) break;
                if (m != null) { if (m.ContentId == myCid) return true; slot++; }
            }
            return false;
        }

        // 通常パーティ (Length >= 2): リーダーのみ。
        if (partyList.Length < 2) return false;

        var leaderIdx = (int)partyList.PartyLeaderIndex;
        try
        {
            if (leaderIdx >= 0 && leaderIdx < partyList.Length)
            {
                var ldr = partyList[leaderIdx];
                if (ldr != null && ldr.ContentId == myCid) return true;
            }
        }
        catch { }

        // インデクサーが null を返す環境向けに foreach の rawPos でも照合。
        try
        {
            int raw = 0;
            foreach (var m in partyList)
            {
                if (raw == leaderIdx)
                    return m != null && m.ContentId == myCid;
                raw++;
            }
        }
        catch { }

        return false;
    }

    /// <summary>アライアンス・パーティーの全メンバー (最大20人)。</summary>
    public static System.Collections.Generic.IEnumerable<IPartyMember> GetAllEnsembleMembers(this IPartyList partyList)
    {
        int count = 0;
        foreach (var m in partyList)
        {
            if (count >= 20) yield break;
            if (m != null) { yield return m; count++; }
        }
    }

    /// <summary>Party A メンバー (0-7)。通常パーティ時は全員。</summary>
    public static System.Collections.Generic.IEnumerable<IPartyMember> GetPartyAMembers(this IPartyList partyList)
    {
        int max = partyList.IsInAlliance() ? 8 : 20;
        int count = 0;
        foreach (var m in partyList)
        {
            if (count >= max) yield break;
            if (m != null) { yield return m; count++; }
        }
    }

    /// <summary>Party B メンバー (8-15)。</summary>
    public static System.Collections.Generic.IEnumerable<IPartyMember> GetPartyBMembers(this IPartyList partyList)
    {
        int count = 0;
        foreach (var m in partyList)
        {
            if (count >= 16) yield break;
            if (m != null && count >= 8) yield return m;
            if (m != null) count++;
        }
    }

    /// <summary>Party C メンバー (16-19)。</summary>
    public static System.Collections.Generic.IEnumerable<IPartyMember> GetPartyCMembers(this IPartyList partyList)
    {
        int count = 0;
        foreach (var m in partyList)
        {
            if (count >= 20) yield break;
            if (m != null && count >= 16) yield return m;
            if (m != null) count++;
        }
    }

    /// <summary>CID がアライアンス(またはパーティー)メンバーに含まれるか。</summary>
    public static bool IsInAllianceOrPartyByCid(this IPartyList partyList, ulong cid) =>
        partyList.GetAllEnsembleMembers().Any(p => p.ContentId == cid);
}
