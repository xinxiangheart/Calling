using DungeonCardDesign.EditorTools;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 一张卡面（3:4）：外框、卡名横幅、卡图、类别徽章、描述区。
///
/// 版式由 Assets/Editor/CardFaceBuilder.cs 生成，这里只管「把数据填进去」，
/// 所以以后换美术（贴图、圆角、描边）不用动这个文件。
///
/// 和策划图对齐的约定：
///   品级   -> 外框颜色
///   类别   -> 徽章形状 + 颜色（攻击 = 正三角、技能 = 倒三角，不写文字）
///   徽章里的数字 = 等级 Lv（_LvN 的 N）
///   描述区 -> 【效果】按 bindings 契约着色，词条跟在后面高亮
///   点描述区 -> 抛出事件让外面打开详情（卡面放不下战斗内升级效果）
/// </summary>
public class CardFaceView : MonoBehaviour
{
    [Header("外框（颜色随品级）")]
    public Image frame;

    [Header("卡名横幅")]
    public Image banner;
    public TMP_Text bannerText;

    [Header("卡图（立绘位，没图时是纯色占位）")]
    public Image art;
    public Color artPlaceholderColor = new Color(0.227f, 0.247f, 0.278f);   // #3A3F47

    [Header("类别徽章：形状随类别，数字是等级")]
    public ShapeGraphic badge;
    public TMP_Text badgeText;

    [Header("描述区（点它看详情）")]
    public Image body;
    public TMP_Text bodyText;

    [Header("点击入口")]
    public Button detailButton;

    [Header("配色：品级 -> 外框")]
    public Color rarityCommon = new Color(0.541f, 0.561f, 0.596f);    // 普通 #8A8F98
    public Color rarityFine = new Color(0.306f, 0.561f, 0.816f);      // 精良 #4E8FD0
    public Color rarityRare = new Color(0.608f, 0.373f, 0.816f);      // 稀有 #9B5FD0
    public Color rarityLegend = new Color(0.878f, 0.663f, 0.231f);    // 传说 #E0A93B
    public Color rarityUnknown = new Color(0.451f, 0.475f, 0.522f);   // 没填品级时兜底

    [Header("配色：类别 -> 徽章")]
    public Color categoryAttack = new Color(0.753f, 0.224f, 0.169f);  // 攻击 红 #C0392B
    public Color categorySkill = new Color(0.180f, 0.525f, 0.839f);   // 技能 蓝 #2E86D6
    public Color categoryOther = new Color(0.478f, 0.310f, 0.749f);   // 其它 紫 #7A4FBF
    public Color badgeNumberColor = new Color(0.961f, 0.827f, 0.235f);// 徽章数字 黄 #F5D33C

    [Header("配色：词条高亮")]
    public Color keywordColor = new Color(0.961f, 0.827f, 0.235f);    // 和徽章数字同色系

    /// <summary>点了详情区。详情界面还没做，先让外面接这个事件。</summary>
    public event System.Action<CardFaceView> detailRequested;

    /// <summary>当前显示的卡和等级，供详情界面取用。</summary>
    public CardData Card { get; private set; }

    public int Level { get; private set; }

    void Awake()
    {
        if (detailButton != null)
        {
            detailButton.onClick.AddListener(OnDetailClicked);
        }
    }

    /// <summary>把一张卡填进卡面。level 传 0 或负数按 1 处理。</summary>
    public void Apply(CardData card, int level)
    {
        Card = card;
        Level = Mathf.Clamp(level <= 0 ? 1 : level, 1, VariableCodec.MaxLevel);

        if (card == null)
        {
            return;
        }

        if (frame != null)
        {
            frame.color = RarityColor(card.rarity);
        }
        if (bannerText != null)
        {
            bannerText.text = card.DisplayName;
        }

        ShapeGraphic.Shape shape = ShapeFor(card.category);

        if (badge != null)
        {
            badge.CurrentShape = shape;
            badge.color = CategoryColor(card.category);
        }
        if (badgeText != null)
        {
            // 徽章里只放数字。等级靠位置和形状认，写 "Lv2" 会让卡面多出一堆小字
            badgeText.text = Level.ToString();
            badgeText.color = badgeNumberColor;

            // 三角形的重心偏底边，数字跟着挪一点才不会看着歪
            var badgeTextRect = (RectTransform)badgeText.transform;
            badgeTextRect.anchoredPosition = new Vector2(
                0f, shape == ShapeGraphic.Shape.TriangleDown ? 8f : -8f);
        }

        if (bodyText != null)
        {
            bodyText.text = BuildBodyText(card, Level);
        }
    }

    /// <summary>换立绘。传 null 就退回纯色占位。</summary>
    public void SetArt(Sprite sprite)
    {
        if (art == null)
        {
            return;
        }

        art.sprite = sprite;
        art.color = sprite != null ? Color.white : artPlaceholderColor;
    }

    /// <summary>描述区文本：效果 + 词条。</summary>
    string BuildBodyText(CardData card, int level)
    {
        string effect = null;

        if (card.bindings != null && card.bindings.effect_segments != null
            && card.bindings.effect_segments.Count > 0)
        {
            // error 段照样画出来（红色），所以这里不因为 hasError 就换别的东西
            effect = CardSegmentRenderer.ToRichText(card.bindings.effect_segments, level, out _);
        }

        if (string.IsNullOrEmpty(effect))
        {
            // 没有 bindings 的卡（纯文字卡、或改动前导出的旧 JSON）退回原文：
            // 变量不会被替换，但至少文字看得见
            effect = card.effect ?? string.Empty;
        }

        string keywords = KeywordLine(card.keywords);
        if (string.IsNullOrEmpty(keywords))
        {
            return effect;
        }
        if (string.IsNullOrEmpty(effect))
        {
            return keywords;
        }

        return effect + "\n" + keywords;
    }

    /// <summary>
    /// 词条按杀戮尖塔的做法放在描述里高亮，不另开横条 ——
    /// 卡面本来就小，再加一条会把描述挤没。
    /// </summary>
    string KeywordLine(List<string> keywords)
    {
        if (keywords == null || keywords.Count == 0)
        {
            return string.Empty;
        }

        var names = new List<string>();
        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword))
            {
                names.Add("<b>" + keyword + "</b>");
            }
        }

        if (names.Count == 0)
        {
            return string.Empty;
        }

        return "<color=" + CardSegmentPalette.ToHex(keywordColor) + ">"
            + string.Join("  ", names.ToArray()) + "</color>";
    }

    Color RarityColor(string rarity)
    {
        if (string.IsNullOrEmpty(rarity))
        {
            return rarityUnknown;
        }

        // 用 Contains 不用等值：策划写「稀有」还是「稀有卡」都得认出来
        if (rarity.Contains("传说")) return rarityLegend;
        if (rarity.Contains("稀有")) return rarityRare;
        if (rarity.Contains("史诗")) return rarityRare;   // 模板里写过「史诗」，先和稀有同色
        if (rarity.Contains("精良")) return rarityFine;
        if (rarity.Contains("普通")) return rarityCommon;
        return rarityUnknown;
    }

    static ShapeGraphic.Shape ShapeFor(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return ShapeGraphic.Shape.TriangleUp;
        }
        if (category.Contains("技能")) return ShapeGraphic.Shape.TriangleDown;
        if (category.Contains("攻击")) return ShapeGraphic.Shape.TriangleUp;
        return ShapeGraphic.Shape.Diamond;
    }

    Color CategoryColor(string category)
    {
        if (string.IsNullOrEmpty(category)) return categoryOther;
        if (category.Contains("技能")) return categorySkill;
        if (category.Contains("攻击")) return categoryAttack;
        return categoryOther;
    }

    void OnDetailClicked()
    {
        if (detailRequested != null)
        {
            detailRequested(this);
            return;
        }

        // 详情界面还没做：先打一行日志，免得点了没反应让人以为坏了
        Debug.Log("[CardFace] 打开详情：" + (Card != null ? Card.DisplayName : "?") + " Lv." + Level);
    }
}
