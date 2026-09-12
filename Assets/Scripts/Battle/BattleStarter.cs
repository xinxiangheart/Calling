using UnityEngine;

/// <summary>
/// Battle 场景的占位启动器。
///
/// 现在只把选中的角色打到 Console，证明场景切换和跨场景数据都是通的。
/// 真正的战斗初始化（抽牌区、手牌、敌方意图）以后接在 Start 里。
/// </summary>
public class BattleStarter : MonoBehaviour
{
    void Start()
    {
        Debug.Log("进入战斗，角色：" + GameSession.DisplayName(GameSession.SelectedCharacter));
    }
}