using System;

/// <summary>格子里是什么。数值会被随机生成的权重表用到，加新类型请往后加。</summary>
public enum ExploreCellKind
{
    Empty = 0,
    Event = 1,
    Monster = 2,
    Treasure = 3,
    Exit = 4,
}

/// <summary>
/// 一层探索的纯逻辑：格子内容、玩家位置、迷雾解锁。
///
/// 刻意不引用 UnityEngine —— 这样它能直接在 dotnet 里跑测试，
/// 不用开 Unity 就能验证「能不能走过去」「迷雾解锁的范围对不对」这类规则。
/// 界面（ExploreCellView / ExploreController）只负责把这里的状态画出来。
/// </summary>
public class ExploreGridModel
{
    /// <summary>四种普通内容各占多少权重，顺序和 ExploreCellKind 的前四项对应。</summary>
    static readonly int[] Weights = { 34, 34, 22, 8 };

    readonly ExploreCellKind[,] _kinds;
    readonly bool[,] _revealed;

    public readonly int Columns;
    public readonly int Rows;

    public int PlayerX { get; private set; }
    public int PlayerY { get; private set; }

    /// <summary>已经解锁的格数，测试和调试用。</summary>
    public int RevealedCount { get; private set; }

    /// <summary>种子。同一个种子生成的楼层完全一样，方便复现问题。</summary>
    public readonly int Seed;

    public ExploreGridModel(int columns, int rows, int seed)
    {
        if (columns < 1) columns = 1;
        if (rows < 1) rows = 1;

        Columns = columns;
        Rows = rows;
        Seed = seed;

        _kinds = new ExploreCellKind[columns, rows];
        _revealed = new bool[columns, rows];

        var random = new Random(seed);
        Generate(random);

        // 玩家随机落一格，脚下这一格强制清空：站在事件格上没法解释「触发了吗」
        PlayerX = random.Next(columns);
        PlayerY = random.Next(rows);
        _kinds[PlayerX, PlayerY] = ExploreCellKind.Empty;

        // 起手就能看到自己和四周，否则第一帧是一块白板，玩家不知道能往哪走
        RevealAround(PlayerX, PlayerY);
    }

    void Generate(Random random)
    {
        int total = 0;
        for (int i = 0; i < Weights.Length; i++)
        {
            total += Weights[i];
        }

        for (int x = 0; x < Columns; x++)
        {
            for (int y = 0; y < Rows; y++)
            {
                _kinds[x, y] = (ExploreCellKind)Roll(random, total);
            }
        }

        // 保证一定有一个向下的出口。先做一层，出口现在只是个记号，
        // 但楼层里没有它，以后接上下楼逻辑时会找不到锚点
        int exitX = random.Next(Columns);
        int exitY = random.Next(Rows);
        _kinds[exitX, exitY] = ExploreCellKind.Exit;
    }

    static int Roll(Random random, int total)
    {
        int pick = random.Next(total);
        for (int i = 0; i < Weights.Length; i++)
        {
            if (pick < Weights[i])
            {
                return i;
            }
            pick -= Weights[i];
        }
        return 0;
    }

    public bool InBounds(int x, int y)
    {
        return x >= 0 && x < Columns && y >= 0 && y < Rows;
    }

    public ExploreCellKind KindAt(int x, int y)
    {
        return _kinds[x, y];
    }

    public bool IsRevealed(int x, int y)
    {
        return _revealed[x, y];
    }

    public bool HasPlayer(int x, int y)
    {
        return x == PlayerX && y == PlayerY;
    }

    /// <summary>是不是玩家上下左右紧挨着的那四格（斜角不算）。</summary>
    public bool IsAdjacent(int x, int y)
    {
        return Math.Abs(x - PlayerX) + Math.Abs(y - PlayerY) == 1;
    }

    /// <summary>
    /// 能不能走这一格。
    ///
    /// 没解锁的不让走：迷雾里等于不知道那边有什么，点得到就走等于迷雾白做了。
    /// 走过的格子解锁后就一直留着，所以回头路天然是通的。
    /// </summary>
    public bool CanMoveTo(int x, int y)
    {
        if (!InBounds(x, y)) return false;
        if (HasPlayer(x, y)) return false;
        if (!IsAdjacent(x, y)) return false;
        return IsRevealed(x, y);
    }

    /// <summary>走一格。走不动就返回 false，调用方不用先问一次 CanMoveTo。</summary>
    public bool TryMove(int x, int y)
    {
        if (!CanMoveTo(x, y))
        {
            return false;
        }

        PlayerX = x;
        PlayerY = y;

        // 站定之后才解锁四周，所以走到新格子会接着露出来一圈
        RevealAround(x, y);
        return true;
    }

    /// <summary>解锁一格自己和上下左右四格。重复解锁不会重复计数。</summary>
    void RevealAround(int x, int y)
    {
        Reveal(x, y);
        Reveal(x - 1, y);
        Reveal(x + 1, y);
        Reveal(x, y - 1);
        Reveal(x, y + 1);
    }

    void Reveal(int x, int y)
    {
        if (!InBounds(x, y) || _revealed[x, y])
        {
            return;
        }

        _revealed[x, y] = true;
        RevealedCount++;
    }
}