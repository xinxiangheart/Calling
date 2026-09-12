/// <summary>
/// 跨场景传递的局外状态。
///
/// 现在只有一个「选中的角色」，用静态类最省事，不需要在场景里摆一个对象。
/// 等局外数据变多（银行金币、天赋点、精灵好感）再考虑改成可序列化的存档对象，
/// 那时这里会变成存档对象的门面，调用点不用动。
/// </summary>
public static class GameSession
{
    /// <summary>玩家在选人界面选定的角色。默认战士，没进过选人界面也能直接开打。</summary>
    public static CharacterId SelectedCharacter = CharacterId.Warrior;

    /// <summary>
    /// 角色的中文名。
    /// UI、日志、以后存档都用这一份映射，避免「格雷」这几个字在四五个地方各写一遍。
    /// </summary>
    public static string DisplayName(CharacterId id)
    {
        switch (id)
        {
            case CharacterId.Rogue: return "莉拉";
            case CharacterId.Alchemist: return "诺姆";
            case CharacterId.Gambler: return "卡珊德拉";
            default: return "格雷";
        }
    }
}