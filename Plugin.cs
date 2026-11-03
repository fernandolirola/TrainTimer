using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.MovingPlatforms;
using EFT.UI;
using EFT.UI.BattleTimer;
using HarmonyLib;
using UnityEngine;

#pragma warning disable CS0618 // CustomTextMeshProUGUI is what the game declares the field as

namespace TrainTimer;

public enum Language
{
    English,
    Portuguese,
	Russian,
}

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public enum TrainPhase
{
    Unknown,
    Coming,
    Boarding,
    Gone,
}

/// <summary>
/// Shows how long until the armored train arrives and how long until it leaves: on the train's row of the
/// game's own extraction list, and optionally as a small line on screen.
/// </summary>
[BepInPlugin("com.zezinho.traintimer", "zezinho-TrainTimer", "1.0.1")]
public class Plugin : BaseUnityPlugin
{
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<Language> Lang = null!;
    internal static ConfigEntry<bool> InExtractionList = null!;
    private ConfigEntry<bool> _overlay = null!;
    private ConfigEntry<Corner> _corner = null!;
    private ConfigEntry<int> _fontSize = null!;
    private ConfigEntry<KeyboardShortcut> _toggleKey = null!;

    private Locomotive? _train;
    private GameWorld? _world;
    private float _nextSearch;
    private bool _visible = true;
    private GUIStyle? _style;
    private string _text = "";

    private void Awake()
    {
        Enabled = Config.Bind("1. General", "Enabled", true, "Show the armored train times in raid.");
        Lang = Config.Bind("1. General", "Language", Language.English, "Language of the train texts.");
        InExtractionList = Config.Bind("2. Extraction List", "Show In Extraction List", true, "Show the train times on the train's row of the extraction list.");
        _overlay = Config.Bind("3. Overlay", "Show Overlay", false, "Also show the train time as a line on screen at all times.");
        _corner = Config.Bind("3. Overlay", "Corner", Corner.TopRight, "Screen corner for the overlay line.");
        _fontSize = Config.Bind("3. Overlay", "Font Size", 18, new ConfigDescription("Size of the overlay text.", new AcceptableValueRange<int>(10, 40)));
        _toggleKey = Config.Bind("3. Overlay", "Toggle Key", new KeyboardShortcut(KeyCode.None), "Key to hide or show the overlay during the raid.");

        try
        {
            ExtractionListPatch.Enable(new Harmony("com.zezinho.traintimer"));
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Extraction list times not applied: {ex.Message}");
        }
    }

    private void Update()
    {
        if (_toggleKey.Value.IsDown()) _visible = !_visible;

        var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
        if (world != _world)
        {
            _world = world;
            _train = null;
            _nextSearch = 0f;
        }
        if (world == null || !Enabled.Value || !_overlay.Value)
        {
            _text = "";
            return;
        }
        // the train only exists on some maps, so look for it now and then instead of every frame
        if (_train == null && Time.time >= _nextSearch)
        {
            _nextSearch = Time.time + 5f;
            _train = FindObjectOfType<Locomotive>();
        }
        _text = _train == null ? "" : Schedule.Sentence(_train);
    }

    private void OnGUI()
    {
        if (!_visible || string.IsNullOrEmpty(_text)) return;
        _style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        _style.fontSize = _fontSize.Value;

        var content = new GUIContent(_text);
        var size = _style.CalcSize(content);
        const float margin = 12f;
        var x = _corner.Value is Corner.TopLeft or Corner.BottomLeft ? margin : Screen.width - size.x - margin;
        var y = _corner.Value is Corner.TopLeft or Corner.TopRight ? margin : Screen.height - size.y - margin;
        var rect = new Rect(x, y, size.x, size.y);

        // dark outline keeps the text readable on bright snow and sky
        _style.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), content, _style);
        _style.normal.textColor = Color.white;
        GUI.Label(rect, content, _style);
    }
}

internal static class Schedule
{
    private static readonly AccessTools.FieldRef<MovingPlatform, DateTime> Depart = AccessTools.FieldRefAccess<MovingPlatform, DateTime>("_depart");

    private static bool Pt => Plugin.Lang.Value == Language.Portuguese;
	private static bool Ru => Plugin.Lang.Value == Language.Russian;
	

    /// <summary>
    /// The route starts at the depart time and each phase begins a fixed number of seconds later. Boarding ends when
    /// the train starts departing: that is when the exit begins its countdown for the players on board.
    /// </summary>
    public static TrainPhase Phase(Locomotive train, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        var depart = Depart(train);
        var timing = train._travelStateTiming;
        if (depart == default || timing == null || timing.Length <= (int)Locomotive.ETravelState.Departing) return TrainPhase.Unknown;

        var now = DateTimeExtensions.UtcNow;
        var arrives = depart.AddSeconds(timing[(int)Locomotive.ETravelState.Arrived]);
        var leaves = depart.AddSeconds(timing[(int)Locomotive.ETravelState.Departing]);
        if (now < arrives)
        {
            left = arrives - now;
            return TrainPhase.Coming;
        }
        if (now < leaves)
        {
            left = leaves - now;
            return TrainPhase.Boarding;
        }
        return TrainPhase.Gone;
    }

    public static string Sentence(Locomotive train) => Phase(train, out var left) switch
	{
		TrainPhase.Coming => (Ru ? "Бронепоезд прибудет через " : Pt ? "Trem blindado chega em " : "Armored train arrives in ") + Clock(left),
		TrainPhase.Boarding => (Ru ? "Бронепоезд отправится через " : Pt ? "Trem blindado parte em " : "Armored train leaves in ") + Clock(left),
		TrainPhase.Gone => Ru ? "Бронепоезд уже уехал" : Pt ? "O trem blindado já partiu" : "The armored train has left",
		_ => "",
	};

    public static string Tag(TrainPhase phase) => phase switch
	{
		TrainPhase.Coming => Ru ? "прибудет через" : Pt ? "chega em" : "arrives in",
		TrainPhase.Boarding => Ru ? "отправится через" : Pt ? "parte em" : "leaves in",
		TrainPhase.Gone => Ru ? "уже уехал" : Pt ? "já partiu" : "has left",
		_ => "",
	};

    public static string Clock(TimeSpan left) =>
        left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes:00}:{left.Seconds:00}";
}

/// <summary>
/// The game lists the train exit with "??:??:??" until you reach it. On that row, the timer shows the time until
/// the train arrives or leaves, and the exit name says which of the two it is.
/// </summary>
internal static class ExtractionListPatch
{
    private static AccessTools.FieldRef<ExitTimerPanel, ExfiltrationPoint>? _point;
    private static AccessTools.FieldRef<ExitTimerPanel, CustomTextMeshProUGUI>? _name;

    public static void Enable(Harmony harmony)
    {
        _point = AccessTools.FieldRefAccess<ExitTimerPanel, ExfiltrationPoint>("_point");
        _name = AccessTools.FieldRefAccess<ExitTimerPanel, CustomTextMeshProUGUI>("_pointName");
        var setText = AccessTools.Method(typeof(ExitTimerPanel), nameof(ExitTimerPanel.SetTimerText), [typeof(TimeSpan)])
            ?? throw new MissingMethodException("ExitTimerPanel.SetTimerText");
        var setActive = AccessTools.Method(typeof(ExitTimerPanel), nameof(ExitTimerPanel.SetTimerTextActive))
            ?? throw new MissingMethodException("ExitTimerPanel.SetTimerTextActive");
        harmony.Patch(setText, postfix: new HarmonyMethod(typeof(ExtractionListPatch), nameof(AfterSetText)));
        harmony.Patch(setActive, postfix: new HarmonyMethod(typeof(ExtractionListPatch), nameof(AfterSetActive)));
        var updateVisited = AccessTools.Method(typeof(ExitTimerPanel), nameof(ExitTimerPanel.UpdateVisitedStatus))
            ?? throw new MissingMethodException("ExitTimerPanel.UpdateVisitedStatus");
        harmony.Patch(updateVisited, postfix: new HarmonyMethod(typeof(ExtractionListPatch), nameof(AfterUpdateVisited)));
    }

    private static Locomotive? TrainOf(ExitTimerPanel panel)
    {
        if (!Plugin.Enabled.Value || !Plugin.InExtractionList.Value || _point is null) return null;
        var point = _point(panel);
        if (point == null || !point.HasRequirements) return null;
        return point.Requirements.OfType<TrainRequirement>().FirstOrDefault()?.Locomotive;
    }

    private static void AfterSetActive(ExitTimerPanel __instance)
    {
        if (TrainOf(__instance) != null) __instance.TimerText.gameObject.SetActive(true);
    }

    // once the exit is visited the game shows requirement tips and fence rep where the timer sits
    private static void AfterUpdateVisited(ExitTimerPanel __instance)
    {
        if (TrainOf(__instance) == null) return;
        if (__instance._itemsObject != null) __instance._itemsObject.SetActive(false);
        if (__instance._discountStats != null) __instance._discountStats.SetActive(false);
    }

    private static void AfterSetText(ExitTimerPanel __instance)
    {
        try
        {
            if (TrainOf(__instance) is not { } train) return;
            var phase = Schedule.Phase(train, out var left);
            if (phase == TrainPhase.Unknown) return;

            __instance.TimerText.gameObject.SetActive(true);
            __instance.TimerText.text = phase == TrainPhase.Gone ? "--:--" : Schedule.Clock(left);
            if (_name is not null && _point is not null && _name(__instance) is { } name)
                name.text = $"{_point(__instance).Settings.Name.Localized()} ({Schedule.Tag(phase)})";
        }
        catch
        {
            // the list must keep working even if the train data is not there
        }
    }
}
