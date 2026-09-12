using UnityEngine;

/// <summary>
/// 选人轮播的档位表：名次 → 位置 / 缩放 / 透明度。
///
/// 屏幕上一共只看到三个位置：正中间、左、右。角色多于三个时，多出来的那张
/// 停在屏幕外，等轮到它的时候再滑进来。
///
/// 这套数字放在运行时程序集里（而不是藏在编辑器工具里），是因为建场景的时候
/// 也要用同一套数字把卡片摆成初始样子 —— 两边各写一份，编辑器里看到的和跑起来
/// 看到的一定会慢慢漂移。
/// </summary>
public static class CharacterCarouselLayout
{
    /// <summary>所有卡片共用的高度，画布中心为原点。</summary>
    public const float SlideY = 110f;

    /// <summary>两侧卡片离中心的水平距离。</summary>
    public const float SideOffsetX = 640f;

    /// <summary>
    /// 藏起来的卡片停在哪儿。这个值要保证整张卡（包括立绘两边的信息框）
    /// 连一个像素都不在屏幕里，否则淡出淡入的时候会在边上闪一下。
    /// </summary>
    public const float ParkOffsetX = 1400f;

    /// <summary>相邻两个停车位之间的距离。有好几张卡同时停在屏幕外时才会用到。</summary>
    public const float ParkSpacing = 300f;

    public const float SideScale = 0.7f;
    public const float SideAlpha = 0.55f;

    /// <summary>
    /// 卡片相对正中间的名次：0 正中、-1 左、+1 右，|delta| &gt;= 2 表示轮到它藏在屏幕外。
    ///
    /// 这里不是简单取模，而是取模后再往中间折一次：四个角色时，排在中心左边那张
    /// 直接取模会得到 +3，动画就会绕着转一大圈，而不是老老实实往左挪一格。
    /// </summary>
    public static int SignedDelta(int index, int center, int count)
    {
        int delta = ((index - center) % count + count) % count;
        if (delta > count / 2)
        {
            delta -= count;
        }
        return delta;
    }

    /// <summary>这个名次是不是三个可见档位之一。false 表示该藏到屏幕外。</summary>
    public static bool IsOnStage(int delta)
    {
        return Mathf.Abs(delta) <= 1;
    }

    /// <summary>可见档位的位置。</summary>
    public static Vector2 StagePosition(int delta)
    {
        return new Vector2(delta * SideOffsetX, SlideY);
    }

    /// <summary>
    /// 屏幕外的停车位。side 传 -1 停左边，其它值停右边；slot 是「第几个停车位」，
    /// 0 是最靠里的那个。
    ///
    /// 角色超过五个时会有两三张同时停在屏幕外，全塞进同一个位置的话它们会叠在一起 ——
    /// 反正全透明看不见，但轮到其中一张该进场时，就分不清谁是谁了。
    /// </summary>
    public static Vector2 ParkPosition(int side, int slot)
    {
        float distance = ParkOffsetX + Mathf.Max(0, slot) * ParkSpacing;
        return new Vector2(side < 0 ? -distance : distance, SlideY);
    }

    /// <summary>名次对应的停车位序号：|delta| 为 2 停第 0 位，为 3 停第 1 位，以此类推。</summary>
    public static int ParkSlot(int delta)
    {
        return Mathf.Max(0, Mathf.Abs(delta) - 2);
    }

    /// <summary>按名次停车：停哪一边、第几位，一次算好。</summary>
    public static Vector2 ParkPositionForDelta(int delta)
    {
        return ParkPosition(delta < 0 ? -1 : 1, ParkSlot(delta));
    }

    public static float Scale(int delta)
    {
        return delta == 0 ? 1f : SideScale;
    }

    public static float Alpha(int delta)
    {
        if (delta == 0)
        {
            return 1f;
        }
        return IsOnStage(delta) ? SideAlpha : 0f;
    }

    /// <summary>
    /// 名字和立绘两侧的信息框只有正中间那张才露出来。
    /// 两侧只留立绘，画面才干净，一眼能看出谁是主角。
    /// </summary>
    public static float InfoAlpha(int delta)
    {
        return delta == 0 ? 1f : 0f;
    }

    /// <summary>从 x 坐标反推卡片现在在哪一侧：-1 左、0 中间、+1 右。</summary>
    public static int SideOf(float x)
    {
        if (x < -1f)
        {
            return -1;
        }
        return x > 1f ? 1 : 0;
    }

    /// <summary>这个 x 是不是屏幕外的停车位（而不是某个可见档位）。</summary>
    public static bool IsParked(float x)
    {
        return Mathf.Abs(x) > SideOffsetX * 1.5f;
    }

    /// <summary>把一张卡摆成指定状态。轮播动画每帧都会调它。</summary>
    public static void Apply(CharacterCardView slide, Vector2 position, float scale, float alpha, float infoAlpha)
    {
        if (slide == null)
        {
            return;
        }

        var rect = slide.transform as RectTransform;
        if (rect != null)
        {
            rect.anchoredPosition = position;
        }

        slide.transform.localScale = new Vector3(scale, scale, 1f);

        if (slide.group != null)
        {
            slide.group.alpha = alpha;
        }
        if (slide.infoGroup != null)
        {
            slide.infoGroup.alpha = infoAlpha;
        }
    }

    /// <summary>
    /// 不带动画，直接把整排卡摆成「center 站在正中间」的样子。
    /// 场景刚建好、以及每次进选人界面时用，省得第一帧是从默认位置飞过来的。
    /// </summary>
    public static void ApplyImmediate(CharacterCardView[] slides, int center)
    {
        if (slides == null || slides.Length == 0)
        {
            return;
        }

        for (int i = 0; i < slides.Length; i++)
        {
            if (slides[i] == null)
            {
                continue;
            }

            int delta = SignedDelta(i, center, slides.Length);
            if (IsOnStage(delta))
            {
                Apply(slides[i], StagePosition(delta), Scale(delta), Alpha(delta), InfoAlpha(delta));
            }
            else
            {
                Apply(slides[i], ParkPositionForDelta(delta), SideScale, 0f, 0f);
            }
        }
    }
}
