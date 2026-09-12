using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 探索场景的总控：生成这一层的网格、接住点击、把左栏的数值填上。
///
/// 规则全在 ExploreGridModel 里，这里只做「把状态画出来」和「把点击转成一次移动」，
/// 所以以后要换成别的表现（比如格子换成图标、加动画）不用动规则。
/// </summary>
public class ExploreController : MonoBehaviour
{
    [Header("探索网格")]
    public RectTransform gridRoot;

    [Tooltip("一行几格")]
    public int columns = 4;

    [Tooltip("一列几格")]
    public int rows = 4;

    public float cellSpacing = 8f;

    [Tooltip("网格离灰底边缘留的空")]
    public float gridPadding = 24f;

    [Tooltip("0 = 每次进场景随机一层；填别的数字可以复现同一层，方便排查")]
    public int seed = 0;

    [Header("左栏")]
    public TMP_Text nameText;
    public TMP_Text levelText;
    public TMP_Text strengthText;
    public TMP_Text constitutionText;
    public SegmentedBar healthBar;
    [Tooltip("生命值下面那行「当前/上限」")]
    public TMP_Text healthText;
    public SegmentedBar hungerBar;
    public SegmentedBar lightBar;
    public SegmentedBar expBar;
    public Button levelTreeButton;

    [Header("兜底尺寸（首帧拿不到 RectTransform 尺寸时用）")]
    public float fallbackGridWidth = 1580f;
    public float fallbackGridHeight = 640f;

    [Header("开局数值（占位，等数值表定稿）")]
    public int level = 1;
    public int health = 8;
    public int healthMax = 10;
    public int hunger = 6;
    public int hungerMax = 10;
    public int lightValue = 7;
    public int lightMax = 10;
    public int exp = 3;
    public int expMax = 20;

    [Header("调试")]
    [Tooltip("播放时按 - / = 扣一点血、回一点血，用来肉眼确认生命条和数字是动态的。接上战斗后关掉即可")]
    public bool debugHealthKeys = true;

    ExploreGridModel _model;
    ExploreCellView[,] _cells;

    void Awake()
    {
        // 按钮监听只挂一次。不能挪进 ApplyPanel：那个方法在编辑器里会被反复调用
        // （改 Inspector 数值、生成场景时），挂在那儿会越挂越多
        if (levelTreeButton != null)
        {
            levelTreeButton.onClick.AddListener(OnLevelTree);
        }
    }

    void OnValidate()
    {
        // 编辑器里改数值要立刻看到长条和数字跟着变。
        // 这里只刷数值：角色名 / 力量 / 体质来自 GameSession 的静态值，
        // 编辑器里域重载后它就回默认，刷了反而会把场景里写好的名字改掉
        ApplyNumbers();
    }

    void Update()
    {
        if (!debugHealthKeys)
        {
            return;
        }

        // 临时的手动验证手段：战斗代码接进来之后把这个开关关掉
        if (Input.GetKeyDown(KeyCode.Minus))
        {
            SetHealth(health - 1);
        }
        else if (Input.GetKeyDown(KeyCode.Equals))
        {
            SetHealth(health + 1);
        }
    }

    void Start()
    {
        int usedSeed = seed != 0 ? seed : System.Environment.TickCount;
        _model = new ExploreGridModel(columns, rows, usedSeed);

        BuildCells();
        RefreshGrid();
        ApplyPanel();

        Debug.Log("[Explore] 本层种子 " + usedSeed + "（想把这一层复现出来，就把它填进 seed）");
        Debug.Log("[Explore] 玩家起点 (" + _model.PlayerX + "," + _model.PlayerY +
                  ")，已解锁 " + _model.RevealedCount + " 格");
    }

    void BuildCells()
    {
        if (gridRoot == null)
        {
            Debug.LogError("[Explore] gridRoot 没接，格子没法生成");
            return;
        }

        // 首帧 RectTransform 的 rect 有可能还没算出来（是 0），
        // 那就用兜底尺寸，不然会算出边长 0 的格子，屏幕上什么都看不见
        float areaWidth = gridRoot.rect.width;
        float areaHeight = gridRoot.rect.height;
        if (areaWidth <= 1f)
        {
            areaWidth = fallbackGridWidth;
        }
        if (areaHeight <= 1f)
        {
            areaHeight = fallbackGridHeight;
        }

        float usableWidth = areaWidth - gridPadding * 2f;
        float usableHeight = areaHeight - gridPadding * 2f;
        float cellWidth = (usableWidth - cellSpacing * (columns - 1)) / columns;
        float cellHeight = (usableHeight - cellSpacing * (rows - 1)) / rows;
        float cell = Mathf.Max(24f, Mathf.Min(cellWidth, cellHeight));

        _cells = new ExploreCellView[columns, rows];

        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                ExploreCellView view = ExploreCellView.Create(gridRoot, cell);

                // 闭包要捕获「这一格」的下标，不能捕获循环变量，
                // 否则所有格子点下去都会当成最后一格
                int cellX = x;
                int cellY = y;
                view.Bind(cellX, cellY, delegate { OnCellClicked(cellX, cellY); });

                // y 从上往下排：第 0 行在最上面，和玩家看到的顺序一致
                var rect = (RectTransform)view.transform;
                rect.anchoredPosition = new Vector2(
                    (x - (columns - 1) * 0.5f) * (cell + cellSpacing),
                    ((rows - 1) * 0.5f - y) * (cell + cellSpacing));

                _cells[x, y] = view;
            }
        }
    }

    void OnCellClicked(int x, int y)
    {
        if (_model == null)
        {
            return;
        }

        // 走不动就什么都不做。未解锁的格子是隐藏的，根本点不到，
        // 能走到这儿的只有「原地」和「斜角」两种情况
        if (!_model.TryMove(x, y))
        {
            return;
        }

        RefreshGrid();
        Debug.Log("[Explore] 走到 (" + x + "," + y + ")：" + _model.KindAt(x, y) +
                  "，已解锁 " + _model.RevealedCount + " 格");
    }

    void RefreshGrid()
    {
        if (_model == null || _cells == null)
        {
            return;
        }

        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                _cells[x, y].Show(_model.KindAt(x, y), _model.IsRevealed(x, y), _model.HasPlayer(x, y));
            }
        }
    }

    /// <summary>把左栏整块填一遍。生成场景的编辑器代码也会调它，好让生成完立刻有内容。</summary>
    public void ApplyPanel()
    {
        CharacterInfo info = CharacterDatabase.Get(GameSession.SelectedCharacter);

        if (nameText != null)
        {
            nameText.text = info.displayName;
        }
        if (strengthText != null)
        {
            strengthText.text = info.strength.ToString();
        }
        if (constitutionText != null)
        {
            constitutionText.text = info.constitution.ToString();
        }

        ApplyNumbers();
    }

    /// <summary>把数值刷到界面上：四条长条 + 生命值下面那行「当前/上限」。</summary>
    void ApplyNumbers()
    {
        // 上限被调小的时候当前值要跟着压回来，否则界面上会出现 12/10 这种数字
        health = Mathf.Clamp(health, 0, Mathf.Max(0, healthMax));
        hunger = Mathf.Clamp(hunger, 0, Mathf.Max(0, hungerMax));
        lightValue = Mathf.Clamp(lightValue, 0, Mathf.Max(0, lightMax));
        exp = Mathf.Clamp(exp, 0, Mathf.Max(0, expMax));

        if (levelText != null)
        {
            levelText.text = "Lv." + level;
        }

        // 长条的上限以这里填的为准，改一处四条一起变
        SetBar(healthBar, healthMax, health);
        SetBar(hungerBar, hungerMax, hunger);
        SetBar(lightBar, lightMax, lightValue);
        SetBar(expBar, expMax, exp);

        if (healthText != null)
        {
            healthText.text = health + "/" + healthMax;
        }
    }

    /// <summary>直接设定当前生命值（会被夹在 0..healthMax 之间）。</summary>
    public void SetHealth(int value)
    {
        health = Mathf.Clamp(value, 0, Mathf.Max(0, healthMax));
        ApplyNumbers();
    }

    /// <summary>扣血。战斗代码调这个，生命条和下面的数字会一起动。</summary>
    public void TakeDamage(int amount)
    {
        SetHealth(health - amount);
    }

    /// <summary>回血。</summary>
    public void Heal(int amount)
    {
        SetHealth(health + amount);
    }

    static void SetBar(SegmentedBar bar, int max, int current)
    {
        if (bar == null)
        {
            return;
        }

        bar.segmentCount = max;
        bar.SetValue(current);
    }

    void OnLevelTree()
    {
        // 等级树界面还没做，先给个看得见的反馈，别让人以为按钮坏了
        Debug.Log("[Explore] 等级树界面还没做（占位按钮）");
    }
}
