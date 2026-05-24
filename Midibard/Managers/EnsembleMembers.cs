// アンサンブルのメンバー列挙 (reckhou/MidiBard2 fork の改良 / 案X)。
// 同一ワールドは Dalamud の IPartyList、クロスワールド(別ワールド混在)は ClientStructs の
// InfoProxyCrossRealm から取得する。Dalamud の IPartyList はクロスワールドのパーティ/アライアンスを
// 列挙しない (空になる) ため、これを併用しないと別ワールド混在の合奏でメンバーが出ない。

using System.Collections.Generic;

using FFXIVClientStructs.FFXIV.Client.UI.Info;

using MidiBard.Managers.Ipc;

using static Dalamud.api;

namespace MidiBard.Managers;

internal static unsafe class EnsembleMembers
{
    /// <summary>合奏対象メンバー (ContentId/名前/ワールド)。同一ワールド＋クロスワールドの両方を統合・重複除去。</summary>
    public static List<(ulong Cid, string Name, string World)> GetAll()
    {
        var list = new List<(ulong Cid, string Name, string World)>();
        var seen = new HashSet<ulong>();

        // 同一ワールド (Dalamud IPartyList)
        foreach (var m in api.PartyList)
        {
            if (m != null && m.ContentId > 0 && seen.Add(m.ContentId))
            {
                var d = m.GetPartyMemberData();
                list.Add((d.Cid, d.Name, d.World));
            }
        }

        // クロスワールド (InfoProxyCrossRealm)。最大3グループ(アライアンス)。
        if (InfoProxyCrossRealm.IsCrossRealmParty())
        {
            for (var g = 0; g < 3; g++)
            {
                var count = InfoProxyCrossRealm.GetGroupMemberCount(g);
                for (uint i = 0; i < count; i++)
                {
                    var mem = InfoProxyCrossRealm.GetGroupMember(i, g);
                    if (mem == null) continue;
                    var cid = mem->ContentId;
                    if (cid > 0 && seen.Add(cid))
                        list.Add((cid, mem->NameString, WorldName((uint)mem->HomeWorld)));
                }
            }
        }

        return list;
    }

    /// <summary>指定 CID が合奏対象 (同一ワールドパーティ or クロスワールドパーティ/アライアンス) に含まれるか。</summary>
    public static bool Contains(ulong cid)
    {
        if (cid == 0) return false;
        foreach (var m in api.PartyList)
            if (m != null && m.ContentId == cid)
                return true;
        return InfoProxyCrossRealm.IsCrossRealmParty() && InfoProxyCrossRealm.IsContentIdInParty(cid);
    }

    /// <summary>アライアンス or クロスワールド構成か (アライアンス合奏ボタンを出す/送信する条件)。</summary>
    public static bool IsAllianceOrCrossWorld()
        => api.PartyList.IsInAlliance() || InfoProxyCrossRealm.IsCrossRealmParty();

    /// <summary>アンサンブル操作パネルを開ける条件。</summary>
    public static bool CanConduct()
    {
        if (InfoProxyCrossRealm.IsCrossRealmParty())
            return InfoProxyCrossRealm.IsLocalPlayerInParty();
        return api.PartyList.IsAllianceOrPartyLeader();
    }

    private static string WorldName(uint worldId)
    {
        if (worldId == 0) return "";
        var row = api.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(worldId);
        return row?.Name.ExtractText() ?? "";
    }
}
