using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 一个角色的展示信息。数值是暂定的，等数值表定稿后改成读配置。
/// </summary>
public class CharacterInfo
{
    public CharacterId id;
    public string displayName;
    public Color color;        // 主题色，同时用作立绘占位和卡片底色
    public int strength;       // 力量
    public int constitution;   // 体质
    public string title;       // 定位，一行话
    public string passive;     // 被动能力
    public string difficulty;  // 难度：低 / 高

    /// <summary>
    /// 场景里角色卡节点的名字，按枚举拼出来。
    /// 选人界面按这个名字建对象、也按这个名字找人，写成属性就不会两边对不上。
    /// </summary>
    public string ObjectName
    {
        get { return "CharacterCard_" + id; }
    }
}

/// <summary>
/// 角色数据的唯一一份。选人界面要显示名字/力体/被动，探索界面左上角也要显示同一套。
///
/// 之前这几项散在 GameSession、SceneBuilder 里各写一份 —— 三份数据一定会漂移，
/// 改个力量值得翻三个文件。现在都从这里取。
/// </summary>
public static class CharacterDatabase
{
    // 除了格雷的 12/12 和「装备需求 -1」是确认过的，其余都是占位。
    // 被动写「待定」而不是编一个像样的名字，是为了让缺的数据在界面上一眼看得见。
    public static readonly CharacterInfo[] All =
    {
        new CharacterInfo
        {
            id = CharacterId.Warrior, displayName = "格雷",
            color = new Color(0.55f, 0.16f, 0.16f),
            strength = 12, constitution = 12,
            title = "均衡 · 稳扎稳打", passive = "装备需求 -1", difficulty = "低",
        },
        new CharacterInfo
        {
            id = CharacterId.Rogue, displayName = "莉拉",
            color = new Color(0.35f, 0.45f, 0.62f),
            strength = 16, constitution = 8,
            title = "高力低体 · 暗影", passive = "待定", difficulty = "高",
        },
        new CharacterInfo
        {
            id = CharacterId.Alchemist, displayName = "诺姆",
            color = new Color(0.24f, 0.52f, 0.30f),
            strength = 8, constitution = 16,
            title = "低力高体 · 菌语", passive = "待定", difficulty = "低",
        },
        new CharacterInfo
        {
            id = CharacterId.Gambler, displayName = "卡珊德拉",
            color = new Color(0.45f, 0.30f, 0.62f),
            strength = 8, constitution = 8,
            title = "双低 · 靠金币翻盘", passive = "待定", difficulty = "高",
        },
    };

    /// <summary>按枚举取角色。找不到就退回第一个，调用方不用处理 null。</summary>
    public static CharacterInfo Get(CharacterId id)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (All[i].id == id)
            {
                return All[i];
            }
        }
        return All[0];
    }

    /// <summary>全部角色名，给下拉框之类的用。</summary>
    public static List<string> DisplayNames()
    {
        var names = new List<string>();
        for (int i = 0; i < All.Length; i++)
        {
            names.Add(All[i].displayName);
        }
        return names;
    }
}