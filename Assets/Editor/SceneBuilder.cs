using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 一键生成 MainMenu / Battle 两个场景，并把它们登记进 Build Settings。
///
/// 为什么不直接把 .unity 文件写进仓库：场景 YAML 里满是 UGUI / TMP 组件的
/// 脚本 GUID 和 fileID 交叉引用，手写等于猜，猜错就是一个打不开的场景。
/// 交给 Unity 自己序列化才靠谱，代价是按一次菜单。
///
/// 放在 Assets/Editor 根目录（而不是 Assets/Editor/CardTree）是有意的：
/// 那边有 DungeonCardDesign.Editor.asmdef，进了那个程序集就看不见
/// Assembly-CSharp 里的 MainMenuController。
/// </summary>
public static class SceneBuilder
{
    const string SceneFolder = "Assets/Scenes";
    const string MainMenuPath = SceneFolder + "/MainMenu.unity";
    const string BattlePath = SceneFolder + "/Battle.unity";

    // UI 全按 1920x1080 参考分辨率摆
    static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

    // ---- 选人界面尺寸（都按 1920x1080 参考分辨率，原点在画布中心）----
    const float SlideWidth = 420f;       // 一张卡的宽度
    const float SlideHeight = 620f;
    const float PortraitHeight = 560f;   // 立绘占掉卡的大部分
    const float NameOffsetY = -312f;     // 名字挂在立绘正下方
    const float FrameWidth = 260f;       // 立绘两侧的信息框
    const float FrameHeight = 300f;
    const float FrameOffsetX = 356f;

    // ---- 探索界面尺寸（同样按 1920x1080，位置都从区域左上角往下量）----
    const float LeftColumnWidth = 278f;   // 左栏宽度，对齐参考图上那条竖线
    const float TopBandHeight = 72f;      // 顶上那一条：经验条
    const float BottomBandHeight = 320f;  // 底下那一条：物品栏
    const float DividerThickness = 3f;

    // 左栏内部：三条格子长条
    const float BarLeft = 20f;
    const float BarWidth = 238f;
    const float BarHeight = 26f;
    const float FirstBarTop = 116f;
    const float BarGapY = 72f;

    // 五张行动卡第一行的位置，第二行按卡高自动往下排
    const float ActionCardTop = 624f;
    const float LevelTreeTop = 948f;

    // 中央探索区要一块不透明灰底，上下两条先留空（透出相机底色）
    static readonly Color ExploreBackdrop = new Color(0.30f, 0.31f, 0.33f, 1f);
    static readonly Color DividerColor = new Color(0.42f, 0.44f, 0.47f, 1f);
    static readonly Color HealthColor = new Color(0.588f, 0.176f, 0.165f);  // #962D2A
    static readonly Color HungerColor = new Color(1.000f, 0.647f, 0.569f);  // #FFA591
    static readonly Color LightColor = new Color(0.910f, 0.784f, 0.314f);   // #E8C850
    static readonly Color ExpColor = new Color(0.30f, 0.72f, 0.34f);

    [MenuItem("Tools/Dungeon Card Design/生成 MainMenu（会重建）并更新 Battle")]
    public static void BuildAll()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("正在播放", "先退出播放模式再生成场景。", "好");
            return;
        }

        if (!EnsureTmpEssentials())
        {
            return;
        }

        // 重建场景会关掉当前场景，先让用户把自己的改动存了
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        if (File.Exists(MainMenuPath)
            && !EditorUtility.DisplayDialog(
                "MainMenu 已存在",
                "MainMenu.unity 已经存在，继续会重建它（场景里手动加的东西会丢）。\n\n" +
                "Battle.unity 不会重建，只做就地更新，手动加的东西会保留。",
                "继续", "取消"))
        {
            return;
        }

        if (!AssetDatabase.IsValidFolder(SceneFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Scenes");
        }

        BuildMainMenu();
        BuildBattle();
        RegisterBuildSettings();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 生成完停在 MainMenu，直接点 Play 就能试
        EditorSceneManager.OpenScene(MainMenuPath);

        EditorUtility.DisplayDialog(
            "生成完成",
            "MainMenu 已重建：\n" + MainMenuPath +
            "\nBattle 已就地更新：\n" + BattlePath +
            "\n\nBuild Settings 已登记，MainMenu 是索引 0。\n" +
            "如果场景里的文字是空白的，再点一次本菜单即可。",
            "好");
    }

    /// <summary>
    /// 只更新 Battle.unity（探索界面），其它场景不碰。
    ///
    /// 单独留一个入口：MainMenu 那边已经就地改造过轮播，为了改探索界面
    /// 把两个场景都重建一遍既没必要，也容易让人心里没底。
    /// </summary>
    [MenuItem("Tools/Dungeon Card Design/生成探索场景（就地更新 Battle.unity）")]
    public static void BuildBattleOnly()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("正在播放", "先退出播放模式再生成场景。", "好");
            return;
        }

        if (!EnsureTmpEssentials())
        {
            return;
        }

        // 会切换场景，先让用户把自己的改动存了
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        // 不再问「要不要覆盖」：这一趟只重建自己生成的那批物体，
        // 手动加的背景图、改过的参数都留着
        BuildBattle();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "更新完成",
            "探索界面已就地更新：\n" + BattlePath + "\n\n" +
            "手动加的东西、改过的参数都还在，只重建自己生成的物体\n" +
            "（槽位 / 行动卡槽数量变少的话，多余的旧格子要手动删）。\n\n" +
            "按 Play 就能试：点相邻的白色格子走，没解锁的格子看不见。\n" +
            "生命值下面会显示 当前/上限，播放时按 - / = 可以扣血回血看看是不是动态的。\n" +
            "如果场景里的文字是空白的，先跑一次「重建中文字体」。",
            "好");
    }

    // ==================================================================
    // MainMenu
    // ==================================================================

    static void BuildMainMenu()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EnsureCamera();
        EnsureEventSystem();

        var controllerGo = new GameObject("MainMenuController");
        var controller = controllerGo.AddComponent<MainMenuController>();

        GameObject canvas = EnsureCanvas();

        // ---- 开始界面 ----
        GameObject startPanel = NewPanel("StartPanel", canvas.transform);

        TextMeshProUGUI title = NewText("Title", startPanel.transform, "Calling", 96f,
            TextAlignmentOptions.Center);
        Place((RectTransform)title.transform, new Vector2(0.5f, 0.5f),
            new Vector2(900f, 130f), new Vector2(0f, 300f));

        Button btnNewGame = NewButton("BtnNewGame", startPanel.transform, "新游戏", new Vector2(0f, 60f));
        Button btnContinue = NewButton("BtnContinue", startPanel.transform, "继续", new Vector2(0f, -40f));
        Button btnSettings = NewButton("BtnSettings", startPanel.transform, "设置", new Vector2(0f, -140f));
        Button btnQuit = NewButton("BtnQuit", startPanel.transform, "退出", new Vector2(0f, -240f));

        // ---- 选人界面 ----
        GameObject characterPanel = NewPanel("CharacterPanel", canvas.transform);
        BuildCharacterPanel(characterPanel, controller);

        // ---- 回填控制器引用 ----
        controller.startPanel = startPanel;
        controller.characterPanel = characterPanel;
        controller.btnNewGame = btnNewGame;
        controller.btnContinue = btnContinue;
        controller.btnSettings = btnSettings;
        controller.btnQuit = btnQuit;

        // 一进场只显示开始界面；MainMenuController.Start 里还会再设一次，
        // 这里设是为了在编辑器里点开场景时看到的就是实际会显示的样子
        characterPanel.SetActive(false);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, MainMenuPath);
    }

    // ==================================================================
    // 选人界面（三档轮播）
    // ==================================================================

    /// <summary>
    /// 建出选人面板里的全部内容：标题、轮播、两个切换箭头、「开始游戏」，
    /// 并把引用回填给控制器。
    ///
    /// 「重新生成场景」和「就地改造现有场景」都走这一个函数 —— 两条路各写一份的话，
    /// 改造出来的和重新生成出来的迟早会长得不一样。
    /// </summary>
    public static void BuildCharacterPanel(GameObject panel, MainMenuController controller)
    {
        // 就地改造时面板里已经有旧东西（四张平铺的卡 + 确认按钮），先清干净
        ClearChildren(panel.transform);

        TextMeshProUGUI title = NewText("Title", panel.transform, "选择角色", 56f, TextAlignmentOptions.Center);
        Place((RectTransform)title.transform, new Vector2(0.5f, 1f),
            new Vector2(900f, 80f), new Vector2(0f, -40f));

        // 轮播舞台：一个铺满的容器，四张卡都挂在它下面，各自用 anchoredPosition 定位
        GameObject stage = NewPanel("CharacterCarousel", panel.transform);

        // 角色数据只有 CharacterDatabase 那一份，选人界面和探索界面都读它
        CharacterInfo[] roster = CharacterDatabase.All;
        var slides = new CharacterCardView[roster.Length];
        for (int i = 0; i < roster.Length; i++)
        {
            slides[i] = BuildCharacterSlide(roster[i], stage.transform);
        }

        Button btnPrev = NewArrowButton("BtnPrev", panel.transform, "◀", new Vector2(-560f, -330f));
        Button btnNext = NewArrowButton("BtnNext", panel.transform, "▶", new Vector2(560f, -330f));
        Button btnStart = NewButton("BtnStart", panel.transform, "开始游戏", new Vector2(0f, -460f));

        // 摆成「第一张在正中间」的样子。用的是和运行时同一套数字，
        // 所以不进播放模式，编辑器里看到的就是跑起来的样子
        CharacterCarouselLayout.ApplyImmediate(slides, 0);

        controller.characterSlides = slides;
        controller.btnPrev = btnPrev;
        controller.btnNext = btnNext;
        controller.btnStart = btnStart;
    }

    /// <summary>
    /// 一张角色卡：立绘 + 名字 + 左右两个信息框。
    ///
    /// 轴心放在立绘中心（而不是卡片左上角），转盘就只需要改 anchoredPosition
    /// 和缩放，名字和信息框会自动跟着立绘一起走。
    /// </summary>
    static CharacterCardView BuildCharacterSlide(CharacterInfo info, Transform parent)
    {
        GameObject go = NewUI(info.ObjectName, parent);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(SlideWidth, SlideHeight);

        var view = go.AddComponent<CharacterCardView>();
        view.characterId = info.id;

        // 整卡底板：既是立绘的衬底，也是点击的落点
        var background = go.AddComponent<Image>();
        background.color = Darken(info.color, 0.28f);
        background.raycastTarget = true;

        var button = go.AddComponent<Button>();
        button.targetGraphic = background;
        view.button = button;

        // 两层透明度各管一件事：整卡那层把两侧压暗，信息区那层只让正中间露名字和信息框
        view.group = go.AddComponent<CanvasGroup>();

        // 立绘占位：一块纯色，等美术替成 Sprite
        GameObject portrait = NewUI("Portrait", go.transform);
        var portraitImage = portrait.AddComponent<Image>();
        portraitImage.color = info.color;
        portraitImage.raycastTarget = false;
        Place((RectTransform)portrait.transform, new Vector2(0.5f, 0.5f),
            new Vector2(SlideWidth - 24f, PortraitHeight), Vector2.zero);
        view.portrait = portraitImage;

        // 名字不能叫 info：会遮住同名的 CharacterInfo 参数，读起来还以为是同一个东西
        GameObject infoRoot = NewUI("InfoRoot", go.transform);
        Stretch((RectTransform)infoRoot.transform);
        view.infoGroup = infoRoot.AddComponent<CanvasGroup>();

        view.nameText = NewText("NameText", infoRoot.transform, info.displayName, 40f, TextAlignmentOptions.Center);
        Place((RectTransform)view.nameText.transform, new Vector2(0.5f, 0.5f),
            new Vector2(SlideWidth, 56f), new Vector2(0f, NameOffsetY));

        BuildInfoFrame("LeftFrame", infoRoot.transform, -FrameOffsetX, "属性",
            "力量 " + info.strength + "\n体质 " + info.constitution);
        BuildInfoFrame("RightFrame", infoRoot.transform, FrameOffsetX, "特性",
            info.title + "\n被动：" + info.passive + "\n难度：" + info.difficulty);

        return view;
    }

    /// <summary>立绘旁边的信息框：半透明底板 + 标题 + 正文，左右各一个。</summary>
    static void BuildInfoFrame(string name, Transform parent, float x, string header, string body)
    {
        GameObject frame = NewUI(name, parent);
        Place((RectTransform)frame.transform, new Vector2(0.5f, 0.5f),
            new Vector2(FrameWidth, FrameHeight), new Vector2(x, 0f));

        var image = frame.AddComponent<Image>();
        image.color = new Color(0.10f, 0.12f, 0.16f, 0.92f);
        image.raycastTarget = false;   // 别挡住点立绘

        // 还没有美术资源，框线先用 Outline 顶一下
        var outline = frame.AddComponent<Outline>();
        outline.effectColor = new Color(1f, 0.82f, 0.35f, 0.35f);
        outline.effectDistance = new Vector2(3f, -3f);

        TextMeshProUGUI headerText = NewText("Header", frame.transform, header, 26f, TextAlignmentOptions.Center);
        headerText.color = new Color(1f, 0.82f, 0.35f);
        Place((RectTransform)headerText.transform, new Vector2(0.5f, 1f),
            new Vector2(FrameWidth - 32f, 40f), new Vector2(0f, -18f));

        TextMeshProUGUI bodyText = NewText("Body", frame.transform, body, 24f, TextAlignmentOptions.TopLeft);
        bodyText.lineSpacing = 14f;
        Place((RectTransform)bodyText.transform, new Vector2(0.5f, 1f),
            new Vector2(FrameWidth - 40f, FrameHeight - 82f), new Vector2(0f, -66f));
    }

    /// <summary>
    /// 切换箭头。直接用字形画（◀ ▶），不依赖美术资源。
    /// 这两个码点得字体里有：NotoSansSC 有，LiberationSans 没有 ——
    /// 换字体的时候记得回来看一眼。
    /// </summary>
    static Button NewArrowButton(string name, Transform parent, string glyph, Vector2 anchoredPosition)
    {
        Button button = NewButton(name, parent, glyph, anchoredPosition);
        ((RectTransform)button.transform).sizeDelta = new Vector2(120f, 120f);

        var image = button.GetComponent<Image>();
        if (image != null)
        {
            image.color = new Color(0.16f, 0.18f, 0.24f, 0.9f);
        }

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.fontSize = 56f;
        }
        return button;
    }

    /// <summary>
    /// 清空一个节点的所有子物体。
    ///
    /// 用 DestroyImmediate 而不是 Destroy：Destroy 要等下一帧才真的删掉，
    /// 而我们紧接着就要往同一个父节点下面建新东西，不立刻清掉会混在一起。
    /// </summary>
    static void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(parent.GetChild(i).gameObject);
        }
    }

    // ==================================================================
    // Battle（探索界面）
    // ==================================================================

    /// <summary>
    /// 搭探索界面：左栏角色状态 / 右上经验条 / 右中探索网格 / 右下物品栏。
    ///
    /// 这个场景以后要兼做战斗界面和事件界面，所以现在就把区域分清楚 ——
    /// 左栏的角色状态、底下的物品栏本来就是共用的，将来换中间那一块就行。
    /// </summary>
    static void BuildBattle()
    {
        // 就地更新，不是推平重建：Battle.unity 已经存在就打开它接着改，
        // 手动加的东西（左栏、物品栏里的背景图之类）和调过的参数都要留着
        Scene scene = File.Exists(BattlePath)
            ? EditorSceneManager.OpenScene(BattlePath, OpenSceneMode.Single)
            : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        EnsureCamera();
        EnsureEventSystem();

        // 进场只打一行日志，用来确认跨场景带过来的角色是对的
        GameObject starterGo = FindOrCreateRoot("BattleStarter");
        EnsureComponent<BattleStarter>(starterGo);

        GameObject canvas = EnsureCanvas();

        // 复用场景里那一个 ExploreController，不新建：列数、种子、开局数值
        // 这些手动调过的东西都挂在它身上
        GameObject controllerGo = FindOrCreateRoot("ExploreController");
        var controller = EnsureComponent<ExploreController>(controllerGo);

        float rightWidth = ReferenceResolution.x - LeftColumnWidth;

        // 两道分隔线就是参考图上那条竖线和横线，也是三个区域的分界
        AddBackground(Region("VerticalDivider", canvas.transform, LeftColumnWidth, 0f,
            DividerThickness, ReferenceResolution.y), DividerColor);
        AddBackground(Region("HorizontalDivider", canvas.transform, LeftColumnWidth,
            ReferenceResolution.y - BottomBandHeight, rightWidth, DividerThickness), DividerColor);

        // ---- 左栏：角色状态 ----
        BuildLeftColumn(Region("LeftColumn", canvas.transform, 0f, 0f,
            LeftColumnWidth, ReferenceResolution.y), controller);

        // ---- 右上：经验条。这一条的背景按要求先留空 ----
        RectTransform topBand = Region("TopBand", canvas.transform, LeftColumnWidth, 0f,
            rightWidth, TopBandHeight);
        controller.expBar = BuildSegmentedBar("ExpBar", topBand, 24f, 20f, 1380f, 30f, ExpColor);

        // ---- 右中：探索区。不透明灰底，格子白色 ----
        RectTransform exploreArea = Region("ExploreArea", canvas.transform, LeftColumnWidth, TopBandHeight,
            rightWidth, ReferenceResolution.y - TopBandHeight - BottomBandHeight);
        // 灰底要吃掉点击：不然点到格子之间的缝会漏到画布底下
        AddBackground(exploreArea, ExploreBackdrop).raycastTarget = true;
        controller.gridRoot = exploreArea;

        // ---- 右下：物品栏。这一条的背景也先留空 ----
        RectTransform bottomBand = Region("BottomBand", canvas.transform, LeftColumnWidth,
            ReferenceResolution.y - BottomBandHeight, rightWidth, BottomBandHeight);
        BuildInventory(bottomBand);

        // 编辑器里先把面板填一遍，免得生成完是一片占位文字，非要按播放才看得出对不对
        controller.ApplyPanel();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, BattlePath);
    }

    // ==================================================================
    // 场景零件
    // ==================================================================

    /// <summary>
    /// 按「离父级左上角多远、多大」摆一块区域。
    /// 界面全按 1920x1080 的绝对像素来摆，比算锚点比例好对，也好看懂。
    ///
    /// 同名区域已存在就直接复用（就地生成的关键，见 FindOrCreateUI）。
    /// </summary>
    static RectTransform Region(string name, Transform parent, float left, float top, float width, float height)
    {
        RectTransform rect = FindOrCreateUI(name, parent);
        Place(rect, new Vector2(0f, 1f), new Vector2(width, height), new Vector2(left, -top));
        return rect;
    }

    /// <summary>
    /// 就地生成的关键：同名的子物体已经存在就用它，没有才新建。
    ///
    /// 这样重复生成不会叠出第二套，手动挂在这个父物体下的东西
    /// （比如左栏和物品栏里手动放的背景图）也不会被清掉。
    /// </summary>
    static RectTransform FindOrCreateUI(string name, Transform parent)
    {
        Transform existing = parent.Find(name);
        if (existing is RectTransform rect)
        {
            return rect;
        }

        return (RectTransform)NewUI(name, parent).transform;
    }

    /// <summary>场景根物体同名就复用，没有才新建。</summary>
    static GameObject FindOrCreateRoot(string name)
    {
        foreach (GameObject go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (go.name == name)
            {
                return go;
            }
        }

        return new GameObject(name);
    }

    /// <summary>
    /// 复用同一个物体时组件只能加一次：已经有了就拿现成的。
    /// 直接 AddComponent 会撞上 Unity 对重复组件的报错。
    /// </summary>
    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T existing = go.GetComponent<T>();
        return existing != null ? existing : go.AddComponent<T>();
    }

    /// <summary>给一块区域加底色。默认不吃点击，免得挡住底下的按钮。</summary>
    static Image AddBackground(RectTransform rect, Color color)
    {
        var image = EnsureComponent<Image>(rect.gameObject);
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    /// <summary>生命 / 饥饿 / 光照 / 经验共用的格子长条。</summary>
    static SegmentedBar BuildSegmentedBar(string name, Transform parent, float left, float top,
        float width, float height, Color fill)
    {
        RectTransform rect = Region(name, parent, left, top, width, height);

        // 先摆好尺寸再加组件：SegmentedBar 会按 RectTransform 的宽度反推格宽，
        // 顺序反了它读到的是 0，格宽就算不出来
        var bar = EnsureComponent<SegmentedBar>(rect.gameObject);
        bar.fillColor = fill;
        bar.defaultWidth = width;
        bar.defaultHeight = height;
        bar.Refresh();
        return bar;
    }

    static void BuildLeftColumn(RectTransform left, ExploreController controller)
    {
        // 名称 + 等级角标
        TextMeshProUGUI nameText = NewText("NameText", left, "格雷", 40f, TextAlignmentOptions.Left);
        Place((RectTransform)nameText.transform, new Vector2(0f, 1f),
            new Vector2(170f, 52f), new Vector2(20f, -44f));
        controller.nameText = nameText;

        RectTransform badge = Region("LevelBadge", left, 196f, 56f, 62f, 34f);
        AddBackground(badge, new Color(0.16f, 0.18f, 0.24f, 1f));
        TextMeshProUGUI levelText = NewText("LevelText", badge, "Lv.1", 22f, TextAlignmentOptions.Center);
        Stretch((RectTransform)levelText.transform);
        controller.levelText = levelText;

        // 三条长条：生命 / 饥饿 / 光照。
        // 只有生命值是一整条，饥饿和光照保持格子样式
        controller.healthBar = BuildSegmentedBar("HealthBar", left, BarLeft, FirstBarTop,
            BarWidth, BarHeight, HealthColor);
        controller.healthBar.style = SegmentedBar.Style.Continuous;
        controller.healthBar.emptyColor = Color.black;   // 被扣掉的那一段涂黑
        // 上一次生成留下的分段格子要清掉，否则会一直挂在它下面（虽然看不见）
        controller.healthBar.ClearAndRebuild();

        // 生命值下面那行「当前/上限」
        TextMeshProUGUI healthText = NewText("HealthText", left, "0/0", 24f, TextAlignmentOptions.Right);
        Place((RectTransform)healthText.transform, new Vector2(0f, 1f),
            new Vector2(BarWidth, 28f), new Vector2(BarLeft, -(FirstBarTop + BarHeight + 6f)));
        controller.healthText = healthText;

        controller.hungerBar = BuildSegmentedBar("HungerBar", left, BarLeft, FirstBarTop + BarGapY,
            BarWidth, BarHeight, HungerColor);
        controller.lightBar = BuildSegmentedBar("LightBar", left, BarLeft, FirstBarTop + BarGapY * 2f,
            BarWidth, BarHeight, LightColor);

        // 力量 / 体质：上面是标签，下面是数值
        BuildStatColumn(left, "力", 60f, controller, true);
        BuildStatColumn(left, "体", 164f, controller, false);

        BuildActionCardSlots(left);

        // 等级树按钮：现在只打日志，界面以后再补
        Button levelTree = NewButton("BtnLevelTree", left, "等级树", Vector2.zero);
        Place((RectTransform)levelTree.transform, new Vector2(0f, 1f),
            new Vector2(140f, 72f), new Vector2(69f, -LevelTreeTop));
        controller.levelTreeButton = levelTree;
    }

    /// <summary>力量 / 体质各占一列：上面方框里写「力」「体」，下面方框写数值。</summary>
    static void BuildStatColumn(RectTransform left, string label, float x,
        ExploreController controller, bool isStrength)
    {
        TextMeshProUGUI caption = NewText("StatLabel_" + label, left, label, 34f, TextAlignmentOptions.Center);
        Place((RectTransform)caption.transform, new Vector2(0f, 1f),
            new Vector2(56f, 46f), new Vector2(x, -350f));

        TextMeshProUGUI value = NewText("StatValue_" + label, left, "0", 36f, TextAlignmentOptions.Center);
        Place((RectTransform)value.transform, new Vector2(0f, 1f),
            new Vector2(56f, 52f), new Vector2(x, -462f));

        if (isStrength)
        {
            controller.strengthText = value;
        }
        else
        {
            controller.constitutionText = value;
        }
    }

    /// <summary>
    /// 局外带的五张行动卡。参考图上就是分两行摆的（上三下二）——
    /// 左栏只有 278 宽，排成一行五张会小到看不清。
    /// </summary>
    static void BuildActionCardSlots(RectTransform left)
    {
        const float cardWidth = 72f;
        const float cardHeight = 92f;
        const float gap = 8f;
        const float rowGap = 26f;

        int[] countPerRow = { 3, 2 };
        float top = ActionCardTop;

        for (int row = 0; row < countPerRow.Length; row++)
        {
            int count = countPerRow[row];
            float total = count * cardWidth + (count - 1) * gap;
            float x = (LeftColumnWidth - total) * 0.5f;   // 每行各自居中

            for (int i = 0; i < count; i++)
            {
                RectTransform card = Region("ActionCard_" + (row + 1) + "_" + (i + 1), left,
                    x + i * (cardWidth + gap), top, cardWidth, cardHeight);
                AddBackground(card, new Color(0.14f, 0.16f, 0.21f, 1f));
                var outline = EnsureComponent<Outline>(card.gameObject);
                outline.effectColor = new Color(0.45f, 0.47f, 0.52f, 1f);
                outline.effectDistance = new Vector2(2f, -2f);
            }

            top += cardHeight + rowGap;
        }
    }

    /// <summary>
    /// 物品栏：基础 2 行 x 10 列。
    ///
    /// 整块居中摆，行高按 3 排的量留了位置（现在只填 2 排），
    /// 所以以后加第三排、或者往两侧各加几列，都还有地方放，不用重排版面。
    /// </summary>
    static void BuildInventory(RectTransform band)
    {
        const int columns = 10;
        const int rows = 2;
        const float slotWidth = 110f;
        const float slotHeight = 90f;
        const float gap = 8f;

        float totalWidth = columns * slotWidth + (columns - 1) * gap;
        float totalHeight = rows * slotHeight + (rows - 1) * gap;
        float left = (band.rect.width - totalWidth) * 0.5f;
        float top = (band.rect.height - totalHeight) * 0.5f;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                RectTransform slot = Region("Slot_" + (row + 1) + "_" + (column + 1), band,
                    left + column * (slotWidth + gap), top + row * (slotHeight + gap),
                    slotWidth, slotHeight);
                AddBackground(slot, new Color(0.17f, 0.18f, 0.22f, 1f));
                var outline = EnsureComponent<Outline>(slot.gameObject);
                outline.effectColor = new Color(0.35f, 0.37f, 0.42f, 1f);
                outline.effectDistance = new Vector2(2f, -2f);
            }
        }
    }

    /// <summary>
    /// 相机：没有才建。已经有就只补 AudioListener，不动它的设置 ——
    /// 手动调过的背景色不该被生成流程冲掉。
    /// </summary>
    static void EnsureCamera()
    {
        GameObject go = FindOrCreateRoot("Main Camera");

        if (go.GetComponent<Camera>() == null)
        {
            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            camera.orthographic = true;
        }

        if (go.tag != "MainCamera")
        {
            go.tag = "MainCamera";
        }

        // Overlay 画布不需要相机渲染，但没相机 Unity 会一直刷警告
        EnsureComponent<AudioListener>(go);
    }

    /// <summary>UI 按钮要靠它才能收到点击，场景里必须有一个。</summary>
    static void EnsureEventSystem()
    {
        GameObject go = FindOrCreateRoot("EventSystem");
        EnsureComponent<EventSystem>(go);

        // 项目用的是旧输入系统（activeInputHandler = 0），所以是 StandaloneInputModule。
        // 已经有别的输入模块（比如手动换成新输入系统）就不插手，两个模块会互相打架
        if (go.GetComponent<BaseInputModule>() == null)
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    /// <summary>
    /// 画布：就地复用现有的，缺件补齐。
    /// 缩放参数每次生成都会按 1920x1080 重设 —— 版面是按这个尺寸摆的绝对像素，
    /// 这里被改过就对不齐了。
    /// </summary>
    static GameObject EnsureCanvas()
    {
        GameObject go = FindOrCreateRoot("Canvas");
        if (go.GetComponent<RectTransform>() == null)
        {
            go.AddComponent<RectTransform>();
        }

        EnsureComponent<Canvas>(go).renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = EnsureComponent<CanvasScaler>(go);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = ReferenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        EnsureComponent<GraphicRaycaster>(go);
        return go;
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    /// <summary>新建一个铺满父级的面板。</summary>
    static GameObject NewPanel(string name, Transform parent)
    {
        GameObject go = NewUI(name, parent);
        Stretch((RectTransform)go.transform);
        return go;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>锚点和轴心设成同一点，这样 anchoredPosition 就是「离这个锚点多远」。</summary>
    static void Place(RectTransform rt, Vector2 anchor, Vector2 size, Vector2 anchoredPosition)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPosition;
    }

    static TextMeshProUGUI NewText(
        string name, Transform parent, string content, float size, TextAlignmentOptions alignment)
    {
        GameObject go = FindOrCreateUI(name, parent).gameObject;
        var text = EnsureComponent<TextMeshProUGUI>(go);
        text.text = content;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;   // 文字不该挡住按钮点击

        // 刚导入 TMP 基础资源时，组件可能还没抓到默认字体，这里补一刀，
        // 免得用户面对一屏空白不知道哪里错了
        TMP_FontAsset font = TMP_Settings.defaultFontAsset;
        if (font != null)
        {
            text.font = font;
        }
        return text;
    }

    /// <summary>按钮 = 底板 Image + Button + 一个铺满的居中文字。</summary>
    static Button NewButton(string name, Transform parent, string label, Vector2 anchoredPosition)
    {
        GameObject go = FindOrCreateUI(name, parent).gameObject;
        Place((RectTransform)go.transform, new Vector2(0.5f, 0.5f),
            new Vector2(420f, 84f), anchoredPosition);

        var image = EnsureComponent<Image>(go);
        image.color = new Color(0.18f, 0.20f, 0.26f, 1f);

        var button = EnsureComponent<Button>(go);
        button.targetGraphic = image;

        TextMeshProUGUI text = NewText("Label", go.transform, label, 32f, TextAlignmentOptions.Center);
        Stretch((RectTransform)text.transform);
        return button;
    }

    /// <summary>把主题色压暗，用来当卡片底色，和立绘拉开层次。</summary>
    static Color Darken(Color color, float factor)
    {
        return new Color(color.r * factor, color.g * factor, color.b * factor, 1f);
    }

    // ==================================================================
    // Build Settings
    // ==================================================================

    static void RegisterBuildSettings()
    {
        var scenes = new List<EditorBuildSettingsScene>
        {
            // MainMenu 放索引 0 —— 它就是启动场景
            new EditorBuildSettingsScene(MainMenuPath, true),
            new EditorBuildSettingsScene(BattlePath, true),
        };

        // 用户自己加过的其他场景保留在后面，不要冲掉
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (existing.path == MainMenuPath || existing.path == BattlePath)
            {
                continue;
            }
            scenes.Add(new EditorBuildSettingsScene(existing.path, existing.enabled));
        }

        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // ==================================================================
    // TMP 前置检查
    // ==================================================================

    /// <summary>
    /// TMP 的文字要能显示，必须先导入 TMP Essential Resources，
    /// 否则场景里所有文字都是空白。没导入就问一句，顺手导掉。
    /// </summary>
    static bool EnsureTmpEssentials()
    {
        if (Resources.Load<TMP_Settings>("TMP Settings") != null)
        {
            return true;
        }

        if (!EditorUtility.DisplayDialog(
                "缺少 TMP 基础资源",
                "TextMeshPro 的 Essential Resources 还没导入。\n" +
                "不导入的话，生成的场景里所有文字都是空白。\n\n现在导入吗？",
                "导入", "取消"))
        {
            return false;
        }

        TMP_PackageResourceImporter.ImportResources(true, false, false);
        AssetDatabase.Refresh();
        return true;
    }
}
