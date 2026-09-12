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

    /// <summary>角色卡的占位数据。数值是暂定的，等数值表定稿后改成读配置。</summary>
    class CharacterSpec
    {
        public CharacterId id;
        public string objectName;
        public string displayName;
        public Color color;          // 角色主题色，同时用作立绘占位和卡片底色
        public int strength;         // 力量
        public int constitution;     // 体质
        public string title;         // 定位，一行话
        public string passive;       // 被动能力
        public string difficulty;    // 难度：低 / 高
    }

    // 除了格雷的 12/12 和「装备需求 -1」是确认过的，其余都是占位。
    // 被动写「待定」而不是编一个像样的名字，是为了让缺的数据在界面上一眼看得见。
    static readonly CharacterSpec[] Characters =
    {
        new CharacterSpec
        {
            id = CharacterId.Warrior, objectName = "CharacterCard_Warrior",
            displayName = "格雷", color = new Color(0.55f, 0.16f, 0.16f),
            strength = 12, constitution = 12,
            title = "均衡 · 稳扎稳打", passive = "装备需求 -1", difficulty = "低",
        },
        new CharacterSpec
        {
            id = CharacterId.Rogue, objectName = "CharacterCard_Rogue",
            displayName = "莉拉", color = new Color(0.35f, 0.45f, 0.62f),
            strength = 16, constitution = 8,
            title = "高力低体 · 暗影", passive = "待定", difficulty = "高",
        },
        new CharacterSpec
        {
            id = CharacterId.Alchemist, objectName = "CharacterCard_Alchemist",
            displayName = "诺姆", color = new Color(0.24f, 0.52f, 0.30f),
            strength = 8, constitution = 16,
            title = "低力高体 · 菌语", passive = "待定", difficulty = "低",
        },
        new CharacterSpec
        {
            id = CharacterId.Gambler, objectName = "CharacterCard_Gambler",
            displayName = "卡珊德拉", color = new Color(0.45f, 0.30f, 0.62f),
            strength = 8, constitution = 8,
            title = "双低 · 靠金币翻盘", passive = "待定", difficulty = "高",
        },
    };

    // ---- 选人界面尺寸（都按 1920x1080 参考分辨率，原点在画布中心）----
    const float SlideWidth = 420f;       // 一张卡的宽度
    const float SlideHeight = 620f;
    const float PortraitHeight = 560f;   // 立绘占掉卡的大部分
    const float NameOffsetY = -312f;     // 名字挂在立绘正下方
    const float FrameWidth = 260f;       // 立绘两侧的信息框
    const float FrameHeight = 300f;
    const float FrameOffsetX = 356f;

    [MenuItem("Tools/Dungeon Card Design/生成 MainMenu 与 Battle 场景（会重建）")]
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

        if ((File.Exists(MainMenuPath) || File.Exists(BattlePath))
            && !EditorUtility.DisplayDialog(
                "场景已存在",
                "MainMenu.unity 或 Battle.unity 已经存在，继续会覆盖它们。\n确定重建吗？",
                "覆盖重建", "取消"))
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
            "已生成：\n" + MainMenuPath + "\n" + BattlePath +
            "\n\nBuild Settings 已登记，MainMenu 是索引 0。\n" +
            "如果场景里的文字是空白的，再点一次本菜单即可。",
            "好");
    }

    // ==================================================================
    // MainMenu
    // ==================================================================

    static void BuildMainMenu()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera();
        CreateEventSystem();

        var controllerGo = new GameObject("MainMenuController");
        var controller = controllerGo.AddComponent<MainMenuController>();

        GameObject canvas = CreateCanvas();

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

        var slides = new CharacterCardView[Characters.Length];
        for (int i = 0; i < Characters.Length; i++)
        {
            slides[i] = BuildCharacterSlide(Characters[i], stage.transform);
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
    static CharacterCardView BuildCharacterSlide(CharacterSpec spec, Transform parent)
    {
        GameObject go = NewUI(spec.objectName, parent);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(SlideWidth, SlideHeight);

        var view = go.AddComponent<CharacterCardView>();
        view.characterId = spec.id;

        // 整卡底板：既是立绘的衬底，也是点击的落点
        var background = go.AddComponent<Image>();
        background.color = Darken(spec.color, 0.28f);
        background.raycastTarget = true;

        var button = go.AddComponent<Button>();
        button.targetGraphic = background;
        view.button = button;

        // 两层透明度各管一件事：整卡那层把两侧压暗，信息区那层只让正中间露名字和信息框
        view.group = go.AddComponent<CanvasGroup>();

        // 立绘占位：一块纯色，等美术替成 Sprite
        GameObject portrait = NewUI("Portrait", go.transform);
        var portraitImage = portrait.AddComponent<Image>();
        portraitImage.color = spec.color;
        portraitImage.raycastTarget = false;
        Place((RectTransform)portrait.transform, new Vector2(0.5f, 0.5f),
            new Vector2(SlideWidth - 24f, PortraitHeight), Vector2.zero);
        view.portrait = portraitImage;

        GameObject info = NewUI("InfoRoot", go.transform);
        Stretch((RectTransform)info.transform);
        view.infoGroup = info.AddComponent<CanvasGroup>();

        view.nameText = NewText("NameText", info.transform, spec.displayName, 40f, TextAlignmentOptions.Center);
        Place((RectTransform)view.nameText.transform, new Vector2(0.5f, 0.5f),
            new Vector2(SlideWidth, 56f), new Vector2(0f, NameOffsetY));

        BuildInfoFrame("LeftFrame", info.transform, -FrameOffsetX, "属性",
            "力量 " + spec.strength + "\n体质 " + spec.constitution);
        BuildInfoFrame("RightFrame", info.transform, FrameOffsetX, "特性",
            spec.title + "\n被动：" + spec.passive + "\n难度：" + spec.difficulty);

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
    // Battle（空场景占位）
    // ==================================================================

    static void BuildBattle()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera();
        CreateEventSystem();

        var starterGo = new GameObject("BattleStarter");
        starterGo.AddComponent<BattleStarter>();

        GameObject canvas = CreateCanvas();
        TextMeshProUGUI text = NewText("PlaceholderText", canvas.transform, "战斗场景（待实现）",
            72f, TextAlignmentOptions.Center);
        Place((RectTransform)text.transform, new Vector2(0.5f, 0.5f),
            new Vector2(1200f, 140f), Vector2.zero);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, BattlePath);
    }

    // ==================================================================
    // 场景零件
    // ==================================================================

    static void CreateCamera()
    {
        var go = new GameObject("Main Camera");
        go.tag = "MainCamera";

        var camera = go.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
        camera.orthographic = true;

        // Overlay 画布不需要相机渲染，但没相机 Unity 会一直刷警告
        go.AddComponent<AudioListener>();
    }

    /// <summary>UI 按钮要靠它才能收到点击，场景里必须有一个。</summary>
    static void CreateEventSystem()
    {
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        // 项目用的是旧输入系统（activeInputHandler = 0），所以是 StandaloneInputModule
        go.AddComponent<StandaloneInputModule>();
    }

    static GameObject CreateCanvas()
    {
        var go = new GameObject("Canvas", typeof(RectTransform));

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = ReferenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();
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
        GameObject go = NewUI(name, parent);
        var text = go.AddComponent<TextMeshProUGUI>();
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
        GameObject go = NewUI(name, parent);
        Place((RectTransform)go.transform, new Vector2(0.5f, 0.5f),
            new Vector2(420f, 84f), anchoredPosition);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.18f, 0.20f, 0.26f, 1f);

        var button = go.AddComponent<Button>();
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
