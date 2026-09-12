using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 用代码画简单几何形状（三角形 / 菱形）。
///
/// 卡面徽章不需要贴图：形状要跟着数据变（攻击 = 正三角、技能 = 倒三角），
/// 直接画网格比准备一堆 sprite 省事，缩放时也不会糊。
/// 颜色就用 Graphic 自带的 color，用法和 Image 一样。
/// </summary>
// 必须显式要求 CanvasRenderer：UGUI 里只有 Image 这类具体实现自己声明了它，
// Graphic / MaskableGraphic 只声明了 RectTransform。少了渲染器，
// OnPopulateMesh 照样被调用，但顶点送不到画布上 —— 表现就是"形状看不见"。
[RequireComponent(typeof(CanvasRenderer))]
public class ShapeGraphic : MaskableGraphic
{
    /// <summary>徽章形状。类别靠形状区分，不靠文字。</summary>
    public enum Shape
    {
        /// <summary>尖朝上：攻击类。</summary>
        TriangleUp = 0,

        /// <summary>尖朝下：技能类。</summary>
        TriangleDown = 1,

        /// <summary>菱形：其它类别（形状还没定，先用它占位）。</summary>
        Diamond = 2,
    }

    [SerializeField]
    Shape _shape = Shape.TriangleUp;

    /// <summary>换形状要标脏重画，所以包一层属性而不是直接暴露字段。</summary>
    public Shape CurrentShape
    {
        get { return _shape; }
        set
        {
            if (_shape == value)
            {
                return;
            }

            _shape = value;
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect rect = GetPixelAdjustedRect();
        float left = rect.xMin;
        float right = rect.xMax;
        float bottom = rect.yMin;
        float top = rect.yMax;
        float midX = rect.center.x;
        float midY = rect.center.y;

        switch (_shape)
        {
            case Shape.TriangleDown:
                AddTriangle(vh, new Vector2(left, top), new Vector2(right, top), new Vector2(midX, bottom));
                break;

            case Shape.Diamond:
                // 菱形是上下两个三角形拼出来的
                AddTriangle(vh, new Vector2(midX, top), new Vector2(right, midY), new Vector2(midX, bottom));
                AddTriangle(vh, new Vector2(midX, top), new Vector2(midX, bottom), new Vector2(left, midY));
                break;

            default:
                AddTriangle(vh, new Vector2(midX, top), new Vector2(right, bottom), new Vector2(left, bottom));
                break;
        }
    }

    void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c)
    {
        int start = vh.currentVertCount;
        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = color;

        vertex.position = a;
        vh.AddVert(vertex);
        vertex.position = b;
        vh.AddVert(vertex);
        vertex.position = c;
        vh.AddVert(vertex);

        vh.AddTriangle(start, start + 1, start + 2);
    }
}
