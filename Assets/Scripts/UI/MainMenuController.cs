using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 主菜单：开始界面 ↔ 选人界面，选完进 Battle。
///
/// 按钮监听在 Awake 里用代码挂，而不是在场景里连线：这样场景文件里不保存
/// UnityEvent，重建场景不会把监听弄丢，改动也只看这一个文件。
///
/// 选人界面是三档轮播：正中间一张大的，左右各一张小的，角色多于三个时多出来的
/// 停在屏幕外。每个档位的位置/缩放/透明度由 CharacterCarouselLayout 算，
/// 这里只负责「转到第几张」以及把过程插值播出来。
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("面板")]
    public GameObject startPanel;
    public GameObject characterPanel;

    [Header("开始界面按钮")]
    public Button btnNewGame;
    public Button btnContinue;
    public Button btnSettings;
    public Button btnQuit;

    [Header("选人界面")]
    public CharacterCardView[] characterSlides;
    public Button btnPrev;
    public Button btnNext;
    public Button btnStart;

    [Header("轮播手感")]
    // 换一张花的时间。调大更舒缓，调小更干脆
    public float transitionDuration = 0.22f;

    // 要不要顺手支持键盘左右键（不影响鼠标操作）
    public bool useArrowKeys = true;

    /// <summary>要加载的战斗场景名，必须和 Build Settings 里登记的名字一致。</summary>
    public string battleSceneName = "Battle";

    /// <summary>现在站在正中间的是第几张。</summary>
    int _centerIndex;

    /// <summary>正在播的换位动画。换手时先 Stop 掉它，再从当前状态接着走。</summary>
    Coroutine _transition;

    void Awake()
    {
        AddClick(btnNewGame, OnNewGame);
        AddClick(btnContinue, OnContinue);
        AddClick(btnSettings, OnSettings);
        AddClick(btnQuit, OnQuit);
        AddClick(btnPrev, delegate { Step(-1); });
        AddClick(btnNext, delegate { Step(1); });
        AddClick(btnStart, OnStart);

        if (characterSlides == null)
        {
            return;
        }

        for (int i = 0; i < characterSlides.Length; i++)
        {
            if (characterSlides[i] == null)
            {
                continue;
            }

            // 闭包要捕获「这一张」的下标而不是循环变量，否则所有卡都会转到最后一张
            int captured = i;
            AddClick(characterSlides[i].button, delegate { Focus(captured, false); });
        }
    }

    void Start()
    {
        ShowStartPanel();

        // 一进场就摆好，不要从默认位置飞过来。默认停在上次选的角色上，
        // 从战斗退回来时选择不会跳回第一个
        Focus(IndexOfSelected(), true);
    }

    void Update()
    {
        if (!useArrowKeys || characterPanel == null || !characterPanel.activeInHierarchy)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            Step(-1);
        }
        else if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            Step(1);
        }
    }

    static void AddClick(Button button, UnityAction action)
    {
        if (button != null)
        {
            button.onClick.AddListener(action);
        }
    }

    void ShowStartPanel()
    {
        if (startPanel != null)
        {
            startPanel.SetActive(true);
        }
        if (characterPanel != null)
        {
            characterPanel.SetActive(false);
        }
    }

    void OnNewGame()
    {
        if (startPanel != null)
        {
            startPanel.SetActive(false);
        }
        if (characterPanel != null)
        {
            characterPanel.SetActive(true);
        }
    }

    void OnContinue()
    {
        Debug.Log("继续游戏（未实现）");
    }

    void OnSettings()
    {
        Debug.Log("设置（未实现）");
    }

    void OnQuit()
    {
#if UNITY_EDITOR
        // 编辑器里 Application.Quit() 不生效，要停播放模式
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ==================================================================
    // 选人轮播
    // ==================================================================

    int SlideCount()
    {
        return characterSlides == null ? 0 : characterSlides.Length;
    }

    /// <summary>上次选过的角色排在第几张；找不到就退回第一张。</summary>
    int IndexOfSelected()
    {
        for (int i = 0; i < SlideCount(); i++)
        {
            if (characterSlides[i] != null && characterSlides[i].characterId == GameSession.SelectedCharacter)
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>往左/往右挪一格。箭头按钮和键盘都走这里。</summary>
    void Step(int delta)
    {
        Focus(_centerIndex + delta, false);
    }

    /// <summary>
    /// 把第 index 张卡转到正中间。immediate 为 true 时不播动画（进场时用）。
    /// </summary>
    void Focus(int index, bool immediate)
    {
        int count = SlideCount();
        if (count == 0)
        {
            return;
        }

        // 左右都能绕回去，所以下标先折回 [0, count)
        index = (index % count + count) % count;

        if (immediate)
        {
            if (_transition != null)
            {
                StopCoroutine(_transition);
                _transition = null;
            }

            _centerIndex = index;
            CharacterCarouselLayout.ApplyImmediate(characterSlides, index);
        }
        else
        {
            // 点的就是正中间那张，而且没有动画在跑：什么都不用做
            if (index == _centerIndex && _transition == null)
            {
                return;
            }

            _centerIndex = index;
            if (_transition != null)
            {
                StopCoroutine(_transition);
            }
            _transition = StartCoroutine(AnimateToCenter(index));
        }

        // 选择立刻落定：刚转到哪张就按开始，用的必须就是那一张
        if (characterSlides[index] != null)
        {
            GameSession.SelectedCharacter = characterSlides[index].characterId;
        }
    }

    /// <summary>
    /// 绕场用的「退场通道」：取在所有真实停车位之外的一位。
    /// 用真实停车位的话，绕场的那张会和停在那儿的卡叠在一起。
    /// </summary>
    static int ExitLane(int count)
    {
        return CharacterCarouselLayout.ParkSlot(count / 2) + 1;
    }

    /// <summary>
    /// 把每张卡从「现在的样子」插值到「新名次该有的样子」。
    ///
    /// 起点是从 Transform 上现读的，而不是记上一次的目标值：这样动画没播完就又点一下，
    /// 会从当前插到一半的位置接着走，而不是先跳回去再走一遍。
    /// </summary>
    IEnumerator AnimateToCenter(int center)
    {
        int count = characterSlides.Length;

        var fromPos = new Vector2[count];
        var toPos = new Vector2[count];
        var fromScale = new float[count];
        var toScale = new float[count];
        var fromAlpha = new float[count];
        var toAlpha = new float[count];
        var fromInfo = new float[count];
        var toInfo = new float[count];

        // 需要绕到对面去的卡单独标出来：它两头的档位在相反的两侧，直接插值的话
        // 会从正中间横穿过去，等于从当前主角身上碾过。这种卡改走两段路线。
        var wrap = new bool[count];
        var wrapExit = new Vector2[count];
        var wrapEntry = new Vector2[count];

        for (int i = 0; i < count; i++)
        {
            CharacterCardView slide = characterSlides[i];
            if (slide == null)
            {
                continue;
            }

            var rect = slide.transform as RectTransform;
            fromPos[i] = rect != null ? rect.anchoredPosition : Vector2.zero;
            fromScale[i] = slide.transform.localScale.x;
            fromAlpha[i] = slide.group != null ? slide.group.alpha : 1f;
            fromInfo[i] = slide.infoGroup != null ? slide.infoGroup.alpha : 1f;

            int delta = CharacterCarouselLayout.SignedDelta(i, center, count);
            toScale[i] = CharacterCarouselLayout.Scale(delta);
            toAlpha[i] = CharacterCarouselLayout.Alpha(delta);
            toInfo[i] = CharacterCarouselLayout.InfoAlpha(delta);

            if (CharacterCarouselLayout.IsOnStage(delta))
            {
                toPos[i] = CharacterCarouselLayout.StagePosition(delta);

                // 轮到上场的那张：它此刻停在屏幕外，但停在哪一侧是不一定的
                // （上一轮它可能是从另一边退场的）。直接插过去会横穿整个画面。
                // 反正它现在全透明，先瞬移到要进场的那一侧再滑进来，看不出来。
                if (fromAlpha[i] <= 0.01f && CharacterCarouselLayout.IsParked(fromPos[i].x))
                {
                    fromPos[i] = CharacterCarouselLayout.ParkPositionForDelta(delta);
                }
                else
                {
                    // 从可见档位直接挪到对面的可见档位 —— 角色数是奇数时才会碰上
                    // （三张卡全站在台上，往左挪一格，最右边那张就得绕到最左边去）。
                    int fromDelta = CharacterCarouselLayout.SideOf(fromPos[i].x);
                    if (fromDelta != 0 && delta != 0 && fromDelta * delta < 0)
                    {
                        wrap[i] = true;
                        wrapExit[i] = CharacterCarouselLayout.ParkPosition(
                            fromDelta < 0 ? -1 : 1, ExitLane(count));
                        wrapEntry[i] = CharacterCarouselLayout.ParkPosition(
                            delta < 0 ? -1 : 1, ExitLane(count));
                    }
                }
            }
            else
            {
                // 轮到下场的那张：往它原来那一侧退，否则会从正中间穿过去
                int side = CharacterCarouselLayout.SideOf(fromPos[i].x);
                if (side == 0)
                {
                    side = delta < 0 ? -1 : 1;
                }
                toPos[i] = CharacterCarouselLayout.ParkPosition(
                    side, CharacterCarouselLayout.ParkSlot(delta));
            }
        }

        float duration = Mathf.Max(0.01f, transitionDuration);
        float elapsed = 0f;

        while (true)
        {
            elapsed += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(elapsed / duration);
            float eased = Mathf.SmoothStep(0f, 1f, k);

            for (int i = 0; i < count; i++)
            {
                if (characterSlides[i] == null)
                {
                    continue;
                }

                // 信息框：淡出放在前半程、淡入放在后半程。两张卡交错的那一瞬间
                // 画面里不会同时挂着两套信息框，看着才不乱。
                float infoK = toInfo[i] > fromInfo[i]
                    ? Mathf.InverseLerp(0.5f, 1f, k)
                    : Mathf.InverseLerp(0f, 0.5f, k);

                // 位置和整卡透明度：绕场的那张要分两段走，其余的照直插值
                Vector2 position;
                float alpha;

                if (wrap[i])
                {
                    // half 在两段里都从 0 走到 1。k 到 0.5 那一下卡片正好在屏幕外，
                    // 透明度也是 0，所以从一侧瞬移到另一侧看不出来。
                    float half = k < 0.5f ? k * 2f : (k - 0.5f) * 2f;
                    float halfEased = Mathf.SmoothStep(0f, 1f, half);

                    if (k < 0.5f)
                    {
                        position = Vector2.Lerp(fromPos[i], wrapExit[i], halfEased);
                        alpha = Mathf.Lerp(fromAlpha[i], 0f, halfEased);
                    }
                    else
                    {
                        position = Vector2.Lerp(wrapEntry[i], toPos[i], halfEased);
                        alpha = Mathf.Lerp(0f, toAlpha[i], halfEased);
                    }
                }
                else
                {
                    position = Vector2.Lerp(fromPos[i], toPos[i], eased);
                    alpha = Mathf.Lerp(fromAlpha[i], toAlpha[i], eased);
                }

                CharacterCarouselLayout.Apply(
                    characterSlides[i],
                    position,
                    Mathf.Lerp(fromScale[i], toScale[i], eased),
                    alpha,
                    Mathf.Lerp(fromInfo[i], toInfo[i], Mathf.SmoothStep(0f, 1f, infoK)));
            }

            if (k >= 1f)
            {
                break;
            }

            yield return null;
        }

        // 收尾对齐到精确值，免得浮点误差让卡片停在差几个像素的地方
        for (int i = 0; i < count; i++)
        {
            if (characterSlides[i] == null)
            {
                continue;
            }

            CharacterCarouselLayout.Apply(characterSlides[i], toPos[i], toScale[i], toAlpha[i], toInfo[i]);
        }

        _transition = null;
    }

    /// <summary>按下开始：带着选中的角色进战斗场景。</summary>
    void OnStart()
    {
        if (string.IsNullOrEmpty(battleSceneName))
        {
            Debug.LogError("[MainMenu] 没有配置战斗场景名");
            return;
        }

        // 场景没登记进 Build Settings 时 LoadScene 会抛异常，
        // 这里先拦一道，给一句能直接看懂的话
        if (!Application.CanStreamedLevelBeLoaded(battleSceneName))
        {
            Debug.LogError(
                "[MainMenu] 场景「" + battleSceneName +
                "」不在 Build Settings 里，请先把它加进去（File → Build Settings）");
            return;
        }

        SceneManager.LoadScene(battleSceneName);
    }
}
