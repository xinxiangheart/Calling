using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// 一个探索格子的界面：白色底板 + 内容记号 + 玩家标记。
///
/// 子物体在这里用代码搭，不依赖预制体 —— 网格是运行时随机生成的，
/// 格数不固定，做成预制体反而多一个要维护的资产。
///
/// 没解锁的格子是整格 SetActive(false)，不是调透明度：
/// 调透明度还能看见一个白框的轮廓，等于把地图形状漏给玩家了。
/// </summary>
public class ExploreCellView : MonoBehaviour
{
    Image _background;
    TMP_Text _label;
    GameObject _playerMarker;

    /// <summary>格子中心在网格里的坐标，回调时带出去。</summary>
    public int X { get; private set; }
    public int Y { get; private set; }

    /// <summary>内容记号的图形和颜色。美术资源还没到，先靠字和颜色区分。</summary>
    static string LabelOf(ExploreCellKind kind)
    {
        switch (kind)
        {
            case ExploreCellKind.Event: return "？";
            case ExploreCellKind.Monster: return "怪";
            case ExploreCellKind.Treasure: return "宝";
            case ExploreCellKind.Exit: return "梯";
            default: return "";
        }
    }

    static Color ColorOf(ExploreCellKind kind)
    {
        switch (kind)
        {
            case ExploreCellKind.Event: return new Color(0.18f, 0.44f, 0.75f);
            case ExploreCellKind.Monster: return new Color(0.75f, 0.22f, 0.17f);
            case ExploreCellKind.Treasure: return new Color(0.72f, 0.53f, 0.04f);
            case ExploreCellKind.Exit: return new Color(0.18f, 0.55f, 0.34f);
            default: return new Color(0.6f, 0.6f, 0.6f);
        }
    }

    /// <summary>在 parent 下建一个格子。cellSize 是边长，位置由调用方摆。</summary>
    public static ExploreCellView Create(RectTransform parent, float cellSize)
    {
        var go = new GameObject("Cell", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(cellSize, cellSize);

        var view = go.AddComponent<ExploreCellView>();

        // 白色底板：也是点击的落点
        view._background = go.AddComponent<Image>();
        view._background.color = Color.white;

        // 现在没有美术，靠描边把格子从灰底上分出来
        var outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0.09f, 0.10f, 0.12f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);

        var button = go.AddComponent<Button>();
        button.targetGraphic = view._background;
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.82f, 0.82f, 0.82f);
        colors.pressedColor = new Color(0.65f, 0.65f, 0.65f);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.5f);
        button.colors = colors;

        // 内容记号
        var labelGo = new GameObject("Content", typeof(RectTransform));
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(cellSize * 0.12f, cellSize * 0.12f);
        labelRect.offsetMax = new Vector2(-cellSize * 0.12f, -cellSize * 0.12f);

        view._label = labelGo.AddComponent<TextMeshProUGUI>();
        view._label.alignment = TextAlignmentOptions.Center;
        view._label.enableWordWrapping = false;
        view._label.fontSize = cellSize * 0.42f;
        view._label.raycastTarget = false;
        TMP_FontAsset font = TMP_Settings.defaultFontAsset;
        if (font != null)
        {
            view._label.font = font;
        }

        // 玩家标记：压在内容记号上面，所以最后建
        var markerGo = new GameObject("PlayerMarker", typeof(RectTransform));
        markerGo.transform.SetParent(go.transform, false);
        var markerRect = (RectTransform)markerGo.transform;
        markerRect.anchorMin = new Vector2(0.5f, 0.5f);
        markerRect.anchorMax = new Vector2(0.5f, 0.5f);
        markerRect.pivot = new Vector2(0.5f, 0.5f);
        markerRect.sizeDelta = new Vector2(cellSize * 0.42f, cellSize * 0.42f);
        markerRect.anchoredPosition = Vector2.zero;

        var marker = markerGo.AddComponent<Image>();
        marker.color = new Color(0.91f, 0.78f, 0.31f);
        marker.raycastTarget = false;
        view._playerMarker = markerGo;

        return view;
    }

    /// <summary>绑坐标和点击。点击回调由控制器统一处理，格子自己不判断能不能走。</summary>
    public void Bind(int x, int y, UnityAction onClick)
    {
        X = x;
        Y = y;

        var button = GetComponent<Button>();
        if (button != null && onClick != null)
        {
            button.onClick.AddListener(onClick);
        }
    }

    /// <summary>按当前状态刷新。revealed 为 false 时整格隐藏。</summary>
    public void Show(ExploreCellKind kind, bool revealed, bool hasPlayer)
    {
        if (gameObject.activeSelf != revealed)
        {
            gameObject.SetActive(revealed);
        }

        if (!revealed)
        {
            return;
        }

        if (_label != null)
        {
            _label.text = LabelOf(kind);
            _label.color = ColorOf(kind);
        }

        if (_playerMarker != null && _playerMarker.activeSelf != hasPlayer)
        {
            _playerMarker.SetActive(hasPlayer);
        }
    }
}