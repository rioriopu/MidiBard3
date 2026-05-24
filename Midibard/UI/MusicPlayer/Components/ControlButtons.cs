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
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Util;

using MidiBard2.Resources;

using static Dalamud.api;

namespace MidiBard;

public partial class PluginUI
{

    private static string GetPlayModeLabel(int labelIndex)
    {
        string[] playModeOptionsLabels = {
            Language.play_mode_single,
            Language.play_mode_single_repeat,
            Language.play_mode_list_ordered,
            Language.play_mode_list_repeat,
            Language.play_mode_random,
        };

        if (labelIndex < playModeOptionsLabels.Length)
        {
            return playModeOptionsLabels[labelIndex];
        }

        return string.Empty;
    }

    private void DrawButtonPlayPause(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
        var PlayPauseIcon = MidiBard.IsPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
        if (ImGuiUtil.IconButton(PlayPauseIcon, "##btnPlayPause"))
        {
            PluginLog.Debug($"PlayPause pressed. was playing: {MidiBard.IsPlaying}");
            MidiPlayerControl.PlayPause();
        }
        ImGui.SameLine();
        ImGui.EndDisabled();
    }

    private void DrawButtonStop()
    {
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Stop, "##btnStop", "Stop"))
        {
            if (FilePlayback.IsWaiting)
            {
                FilePlayback.CancelWaiting();
            }
            else
            {
                MidiPlayerControl.Stop();
            }

            StopEnsemble();
        }
    }

    /// <summary>
    /// 「曲を読み込んでアンサンブル準備」ボタン。
    /// 左クリック: 選択中の曲を読み込み (再生しない) + アライアンス全員に楽器装着を指示。
    /// </summary>
    private void DrawButtonLoadAndPrepareEnsemble()
    {
        var tooltip = EnsembleMembers.IsAllianceOrCrossWorld()
            ? "Load song & equip instruments for all alliance members\n(曲を読み込み、アライアンス全員が担当楽器を装着)"
            : "Load song config\n(曲の設定を読み込む)";

        if (ImGuiUtil.IconButton(FontAwesomeIcon.FileImport, "##btnLoadEnsemble", tooltip))
        {
            // 非同期で曲を読み込み → 完了後に楽器装着指示を送信
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // CurrentSongIndex が未設定(-1) の場合は先頭曲(0)を使う (Play()と同じ動作)
                    int idx = PlaylistManager.CurrentSongIndex < 0 ? 0 : PlaylistManager.CurrentSongIndex;
                    await PlaylistManager.LoadPlayback(idx, startPlaying: false, sync: true);

                    // 他クライアントが曲をロードし終わるまで待つ (IPC往復 + LoadPlayback処理時間)
                    await System.Threading.Tasks.Task.Delay(1200);

                    api.Framework.RunOnFrameworkThread(() =>
                    {
                        // EnsembleControlWindow に設定を反映 (他クライアントにも配信)
                        if (MidiBard.CurrentPlayback?.MidiFileConfig is { } config)
                            IPCHandles.UpdateMidiFileConfig(config);
                    });

                    // UpdateMidiFileConfig が他クライアントに届いてから楽器装着指示
                    await System.Threading.Tasks.Task.Delay(500);
                    api.Framework.RunOnFrameworkThread(() =>
                    {
                        if (EnsembleMembers.IsAllianceOrCrossWorld())
                            PartyChatCommand.SendAllianceUpdateInstrument();
                        else if (MidiBard.config.playOnMultipleDevices)
                            PartyChatCommand.SendUpdateInstrument();
                        else
                            IPCHandles.UpdateInstrument(true);
                    });
                }
                catch (Exception ex)
                {
                    api.PluginLog.Error(ex, "[LoadEnsemble] failed to load playback");
                }
            });
        }
        ImGui.SameLine();
    }

    private void DrawButtonPlayMode(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
        ImGui.SameLine();
        FontAwesomeIcon icon = (PlayMode)MidiBard.config.PlayMode switch
        {
            PlayMode.Single => FontAwesomeIcon.Reply,
            PlayMode.ListOrdered => FontAwesomeIcon.SortAmountDownAlt,
            PlayMode.ListRepeat => FontAwesomeIcon.Sync,
            PlayMode.SingleRepeat => FontAwesomeIcon.Redo,
            PlayMode.Random => FontAwesomeIcon.Random,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (ImGuiUtil.IconButton(icon, "##btnPlayMode"))
        {
            MidiBard.config.PlayMode += 1;
            MidiBard.config.PlayMode %= 5;
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            MidiBard.config.PlayMode += 4;
            MidiBard.config.PlayMode %= 5;
        }
        ImGui.EndDisabled();
        ImGuiUtil.ToolTip(GetPlayModeLabel(MidiBard.config.PlayMode));
    }

    private void DrawButtonShowSettingsWindow()
    {
        ImGui.SameLine();
        Vector4? btnColor = MidiBard.Ui.showSettingsWindow ? MidiBard.config.themeColor : null;

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Cog, "##btnSettingsWindow", color: btnColor))
        {
            MidiBard.Ui.ToggleSettingsWindow();
        }
        ImGuiUtil.ToolTip(Language.icon_button_tooltip_settings_panel);
    }

    private void DrawButtonVisualization()
    {
        ImGui.SameLine();
        Vector4? color = MidiBard.Ui.showTrackVisualizerWindow ? MidiBard.config.themeColor : null;
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Film, "##btnTrackVisualizerToggle", Language.icon_button_tooltip_visualization, color))
        {
            showTrackVisualizerWindow ^= true;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _resetPlotWindowPosition = true;
        }
    }

    private void DrawButtonShowEnsembleWindow(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
        ImGui.SameLine();
        Vector4? btnColor = MidiBard.Ui.ShowEnsembleWindow ? MidiBard.config.themeColor : null;
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Users, "##btnEnsemble", color: btnColor))
        {
            ShowEnsembleWindow ^= true;
        }
        ImGui.EndDisabled();
        ImGuiUtil.ToolTip(Language.icon_button_tooltip_ensemble_panel);
    }

    private static void StopEnsemble()
    {
        if (MidiBard.config.playOnMultipleDevices && api.PartyList.Length > 1)
        {
            PartyChatCommand.SendClose();
        }
        else if (api.PartyList.Length <= 1)
        {
            SwitchInstrument.SwitchToContinue(0);
            MidiPlayerControl.Stop();
            return;
        }
        else
        {
            IPCHandles.UpdateInstrument(false);
        }
    }
}
