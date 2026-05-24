using System;
using System.Collections.Generic;
using System.Linq;

using BardMusicPlayer.XIVMIDI;
using BardMusicPlayer.XIVMIDI.IO;

using Dalamud.Game.Text;
using Dalamud.Utility;

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard;

internal static class PartyChatCommand
{
    private static readonly Dictionary<string, Action<string[]>> CommandHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["playonmultipledevices"] = HandlePlayOnMultipleDevices,
            ["pmd"] = HandlePlayOnMultipleDevices,
            ["switchto"] = HandleSwitchTo,
            ["usechatplaylistsync"] = HandleSendUseChatPlaylistSync,
            ["playlistremove"] = HandleRemoveSong,
            ["playlistmove"] = HandleChangeSongOrder,
            ["reloadplaylist"] = HandleReloadPlaylist,
            ["updatedefaultperformer"] = HandleUpdateDefaultPerformer,
            ["updateinstrument"] = HandleUpdateInstrument,
            ["close"] = HandleClose,
            ["speed"] = HandleChangeSpeed,
            ["transpose"] = HandleSetGlobalTranspose,
            ["downloadsong"] = HandleDownloadSong,
        };

    // アライアンスチャット (/a) で受け付けるコマンド。案X: アライアンス全員でローカル再生を同期する。
    private static readonly Dictionary<string, Action<string[]>> AllianceCommandHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = HandleAlliancePlay,
            ["stop"] = HandleAllianceStop,
            ["updateinstrument"] = HandleAllianceUpdateInstrument,
        };

    internal static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.IsHandled)
            return;
        if (message.LogKind != XivChatType.Party && message.LogKind != XivChatType.Alliance)
            return;

        string[] parts = message.Message.TextValue.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return;

        string cmd = parts[0].ToLower();
        string[] args = parts.Skip(1).ToArray();

        // パーティチャットは従来コマンド、アライアンスチャットはアライアンス専用コマンドのみ受け付ける。
        var handlers = message.LogKind == XivChatType.Alliance ? AllianceCommandHandlers : CommandHandlers;
        if (handlers.TryGetValue(cmd, out var action))
        {
            action.Invoke(args);
        }
    }

    // ===== アライアンス合奏 (タイムスタンプ同期方式) =====
    //
    // 同期の仕組み:
    //   1. リーダーが拡声器ボタン or /mbard aensemble を押す
    //   2. /countdown N をゲームに送信 (視覚的カウントダウン)
    //   3. /a play <TARGET_MS> をアライアンスチャットに送信
    //      TARGET_MS = 現在UTC ms + N秒 + バッファ(=サーバー往復遅延の推定値)
    //   4. 全員(リーダー含む)がチャットを受信したら:
    //      delay = TARGET_MS - 現在UTC ms を計算して RunOnTick でスケジュール
    //
    // ポイント: リーダーはローカルエコーで即受信、他は遅延受信になるが、
    //           絶対目標時刻(TARGET_MS)は全員共通なので遅延が自動吸収される。
    //           NTPで同期された時計があれば全クライアント±数ms以内で揃う。
    //
    //  例: countdown=5、バッファ=500ms の場合
    //    T=0ms:      リーダーが /countdown 5 + /a play TARGET(T+5500) 送信
    //    T≈100ms:   全員がサーバー経由でメッセージ受信
    //    T≈5500ms:  TARGET_MS 到達 → 全員同時演奏開始
    //    (/countdown 表示は T+100〜T+5100ms の5秒間。演奏開始は0の約400ms後)

    private const int PlayDelayMsFallback = 3000; // timestamp解析失敗時のフォールバック
    private static DateTime _lastAlliancePlay = DateTime.MinValue;
    private static DateTime _lastAllianceStop = DateTime.MinValue;

    internal static void SendAllianceEnsembleStart()
    {
        if (!EnsembleMembers.IsAllianceOrCrossWorld())
            return;

        int countdown = MidiBard.config.AllianceCountdownSeconds;

        // 視覚的カウントダウン送信 (リーダーのパーティグループに表示)
        if (countdown > 0)
            Chat.SendMessage($"/countdown {countdown}");

        // 目標時刻: カウントダウン秒数 + サーバー往復遅延バッファ(500ms)。FF14サーバー時刻基準(NTP非依存)。
        long targetMs = ServerClock.NowMs()
                        + countdown * 1000L
                        + 500L;
        Chat.SendMessage($"/a play {targetMs}");
    }

    private static void HandleAlliancePlay(string[] args)
    {
        if ((DateTime.Now - _lastAlliancePlay).TotalSeconds < 3)
            return; // 二重防止 (ローカルエコー等)
        _lastAlliancePlay = DateTime.Now;

        var pb = MidiBard.CurrentPlayback;
        if (pb == null)
            return; // 曲未ロードなら何もしない

        // 即座にセットアップ(担当トラック反映・楽器装備・停止・先頭出し)を実施
        try
        {
            pb.SyncTrackStatusWithMidiFileConfig();
            SwitchInstrument.SwitchToContinue(pb.GetInstrumentId());
            pb.Stop();
            pb.MoveToStart();
        }
        catch (Exception e) { api.PluginLog.Error(e, "error preparing alliance ensemble"); }

        // 目標時刻まで待ってから演奏開始
        long delayMs;
        if (args.Length > 0 && long.TryParse(args[0], out long targetMs))
        {
            delayMs = targetMs - ServerClock.NowMs(); // FF14サーバー時刻基準で目標までの待ち
            if (delayMs < 50) delayMs = 50;     // 最低50ms(フレーム同期バッファ)
            if (delayMs > 30000) delayMs = PlayDelayMsFallback; // 30秒超は異常値
            api.PluginLog.Debug($"[AlliancePlay] target={targetMs} delay={delayMs}ms");

            // 全パーティ(B・C含む)にカウントダウンを表示するため、
            // 各クライアントが自パーティ向けに /countdown を発行する。
            // 残り時間から秒数を算出し、2秒以上あれば発行する。
            int countdownSec = (int)(delayMs / 1000);
            if (countdownSec >= 2)
                Chat.SendMessage($"/countdown {countdownSec}");
        }
        else
        {
            // 旧形式(/a play 引数なし)へのフォールバック
            delayMs = PlayDelayMsFallback;
            api.PluginLog.Debug($"[AlliancePlay] no timestamp, fallback delay={delayMs}ms");
        }

        api.Framework.RunOnTick(() => MidiPlayerControl.DoPlay(true), TimeSpan.FromMilliseconds(delayMs));
    }

    /// <summary>アライアンス全員に担当楽器の装着を指示する。</summary>
    internal static void SendAllianceUpdateInstrument()
    {
        if (!EnsembleMembers.IsAllianceOrCrossWorld())
            return;
        Chat.SendMessage("/a updateinstrument");
    }

    private static void HandleAllianceUpdateInstrument(string[] args)
    {
        var pb = MidiBard.CurrentPlayback;
        if (pb == null)
        {
            // 曲が未ロードの場合: プレイリストから自動ロードしてから再試行
            if (PlaylistManager.FilePathList.Count > 0)
            {
                api.PluginLog.Debug("[AllianceUpdateInstrument] CurrentPlayback is null, auto-loading song...");
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    int idx = PlaylistManager.CurrentSongIndex < 0 ? 0 : PlaylistManager.CurrentSongIndex;
                    await PlaylistManager.LoadPlayback(idx, startPlaying: false, sync: false);
                    await System.Threading.Tasks.Task.Delay(500);
                    api.Framework.RunOnFrameworkThread(() => HandleAllianceUpdateInstrument(args));
                });
            }
            return;
        }
        try
        {
            pb.SyncTrackStatusWithMidiFileConfig();
            uint instrumentId = pb.GetInstrumentId();
            SwitchInstrument.SwitchToContinue(instrumentId);
            api.PluginLog.Debug($"[AllianceUpdateInstrument] instrument={instrumentId}");
        }
        catch (Exception e) { api.PluginLog.Error(e, "HandleAllianceUpdateInstrument"); }
    }

    internal static void SendAllianceEnsembleStop()
    {
        if (!EnsembleMembers.IsAllianceOrCrossWorld())
            return;
        Chat.SendMessage("/a stop");
    }

    private static void HandleAllianceStop(string[] args) => TriggerLocalStop();

    private static void TriggerLocalStop()
    {
        if ((DateTime.Now - _lastAllianceStop).TotalSeconds < 3)
            return;
        _lastAllianceStop = DateTime.Now;
        MidiPlayerControl.Stop();
    }

    internal static void SendPlayOnMultipleDevices(bool isOn)
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        var str = isOn ? "on" : "off";
        Chat.SendMessage($"/p pmd {str}");
    }

    private static void HandlePlayOnMultipleDevices(string[] args)
    {
        if (args.Length < 1)
            return;

        var value = args[0].ToLower();
        if (value == "on")
            MidiBard.config.playOnMultipleDevices = true;
        else if (value == "off")
            MidiBard.config.playOnMultipleDevices = false;
    }

    // -------------------------

    internal static void SendUseChatPlaylistSync(bool isOn)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
        {
            return;
        }

        var str = isOn ? "on" : "off";
        Chat.SendMessage($"/p usechatplaylistsync {str}");
    }

    private static void HandleSendUseChatPlaylistSync(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices) return;

        if (args.Length < 1)
            return;

        var value = args[0].ToLower();
        if (value == "on")
            MidiBard.config.useChatPlaylistSync = true;
        else if (value == "off")
            MidiBard.config.useChatPlaylistSync = false;
    }

    // -------------------------

    internal static void SendSwitchTo(int songIndex)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p switchto {songIndex + 1}");
    }

    private static void HandleSwitchTo(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int songIndex))
        {
            MidiPlayerControl.StopLrc();
            PlaylistManager.LoadPlayback(songIndex - 1);
            MidiBard.Ui.OpenMainWindow();
        }
    }

    // -------------------------

    internal static void SendRemoveSong(int songIndex)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p playlistremove {songIndex + 1}");
    }

    private static void HandleRemoveSong(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int songIndex))
        {
            PlaylistManager.RemoveLocal(songIndex - 1);
        }
    }

    // -------------------------

    internal static void SendChangeSongOrder(int songIndex, int targetIndex)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p playlistmove {songIndex + 1} {targetIndex + 1}");
    }

    private static void HandleChangeSongOrder(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || args.Length < 2)
            return;

        if (int.TryParse(args[0], out int fromIndex) && int.TryParse(args[1], out int toIndex))
        {
            PlaylistManager.MoveSongToIndexLocal(fromIndex - 1, toIndex - 1);
        }
    }

    // -------------------------

    internal static void SendChangeSpeed(float speed)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p speed {speed}");
    }

    private static void HandleChangeSpeed(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (float.TryParse(args[0], out float speed))
        {
            MidiBard.config.PlaySpeed = Math.Max(0.1f, speed);
        }
    }

    // -------------------------

    internal static void SendSetGlobalTranspose(int transpose)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p transpose {transpose}");
    }

    private static void HandleSetGlobalTranspose(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int transpose))
        {
            MidiBard.config.SetTransposeGlobal(transpose);
        }
    }

    // -------------------------

    internal static void SendClose()
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage("/p close");
    }

    private static void HandleClose(string[] args)
    {
        MidiPlayerControl.Stop();
        SwitchInstrument.SwitchToAsync(0);
    }

    // -------------------------

    internal static void SendReloadPlaylist()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p reloadplaylist");
    }

    private static void HandleReloadPlaylist(string[] args)
    {
        PlaylistManager.CurrentContainer = PlaylistManager.LoadLastPlaylist();
    }

    // -------------------------

    internal static void SendUpdateDefaultPerformer()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p updatedefaultperformer");
    }

    private static void HandleUpdateDefaultPerformer(string[] args)
    {
        MidiFileConfigManager.LoadDefaultPerformer();
    }

    // -------------------------

    internal static void SendUpdateInstrument()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p updateinstrument");
    }

    private static void HandleUpdateInstrument(string[] args)
    {
        if (MidiBard.CurrentPlayback == null)
        {
            return;
        }

        MidiBard.CurrentPlayback.SyncTrackStatusWithMidiFileConfig();
        uint instrumentId = MidiBard.CurrentPlayback.GetInstrumentId();

        SwitchInstrument.SwitchToContinue(instrumentId);
    }

    // -------------------------

    internal static void SendDownloadSong(string url)
    {
        if (!api.PartyList.IsPartyLeader() || !MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
            return;
        Chat.SendMessage($"/p downloadsong {url}");
    }

    private static void HandleDownloadSong(string[] args)
    {
        if (!args[0].IsNullOrEmpty())
        {
            api.LogDebug("download");
            XIVMIDI.Instance.AddToQueue(new GetRequest()
            {
                Url = args[0],
                Host = "xivmidi.com",
                Accept = "audio/midi",
                Requester = Requester.DOWNLOAD
            });
        }
    }
}
