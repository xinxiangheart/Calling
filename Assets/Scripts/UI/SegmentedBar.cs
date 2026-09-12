using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 数值长条：饥饿度、光照值、经验条是一格一格的，生命值是一整条。
///
/// 两种样式共用同一套参数（segmentCount 是上限、filledCount 是当前值），
/// 调用方不用为样式分叉：
///   分段 = 画 segmentCount 个小格，前 filledCount 个亮
///   整条 = 一条按比例填充的长条，没填到的部分用 emptyColor 画（生命值要黑色）
///
/// 带 ExecuteAlways 是有意的：格数要在 Inspector 里改完立刻看到效果，
/// 不然改个数字得进播放模式才知道版面对不对。
///
/// 每格的宽度由自身 RectTransform 的宽度反推（总宽 = 格数 * 格宽 + 间隔），
/// 所以改格数只会改「每格多粗」，整条不会被撑破版面。
/// </summary>
[ExecuteAlways]
public class SegmentedBar : MonoBehaviour
{
    /// <summary>长条怎么画。</summary>
    public enum Style
    {
        /// <summary>一格一格（饥饿、光照、经验）。</summary>
        Segmented = 0,

        /// <summary>一整条按比例填充（生命值）。</summary>
        Continuous = 1,
    }

    [Header("样式")]
    [Tooltip("分段 = 一格一格；整条 = 按比例填充")]
    public Style style = Style.Segmented;

    [Header("格数")]
    [Tooltip("一共几格。整条样式下它是上限，用来算填充比例")]
    public int segmentCount = 10;

    [Tooltip("亮起来几格 / 当前值。超过上限会被夹住")]
    public int filledCount = 10;

    [Header("外观")]
    public float spacing = 3f;
    public Color fillColor = new Color(0.85f, 0.24f, 0.24f);
    [Tooltip("没填到的部分：分段样式是空格子，整条样式是被扣掉的那一段")]
    public Color emptyColor = new Color(1f, 1f, 1f, 0.14f);

    [Header("兜底尺寸")]
    [Tooltip("首帧 RectTransform 还没算出来时用它顶着，平时不用管")]
    public float defaultWidth = 238f;
    public float defaultHeight = 26f;

    // 子物体池只增不减：删对象会留 Undo 记录、还会把场景弄脏，
    // 而格数通常只会被调大调小来回改
    readonly List<Image> _segments = new List<Image>();

    // 整条样式的两块：底（被扣掉的部分）和填充（剩余的部分）
    Image _track;
    Image _fill;

    bool _refreshing;

    const string SegmentPrefix = "Seg_";
    const string TrackName = "Bar_Track";
    const string FillName = "Bar_Fill";

    void OnEnable()
    {
        Refresh();
    }

    void OnValidate()
    {
        Refresh();
    }

    /// <summary>运行时改当前值用这个，会自动重排颜色。</summary>
    public void SetValue(int filled)
    {
        filledCount = filled;
        Refresh();
    }

    public void Refresh()
    {
        // Refresh 会改子物体的 activeSelf，那会再触发一次 OnValidate，不挡一下会无限递归
        if (_refreshing)
        {
            return;
        }

        // 预制体资产里不建子物体：那里建出来的东西会被存进资产文件，
        // 而且导入预制体时建对象属于未定义行为。场景里的对象才处理。
        if (!gameObject.scene.IsValid())
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (_segments.Count == 0)
            {
                AdoptExistingSegments();
            }

            if (style == Style.Continuous)
            {
                // 整条样式另走一条路。这里直接 return，外面的 finally 照样会把
                // _refreshing 复位，不会把后面的刷新全堵死
                RefreshContinuous();
                return;
            }

            // 整条样式留下的两块要收起来，否则两套画法会叠在一起
            EnsureContinuousParts();
            SetPartsActive(false);

            int count = Mathf.Max(0, segmentCount);
            int filled = Mathf.Clamp(filledCount, 0, count);

            var self = transform as RectTransform;
            float total = self != null ? self.rect.width : 0f;
            float height = self != null ? self.rect.height : 0f;
            if (total <= 1f)
            {
                total = defaultWidth;
            }
            if (height <= 1f)
            {
                height = defaultHeight;
            }

            // 格宽是从总宽反推的：减少格数就是每格变粗，不是整条变短
            float segmentWidth = count > 0
                ? (total - spacing * (count - 1)) / count
                : total;

            while (_segments.Count < count)
            {
                _segments.Add(CreateSegment(_segments.Count));
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                Image segment = _segments[i];
                if (segment == null)
                {
                    continue;
                }

                bool visible = i < count;
                if (segment.gameObject.activeSelf != visible)
                {
                    segment.gameObject.SetActive(visible);
                }
                if (!visible)
                {
                    continue;
                }

                segment.color = i < filled ? fillColor : emptyColor;

                var rect = (RectTransform)segment.transform;
                rect.sizeDelta = new Vector2(segmentWidth, height);
                rect.anchoredPosition = new Vector2(i * (segmentWidth + spacing), 0f);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>拿自身 RectTransform 的尺寸；首帧还没算出来时用兜底值顶着。</summary>
    void ResolveSize(out float total, out float height)
    {
        var self = transform as RectTransform;
        total = self != null ? self.rect.width : 0f;
        height = self != null ? self.rect.height : 0f;
        if (total <= 1f)
        {
            total = defaultWidth;
        }
        if (height <= 1f)
        {
            height = defaultHeight;
        }
    }

    /// <summary>
    /// 整条样式：底铺满整宽，填充按 filledCount / segmentCount 的比例。
    ///
    /// 比例是算成浮点的，不像分段那样一格一格取整 —— 生命值上限通常比格子数大得多
    /// （比如 76/100），取整就看不出少了一点。
    /// </summary>
    void RefreshContinuous()
    {
        EnsureContinuousParts();
        SetPartsActive(true);

        int count = Mathf.Max(0, segmentCount);
        int filled = Mathf.Clamp(filledCount, 0, count);

        float total;
        float height;
        ResolveSize(out total, out height);

        float ratio = count > 0 ? (float)filled / count : 0f;

        _track.color = emptyColor;
        _track.rectTransform.sizeDelta = new Vector2(total, height);
        _track.rectTransform.anchoredPosition = Vector2.zero;

        _fill.color = fillColor;
        _fill.rectTransform.sizeDelta = new Vector2(total * ratio, height);
        _fill.rectTransform.anchoredPosition = Vector2.zero;
    }

    /// <summary>整条样式要用的两块：底 + 填充。缺了就建，顺序保证填充压在底上面。</summary>
    void EnsureContinuousParts()
    {
        if (_track == null)
        {
            _track = EnsurePart(TrackName);
        }
        if (_fill == null)
        {
            _fill = EnsurePart(FillName);
        }

        // 只在顺序不对时才动层级：SetSiblingIndex 会碰场景，能少碰就少碰
        if (_fill.transform.GetSiblingIndex() < _track.transform.GetSiblingIndex())
        {
            _fill.transform.SetSiblingIndex(_track.transform.GetSiblingIndex() + 1);
        }
    }

    /// <summary>收起 / 显示整条样式的两块。</summary>
    void SetPartsActive(bool active)
    {
        if (_track != null && _track.gameObject.activeSelf != active)
        {
            _track.gameObject.SetActive(active);
        }
        if (_fill != null && _fill.gameObject.activeSelf != active)
        {
            _fill.gameObject.SetActive(active);
        }
    }

    /// <summary>整条样式的一块：锚在左边，宽度就是「填了多少」，所以改值只改宽度。</summary>
    Image EnsurePart(string name)
    {
        Transform child = transform.Find(name);
        GameObject go = child != null ? child.gameObject : null;
        if (go == null)
        {
            go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, false);
        }

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);

        var image = go.GetComponent<Image>();
        if (image == null)
        {
            image = go.AddComponent<Image>();
        }
        image.raycastTarget = false;
        return image;
    }

    /// <summary>
    /// 域重载之后 _segments 会空掉，但子物体还留在场景里。
    /// 先按名字把它们认回来，否则每次重编译都会又叠一套新的上去。
    /// </summary>
    void AdoptExistingSegments()
    {
        _segments.Clear();

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (!child.name.StartsWith(SegmentPrefix))
            {
                continue;
            }

            var image = child.GetComponent<Image>();
            if (image != null)
            {
                _segments.Add(image);
            }
        }

        // 名字里的序号就是顺序，别依赖 childCount 的先后
        _segments.Sort(delegate (Image a, Image b)
        {
            return SegmentIndex(a.name).CompareTo(SegmentIndex(b.name));
        });
    }

    static int SegmentIndex(string name)
    {
        int index;
        if (name.Length > SegmentPrefix.Length
            && int.TryParse(name.Substring(SegmentPrefix.Length), out index))
        {
            return index;
        }
        return 0;
    }

    Image CreateSegment(int index)
    {
        var go = new GameObject(SegmentPrefix + index.ToString("00"), typeof(RectTransform));
        go.transform.SetParent(transform, false);

        // 锚在左中：每格的位置就是「离左边缘多远」，不随父物体宽度变化跳来跳去
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);

        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    /// <summary>
    /// 把子物体全清掉再重建。池子和实际对不上（手动删过、或者换了样式）时用它。
    ///
    /// 只能由菜单流程这类「安全的时机」调用：Unity 不允许在 OnValidate 期间
    /// DestroyImmediate，所以样式改成整条时，刷新只能把旧格子 SetActive(false) 藏起来。
    /// </summary>
    [ContextMenu("清空并重建")]
    public void ClearAndRebuild()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child.name.StartsWith(SegmentPrefix) || child.name.StartsWith("Bar_"))
            {
                DestroyImmediate(child.gameObject);
            }
        }

        _segments.Clear();
        _track = null;
        _fill = null;
        Refresh();
    }
}
