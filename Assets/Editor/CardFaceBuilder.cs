using DungeonCardDesign.EditorTools;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 生成卡面预制体（3:4）和一张预览场景。
///
/// 为什么不手写 .prefab：预制体里全是组件 GUID 和 fileID 交叉引用，手写等于猜，
/// 交给 Unity 序列化才靠谱 —— 和 SceneBuilder 走同一条路。
///
/// 版式数字全在下面那组常量里，改版式只动常量，不用翻布局代码。
/// 重复生成是就地更新：打开现有预制体内容、按名字复用物体，
/// 所以挂在 CardFaceView 上的数据色（品级 / 类别 / 词条颜色）手动调过会留着；
/// 结构色（卡面底色、缎带、描述区底色）是图案的一部分，每次按下面的常量重设。
/// </summary>
public static class CardFaceBuilder
{
    const string PrefabFolder = "Assets/Prefabs/Cards";
    const string PrefabPath = PrefabFolder + "/CardFace.prefab";
    const string PreviewScenePath = "Assets/Scenes/CardPreview.unity";

    // ---- 3:4 卡面版式：坐标相对卡面左上角，单位是 1920x1080 参考分辨率下的像素 ----
    const float CardWidth = 360f;
    const float CardHeight = 480f;
    const float InnerInset = 10f;        // 卡面底色离外框留的空 = 外框的厚度
    const float SideInset = 16f;         // 卡图 / 描述区离卡面底色左右留的空
    const float BannerTop = 6f;
    const float BannerWidth = 250f;
    const float BannerHeight = 62f;
    const float ArtTop = 78f;
    const float ArtHeight = 168f;
    const float BadgeWidth = 52f;
    const float BadgeHeight = 46f;
    const float BodyPadding = 12f;       // 描述文字离描述区边框的内边距
    const float BodyBottomMargin = 14f;

    // ---- 结构色：图案本身的颜色，不随数据变 ----
    static readonly Color InnerColor = new Color(0.169f, 0.184f, 0.212f);       // #2B2F36 卡面底色
    static readonly Color BannerColor = new Color(0.788f, 0.804f, 0.831f);      // #C9CDD4 银灰缎带
    static readonly Color BannerTextColor = new Color(0.137f, 0.149f, 0.169f);  // #23262B
    static readonly Color BodyColor = new Color(0.361f, 0.314f, 0.267f);        // #5C5044 描述区（深棕，白字看得清）
    static readonly Color BodyTextColor = new Color(0.929f, 0.937f, 0.949f);    // #EDEFF2
    static readonly Color BadgeOutlineColor = new Color(0.078f, 0.086f, 0.102f, 0.9f);

    // ---- 预览场景 ----
    const float PreviewCardScale = 0.8f;
    const float PreviewSpacing = 24f;

    [MenuItem("Tools/Dungeon Card Design/生成卡面预制体（Cards/CardFace）")]
    public static void BuildPrefabMenu()
    {
        BuildPrefab();
    }

    [MenuItem("Tools/Dungeon Card Design/生成卡面预览场景（含样例卡）")]
    public static void BuildPreviewScene()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("正在播放", "先退出播放模式再生成场景。", "好");
            return;
        }

        if (!SceneBuilder.EnsureTmpEssentials())
        {
            return;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            EditorUtility.DisplayDialog("还没有卡面预制体",
                "先跑一次「生成卡面预制体（Cards/CardFace）」，再来生成预览。", "好");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        List<Sample> samples = BuildSamples();

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SceneBuilder.EnsureCamera();
        SceneBuilder.EnsureEventSystem();
        GameObject canvas = SceneBuilder.EnsureCanvas();

        // 横排一行居中。按 PreviewCardScale 缩过，5 张正好放得下 1920 宽
        float cardWidth = CardWidth * PreviewCardScale;
        float totalWidth = samples.Count * cardWidth + (samples.Count - 1) * PreviewSpacing;
        float startX = -totalWidth * 0.5f + cardWidth * 0.5f;

        for (int i = 0; i < samples.Count; i++)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, canvas.transform);
            var rect = (RectTransform)instance.transform;
            SceneBuilder.Place(rect, new Vector2(0.5f, 0.5f), new Vector2(CardWidth, CardHeight),
                new Vector2(startX + i * (cardWidth + PreviewSpacing), 0f));
            rect.localScale = Vector3.one * PreviewCardScale;
            instance.name = "Card_" + (i + 1) + "_" + samples[i].card.DisplayName;

            var view = instance.GetComponent<CardFaceView>();
            if (view != null)
            {
                view.Apply(samples[i].card, samples[i].level);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, PreviewScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "预览场景已生成",
            "已生成：" + PreviewScenePath + "\n\n" +
            "样例来自真实导出的卡（" + CardTreeLoader.DefaultExportDirectory + "/test_cards.json），\n" +
            "另外补了两张临时的技能 / 特殊卡，用来对比徽章形状。\n\n" +
            "改版式：动 CardFaceBuilder 里的常量，重新跑一次「生成卡面预制体」。",
            "好");
    }

    /// <summary>生成 / 更新卡面预制体。</summary>
    static void BuildPrefab()
    {
        EnsurePrefabFolder();

        bool exists = File.Exists(PrefabPath);
        GameObject root = exists
            ? PrefabUtility.LoadPrefabContents(PrefabPath)
            : new GameObject("CardFace", typeof(RectTransform));

        try
        {
            var view = SceneBuilder.EnsureComponent<CardFaceView>(root);
            Layout((RectTransform)root.transform, view);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            if (exists)
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "卡面预制体已更新",
            "预制体：" + PrefabPath + "\n" +
            "尺寸：" + CardWidth + " x " + CardHeight + "（3:4）\n\n" +
            "版式和结构色已重设；挂在 CardFaceView 上的品级 / 类别 / 词条颜色保持不动。\n" +
            "想看效果就跑一次「生成卡面预览场景」。",
            "好");
    }

    /// <summary>把预制体里的五个部件摆好并接到 CardFaceView 上。</summary>
    static void Layout(RectTransform root, CardFaceView view)
    {
        // 卡面根：3:4，锚在中心。将来放手牌 / 槽位由父物体摆位置
        SceneBuilder.Place(root, new Vector2(0.5f, 0.5f), new Vector2(CardWidth, CardHeight), Vector2.zero);

        float innerWidth = CardWidth - InnerInset * 2f;
        float innerHeight = CardHeight - InnerInset * 2f;

        // ---- 外框：整张卡。运行时颜色随品级变，这里填的是编辑器里的预览值 ----
        Image frame = SceneBuilder.AddBackground(
            SceneBuilder.Region("Frame", root, 0f, 0f, CardWidth, CardHeight), view.rarityCommon);

        RectTransform inner = SceneBuilder.Region("Inner", root, InnerInset, InnerInset, innerWidth, innerHeight);
        SceneBuilder.AddBackground(inner, InnerColor);

        // ---- 卡名横幅 ----
        RectTransform bannerRect = SceneBuilder.Region("Banner", inner,
            (innerWidth - BannerWidth) * 0.5f, BannerTop, BannerWidth, BannerHeight);
        Image banner = SceneBuilder.AddBackground(bannerRect, BannerColor);

        TextMeshProUGUI bannerText = SceneBuilder.NewText(
            "BannerText", bannerRect, "卡名", 30f, TextAlignmentOptions.Center);
        SceneBuilder.Stretch((RectTransform)bannerText.transform);
        bannerText.fontStyle = FontStyles.Bold;
        bannerText.color = BannerTextColor;

        // ---- 卡图：立绘位，没图时是纯色 ----
        float artWidth = innerWidth - SideInset * 2f;
        Image art = SceneBuilder.AddBackground(
            SceneBuilder.Region("Art", inner, SideInset, ArtTop, artWidth, ArtHeight), view.artPlaceholderColor);

        // ---- 类别徽章：压在卡图和描述区的交界上 ----
        float artBottom = ArtTop + ArtHeight;
        RectTransform badgeRect = SceneBuilder.Region("Badge", inner,
            (innerWidth - BadgeWidth) * 0.5f, artBottom - BadgeHeight * 0.5f, BadgeWidth, BadgeHeight);

        // 先补渲染器再加图形：EnsureComponent 遇到"旧预制体里已经挂着 ShapeGraphic"时
        // 会直接返回旧组件，不会触发 RequireComponent 自动补件 —— 那样形状画了也不渲染。
        SceneBuilder.EnsureComponent<CanvasRenderer>(badgeRect.gameObject);

        var badge = SceneBuilder.EnsureComponent<ShapeGraphic>(badgeRect.gameObject);
        badge.raycastTarget = false;

        // 描边让徽章在深色卡面和深色描述区上都分得出来
        var badgeOutline = SceneBuilder.EnsureComponent<Outline>(badgeRect.gameObject);
        badgeOutline.effectColor = BadgeOutlineColor;
        badgeOutline.effectDistance = new Vector2(2f, -2f);

        TextMeshProUGUI badgeText = SceneBuilder.NewText(
            "BadgeText", badgeRect, "1", 26f, TextAlignmentOptions.Center);
        SceneBuilder.Place((RectTransform)badgeText.transform, new Vector2(0.5f, 0.5f),
            new Vector2(BadgeWidth, 30f), Vector2.zero);

        // ---- 描述区：同时是「点开详情」的按钮 ----
        float bodyTop = artBottom + BadgeHeight * 0.5f + SideInset;
        RectTransform bodyRect = SceneBuilder.Region("Body", inner, SideInset, bodyTop,
            innerWidth - SideInset * 2f, innerHeight - bodyTop - BodyBottomMargin);

        Image body = SceneBuilder.AddBackground(bodyRect, BodyColor);
        // 卡面上只有这块吃点击，点它就是"看详情"
        body.raycastTarget = true;

        var detailButton = SceneBuilder.EnsureComponent<Button>(bodyRect.gameObject);
        detailButton.targetGraphic = body;
        detailButton.transition = Selectable.Transition.ColorTint;
        ColorBlock pressColors = detailButton.colors;
        pressColors.normalColor = Color.white;
        pressColors.highlightedColor = new Color(0.92f, 0.92f, 0.92f);
        pressColors.pressedColor = new Color(0.78f, 0.78f, 0.78f);
        detailButton.colors = pressColors;

        TextMeshProUGUI bodyText = SceneBuilder.NewText(
            "BodyText", bodyRect, string.Empty, 20f, TextAlignmentOptions.Top);
        Inset((RectTransform)bodyText.transform, BodyPadding);
        bodyText.color = BodyTextColor;

        // ---- 回填引用 ----
        view.frame = frame;
        view.banner = banner;
        view.bannerText = bannerText;
        view.art = art;
        view.badge = badge;
        view.badgeText = badgeText;
        view.body = body;
        view.bodyText = bodyText;
        view.detailButton = detailButton;
    }

    /// <summary>预览用的样例：真实导出的卡 + 两张临时造的卡（真实数据里还没有技能 / 特殊）。</summary>
    static List<Sample> BuildSamples()
    {
        var samples = new List<Sample>();

        string exportPath = Path.Combine(CardTreeLoader.ExportDirectoryFullPath, "test_cards.json");
        if (File.Exists(exportPath))
        {
            CanvasExport export = CardTreeLoader.Load(exportPath);
            if (export != null && export.cards != null)
            {
                // 等级错开，顺便看清三个等级的徽章数字
                for (int i = 0; i < export.cards.Length && i < 3; i++)
                {
                    samples.Add(new Sample(export.cards[i], i + 1));
                }
            }
        }
        else
        {
            Debug.LogWarning("[CardFace] 没找到 " + exportPath + "，预览里只有临时样例");
        }

        // 这两张只是为了让徽章的另外两种形状能看见，效果文本故意不写 {变量}
        samples.Add(new Sample(new CardData
        {
            id = "sample_skill",
            name = "示例技能",
            rarity = "精良",
            category = "技能",
            effect = "获得 8 点护盾。",
            keywords = new List<string> { "顽固" },
        }, 2));

        samples.Add(new Sample(new CardData
        {
            id = "sample_special",
            name = "示例特殊",
            rarity = "稀有",
            category = "特殊",
            effect = "把抽牌区最上面的一张牌放回手牌。",
        }, 3));

        return samples;
    }

    /// <summary>撑满父物体，但四周留出内边距（描述文字用）。</summary>
    static void Inset(RectTransform rect, float padding)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(padding, padding);
        rect.offsetMax = new Vector2(-padding, -padding);
    }

    static void EnsurePrefabFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
        {
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        }
        if (!AssetDatabase.IsValidFolder(PrefabFolder))
        {
            AssetDatabase.CreateFolder("Assets/Prefabs", "Cards");
        }
    }

    /// <summary>预览场景里的一张卡：数据 + 显示的等级。</summary>
    struct Sample
    {
        public readonly CardData card;
        public readonly int level;

        public Sample(CardData card, int level)
        {
            this.card = card;
            this.level = level;
        }
    }
}
