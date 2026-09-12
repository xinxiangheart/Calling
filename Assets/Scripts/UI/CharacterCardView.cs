using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 选人轮播里的一张角色卡：立绘 + 名字 + 左右两个信息框。
///
/// 卡片自己不知道左右还有谁，也不负责「转到哪一张」——那两件事分别是
/// CharacterCarouselLayout 和 MainMenuController 的。这里只是摆放时要用的零件，
/// 以后加角色或者改成环形都不该动到这个文件。
///
/// 轴心放在立绘中心（见 SceneBuilder.BuildCharacterSlide），所以转盘只要改
/// anchoredPosition 和缩放，名字和信息框会跟着一起走。
/// </summary>
public class CharacterCardView : MonoBehaviour
{
    [Header("数据")]
    public CharacterId characterId = CharacterId.Warrior;

    // 点这张卡 = 把它转到正中间，所以整张卡都要能吃到射线
    [Header("引用")]
    public Button button;

    // 整张卡的透明度。两侧的卡压暗成半透明，藏在屏幕外的是 0
    public CanvasGroup group;

    // 名字和两侧信息框的透明度，只有正中间那张是 1
    public CanvasGroup infoGroup;

    // 立绘占位，等美术换成真图时从这里接
    public Image portrait;

    // 名字文本，摆在立绘正下方
    public TextMeshProUGUI nameText;
}
