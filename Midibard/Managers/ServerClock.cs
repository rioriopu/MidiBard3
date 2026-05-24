// FF14 のサーバー時刻を共有クロックとして使うヘルパ (NTP 非依存)。
// Framework.GetServerTime() は各クライアントのローカル時計をサーバーに合わせた時刻なので、
// ローカル時計(NTP)の誤差に依存せず全クライアントで一致する。秒解像度のため、サーバー秒が
// 変わった瞬間のローカル時刻からオフセットを学習し、ミリ秒精度のサーバー基準時刻を提供する。

using System;

using Dalamud.Plugin.Services;

using static Dalamud.api;

using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace MidiBard.Managers;

internal static class ServerClock
{
    private static long _offsetMs;          // serverMs - localUnixMs
    private static long _lastRaw = long.MinValue;
    private static bool _hooked;

    public static bool Ready { get; private set; }

    public static void Init()
    {
        if (_hooked) return;
        _hooked = true;
        api.Framework.Update += OnUpdate;
    }

    public static void Dispose()
    {
        if (!_hooked) return;
        api.Framework.Update -= OnUpdate;
        _hooked = false;
    }

    private static void OnUpdate(IFramework _)
    {
        long raw = CSFramework.GetServerTime(); // 静的メソッド (サーバー時刻)
        if (raw == _lastRaw) return;            // 値が変わった瞬間 = 秒境界(またはms更新)
        _lastRaw = raw;

        // >1e11 なら既にミリ秒、それ未満なら秒なので 1000 倍。
        long serverMs = raw > 100_000_000_000L ? raw : raw * 1000L;
        _offsetMs = serverMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Ready = true;
    }

    /// <summary>サーバー基準の現在時刻 (ミリ秒)。未準備ならローカル壁時計にフォールバック。</summary>
    public static long NowMs()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (Ready ? _offsetMs : 0L);
}
