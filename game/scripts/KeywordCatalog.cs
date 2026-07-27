using System.Collections.Generic;
using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// The editable table of keyword display text (docs/22 批次D4). Edit res://data/keyword_catalog.tres in the
/// Godot Inspector — rename a keyword, reword its explanation — with no code change or recompile. Loaded once
/// and cached; if the .tres is missing or broken it falls back to <see cref="BuildDefault"/>, which is
/// byte-for-byte the old hardcoded CardView table (also the source used to (re)generate the .tres).
/// </summary>
[GlobalClass]
public partial class KeywordCatalog : Resource
{
    [Export] public Godot.Collections.Array<KeywordDef> Keywords { get; set; } = new();

    private static KeywordCatalog? _cached;
    private static Dictionary<string, KeywordDef>? _byName;

    /// <summary>The catalog every display surface reads: .tres if present, else the built-in default.</summary>
    public static KeywordCatalog Instance => _cached ??= GameData.LoadKeywordCatalog();

    /// <summary>Entry for a rules-layer keyword, or null for unknown/internal keywords (which the old switch
    /// rendered as empty strings — callers keep that fallback).</summary>
    public static KeywordDef? Get(Keyword k)
    {
        if (_byName is null)
        {
            _byName = new Dictionary<string, KeywordDef>();
            foreach (var d in Instance.Keywords)
                if (d is not null && d.KeywordName.Length > 0)
                    _byName[d.KeywordName] = d;
        }
        return _byName.TryGetValue(k.ToString(), out var def) ? def : null;
    }

    /// <summary>The pre-catalog hardcoded table (CardView.KeywordName0 + KeywordDesc as of v0.7.5), rebuilt
    /// as data. Kept as the runtime fallback AND as the canonical content of res://data/keyword_catalog.tres.</summary>
    public static KeywordCatalog BuildDefault()
    {
        var cat = new KeywordCatalog();
        void Add(string keyword, string name, string desc, bool hasValue = false) =>
            cat.Keywords.Add(new KeywordDef
            {
                KeywordName = keyword, DisplayName = name, Description = desc, HasValue = hasValue,
            });

        Add("Charge", "冲锋", "部署当回合即可移动与攻击。");
        Add("Assault", "突袭", "部署当回合可攻击,但不能移动。");
        Add("Swift", "疾行", "每回合可移动的格数提升。", hasValue: true);
        Add("Range", "射程", "可攻击 N 步(横纵相加)内的任意敌人,越过其他随从;仅当目标能反击到你(在其射程/相邻内)时才吃反击。", hasValue: true);
        Add("Taunt", "嘲讽", "与其相邻的敌方随从必须优先攻击它。");
        Add("HoldFast", "坚守", "本回合未移动时,受到的伤害 -1。");
        Add("Trample", "践踏", "近战攻击时,对目标周围相邻的所有单位(含友方)也造成等量伤害。");
        Add("CheapShot", "偷袭", "近战攻击不受反击。");
        Add("Shield", "持盾", "免疫下一次受到的伤害。");
        Add("Garrison", "驻防", "位于己方底线行时 +1/+1。");
        Add("Leap", "跃障", "移动时可跨过一个随从,直线跳跃 2 格。");
        Add("PackTactics", "围猎", "近战攻击时,每有一个友方与目标相邻,伤害 +2(可叠加)。");
        Add("Hidden", "潜行", "潜行:不能被敌方指令/战吼选中(范围/AOE 仍会命中);攻击后现形。");
        Add("Emplacement", "架设", "架设:不能移动;受到指令/技能/战吼等效果伤害 +1(普通攻击不加)。");
        Add("Pierce", "贯穿", "贯穿:远程攻击时,同时对目标正后方一格的随从(不分敌我)造成等额伤害。");
        Add("Blessing", "福泽", "福泽:与其相邻的友方随从受到的伤害 -1(不含自身,可与坚守叠加)。");
        Add("Guardian", "守护", "守护:与其相邻的友方随从将要受到的伤害,转移到它身上承受(享受它自身的减伤)。");
        Add("Rooted", "定身", "定身:本回合不能移动(跃障 / 额外移动力也无效),但仍可攻击与反击。");
        Add("MoltenSword", "熔岩巨剑", "熔岩巨剑:装备后 +3 攻击、射程 2、贯穿(永久)。");
        Add("KindleImmune", "免疫薪炎", "免疫薪炎:免疫薪炎(spell.kindle)伤害;但被薪炎命中仍会加速自身成长。");
        Add("KindleAegis", "不焚", "不焚:你的所有友方随从免疫薪炎(spell.kindle)伤害,不限距离、含自身;被薪炎命中仍会加速成长。");
        Add("SpellWard", "法术护体", "法术护体:抵挡下一次敌方指令/战吼效果(伤害归零 / 指向失效),之后消耗。");
        return cat;
    }
}
