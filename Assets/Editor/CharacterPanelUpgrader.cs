using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 把已经生成好的 MainMenu 场景就地改造成轮播选人界面。
///
/// 为什么不直接改 .unity 文件：场景 YAML 里全是 fileID / GUID 交叉引用，
/// 删掉四张旧卡再换上一整套新结构，手写等于赌。让 Unity 自己增删 GameObject 才靠谱，
/// 代价是按一次菜单。
///
/// 结构和 SceneBuilder.BuildCharacterPanel 共用同一份代码，所以「就地改造」和
/// 「重新生成场景」得到的界面是同一套 —— 以后调布局也只需要改那一处。
/// </summary>
public static class CharacterPanelUpgrader
{
    const string MainMenuPath = "Assets/Scenes/MainMenu.unity";

    [MenuItem("Tools/Dungeon Card Design/选人界面改造为轮播（就地改场景）")]
    public static void Upgrade()
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("正在播放", "先退出播放模式再改场景。", "好");
            return;
        }

        if (!File.Exists(MainMenuPath))
        {
            EditorUtility.DisplayDialog("找不到场景", "没有这个文件：\n" + MainMenuPath, "好");
            return;
        }

        // 先把用户手上的改动存了，别让他白干
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        // 已经开着就直接用，没开才去读文件 —— 少一次没必要的重载
        Scene scene = SceneManager.GetSceneByPath(MainMenuPath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(MainMenuPath, OpenSceneMode.Single);
        }

        MainMenuController controller = FindController(scene);
        if (controller == null)
        {
            EditorUtility.DisplayDialog(
                "场景里没有 MainMenuController",
                "这个场景不像是本工具生成的，我不动它。",
                "好");
            return;
        }

        // 面板引用理论上一定挂好了；真丢了就按名字找回来，还找不到就停手
        GameObject panel = controller.characterPanel;
        if (panel == null)
        {
            panel = FindByName(scene, "CharacterPanel");
        }
        if (panel == null)
        {
            EditorUtility.DisplayDialog("找不到选人面板", "场景里没有 CharacterPanel。", "好");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "改造选人界面",
                "只改这一个场景文件里的 CharacterPanel：把它现有的内容\n" +
                "（四张平铺的角色卡和确认按钮）换成三档轮播 + 左右箭头 + 开始游戏。\n" +
                "Canvas、相机、EventSystem、开始界面都不动，也不会新建或覆盖场景文件。\n\n" +
                "这一步不能用 Ctrl+Z 撤销，所以动手前会先备份一份。\n\n继续吗？",
                "改造", "取消"))
        {
            return;
        }

        // 先落一份备份再动手。场景文件还没进版本库，光靠 git 是没有退路的
        string backup = BackupScene();

        // 重建面板内容，并把新的引用回填给场景里那个控制器
        SceneBuilder.BuildCharacterPanel(panel, controller);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, MainMenuPath);
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "改造完成",
            "选人界面已换成三档轮播。\n\n" +
            "直接点 Play，进「新游戏」就能看到。\n" +
            "如果文字是空白的，先跑一次「重建中文字体」。\n\n" +
            "改之前的场景备份在：\n" + backup + "\n" +
            "确认效果没问题之后，可以把它删掉。",
            "好");
    }

    /// <summary>
    /// 把场景文件原样复制一份到项目根目录的 Backups/ 下，返回备份的完整路径。
    ///
    /// 备份放在 Assets/ 外面是有意的：Unity 不会去导入它，也就不会凭空多出一个 .meta。
    /// 万一改造结果不对，把这份 .bak 覆盖回 Assets/Scenes/MainMenu.unity 就能回到改前的样子。
    /// </summary>
    static string BackupScene()
    {
        // Application.dataPath 就是 <项目根>/Assets，往上一级才是项目根目录
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string folder = Path.Combine(projectRoot, "Backups");
        Directory.CreateDirectory(folder);

        string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string target = Path.Combine(folder, "MainMenu.unity." + stamp + ".bak");

        // 用绝对路径复制：MainMenuPath 是相对路径，靠的是「编辑器的工作目录正好是项目根」，
        // 复制这种会留下痕迹的操作不赌这个前提
        string source = Path.Combine(Application.dataPath, "Scenes", "MainMenu.unity");
        File.Copy(source, target, true);
        return target;
    }

    static MainMenuController FindController(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var controller = root.GetComponent<MainMenuController>();
            if (controller != null)
            {
                return controller;
            }

            // 挂在子节点上也认
            controller = root.GetComponentInChildren<MainMenuController>(true);
            if (controller != null)
            {
                return controller;
            }
        }
        return null;
    }

    static GameObject FindByName(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform found = FindDeep(root.transform, name);
            if (found != null)
            {
                return found.gameObject;
            }
        }
        return null;
    }

    static Transform FindDeep(Transform node, string name)
    {
        if (node.name == name)
        {
            return node;
        }

        for (int i = 0; i < node.childCount; i++)
        {
            Transform found = FindDeep(node.GetChild(i), name);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
