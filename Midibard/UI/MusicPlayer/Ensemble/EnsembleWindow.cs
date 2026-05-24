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

using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private bool ShowEnsembleWindow;

    private void DrawEnsembleWindow()
    {
        if (!ShowEnsembleWindow) return;
        if (!global::MidiBard.Managers.EnsembleMembers.CanConduct()) return;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * 2.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.Y));

        if (ImGui.Begin(Language.window_title_ensemble_panel + "###ensembleWindow", ref ShowEnsembleWindow))
        {
            ImGui.BeginChild("##EnsembleControlMenuFixedHeight", ImGuiHelpers.ScaledVector2(-1, 40), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawEnsembleControlMenu();
            ImGui.EndChild();

            ImGui.Separator();

            ImGui.BeginChild("##EnsembleScrollableContent", new Vector2(-1, 0), false, ImGuiWindowFlags.HorizontalScrollbar);

            if (MidiBard.config.playOnMultipleDevices && !MidiBard.config.usingFileSharingServices)
            {
                ImGui.Button($"You are NOT using file sharing services to sync settings.\nTrack assign is disabled.\nPlease choose the tracks on clients individually.", new Vector2(-1, 100));
            }
            else if (MidiBard.CurrentPlayback == null)
            {
                if (ImGui.Button(Language.ensemble_select_a_song_from_playlist, new Vector2(-1, ImGui.GetFrameHeight())))
                {
                }
            }
            else
            {
                try
                {
                    var changed = false;
                    var fileConfig = MidiBard.CurrentPlayback.MidiFileConfig;

                    // トラック数ヘッダー
                    int trackCount = fileConfig?.Tracks?.Count ?? 0;
                    ImGui.TextUnformatted($"Tracks: {trackCount}");
                    ImGui.Separator();

                    if (fileConfig == null || trackCount == 0)
                    {
                        ImGui.TextUnformatted(fileConfig == null
                            ? "Song config not loaded."
                            : "No tracks found.");
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Reload##reloadMidiConfig"))
                        {
                            // MidiFileConfig を再構築してアライアンス合奏設定を反映する
                            var pb = MidiBard.CurrentPlayback;
                            if (pb != null)
                            {
                                var newConfig = MidiFileConfigManager.GetMidiConfigFromFile(pb.FilePath)
                                               ?? MidiFileConfigManager.GetMidiConfigFromTrack(pb.TrackInfos);
                                pb.MidiFileConfig = newConfig;
                            }
                        }
                    }
                    else
                    {

                        // 同一ワールド＋クロスワールドの全員を候補にする。
                        var partyList = global::MidiBard.Managers.EnsembleMembers.GetAll();

                        var cidToIndexMap = MidiBard.config.EnsembleMemberConfigs
                            .Select((config, index) => new { config.Cid, Index = index })
                            .ToDictionary(item => item.Cid, item => item.Index);

                        var orderedPartyList = partyList
                            .OrderBy(partyMember => cidToIndexMap.ContainsKey(partyMember.Cid)
                                                    ? cidToIndexMap[partyMember.Cid]
                                                    : int.MaxValue)
                            .ToList();

                        orderedPartyList.Insert(0, (Cid: 0, Name: "", World: ""));

                        var partyNamesList = orderedPartyList
                            .Select(partyMember => partyMember.Cid != 0 ? $"{partyMember.Name}·{partyMember.World}" : "")
                            .ToArray();

                        if (ImGui.BeginTable("fileConfig.Tracks", 4, ImGuiTableFlags.SizingFixedFit))
                        {
                            ImGui.TableSetupColumn("checkbox", ImGuiTableColumnFlags.WidthStretch, 1);
                            ImGui.TableSetupColumn("instrument", ImGuiTableColumnFlags.WidthFixed);
                            ImGui.TableSetupColumn("transpose", ImGuiTableColumnFlags.WidthFixed);
                            ImGui.TableSetupColumn("playername", ImGuiTableColumnFlags.WidthStretch, 1.2f);

                            var id = 125687;
                            foreach (var dbTrack in fileConfig.Tracks)
                            {
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.PushID(id++);
                                ImGui.PushStyleColor(ImGuiCol.Text, dbTrack.Enabled ? ThemeManager.CurrentTheme.Text : ThemeManager.CurrentTheme.TextDisabled);
                                ImGui.AlignTextToFramePadding();
                                changed |= ImGui.Checkbox($"{dbTrack.Index + 1:00} {dbTrack.Name}", ref dbTrack.Enabled);

                                ImGui.TableNextColumn(); //1
                                changed |= InstrumentPicker($"##ensembleInstrumentPicker", ref dbTrack.Instrument);

                                ImGui.TableNextColumn(); //2
                                ImGui.SetNextItemWidth(ImGui.GetFrameHeight() * 3.3f);
                                changed |= ImGuiUtil.InputIntWithReset($"##ensembleTransposeTrack", ref dbTrack.Transpose, 12, () => 0);

                                ImGui.TableNextColumn(); //3
                                ImGui.SetNextItemWidth(-1);

                                var firstMidiFileCid = MidiFileConfig.GetFirstCidInParty(dbTrack);
                                var selectedIdx = firstMidiFileCid == 0 ? 0 : orderedPartyList.FindIndex(i => i.Cid != 0 && i.Cid == firstMidiFileCid);

                                if (ImGui.Combo("##partymemberSelect", ref selectedIdx, partyNamesList, partyNamesList.Length))
                                {
                                    if (selectedIdx >= 1)
                                    {
                                        var currentCid = orderedPartyList[selectedIdx].Cid;
                                        if (firstMidiFileCid > 0 && currentCid != firstMidiFileCid)
                                        {
                                            dbTrack.AssignedCids.Remove(firstMidiFileCid);
                                            changed = true;
                                        }

                                        if (currentCid > 0)
                                        {
                                            if (!dbTrack.AssignedCids.Contains(currentCid))
                                            {
                                                dbTrack.AssignedCids.Insert(0, currentCid);
                                                changed = true;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // choose empty: 担当割当を全員ぶん解除 (同一ワールド＋クロスワールド)
                                        dbTrack.AssignedCids.RemoveAll(c => global::MidiBard.Managers.EnsembleMembers.Contains(c));
                                        changed = true;
                                    }
                                }

                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    // choose empty: 担当割当を全員ぶん解除 (同一ワールド＋クロスワールド)
                                    dbTrack.AssignedCids.RemoveAll(c => global::MidiBard.Managers.EnsembleMembers.Contains(c));
                                    changed = true;
                                }

                                ImGuiUtil.ToolTip(Language.ensemble_combo_tooltip_assign_track_character);

                                ImGui.PopStyleColor();

                                ImGui.PopID();
                            }

                            ImGui.EndTable();
                        }

                        if (changed)
                        {
                            fileConfig.Save(MidiBard.CurrentPlayback.FilePath);
                            IPCHandles.UpdateMidiFileConfig(fileConfig);
                        }

                    } // end else (trackCount > 0)
                }
                catch (Exception e)
                {
                    ImGui.TextUnformatted(e.ToString());
                }
            }

#if DEBUG
            try
            {
                foreach (var partyMember in api.PartyList)
                {
                    ImGui.TextUnformatted($"{partyMember.Name} {partyMember.ContentId:X} {partyMember.EntityId:X} {partyMember.Address.ToInt64():X}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"C##{partyMember.ContentId}"))
                    {
                        ImGui.SetClipboardText(partyMember.Address.ToInt64().ToString("X"));
                    }
                }
            }
            catch (Exception e)
            {
                ImGui.TextUnformatted(e.ToString());
            }
#endif

            ImGui.EndChild();
        }

        ImGui.End();

        ImGui.PopStyleVar(2);
    }
}
