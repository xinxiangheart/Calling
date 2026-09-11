using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 单张卡牌的「战斗内升级效果」填写窗口。
    ///
    /// 入口有两个：节点上双击，或右侧编辑面板里的「打开大窗口填写」按钮。
    /// 输入是实时写回内存模型的（不用另外点保存），关掉窗口前的内容不会丢。
    /// </summary>
    public class CardUpgradeWindow : EditorWindow
    {
        CardData _card;
        Action<CardData> _onChanged;

        Label _titleLabel;
        TextField _textField;

        /// <summary>打开某张卡的升级效果填写窗口。card 为空时给出提示而不是报错。</summary>
        public static void Open(CardData card, Action<CardData> onChanged)
        {
            if (card == null)
            {
                EditorUtility.DisplayDialog("战斗内升级", "请先在左侧点选一张卡牌。", "好");
                return;
            }

            CardUpgradeWindow window = CreateInstance<CardUpgradeWindow>();
            window.titleContent = new GUIContent("战斗内升级 - " + card.DisplayName);
            window.minSize = new Vector2(440f, 300f);
            window._card = card;
            window._onChanged = onChanged;
            window.ShowUtility();
            window.Focus();
        }

        void OnEnable()
        {
            BuildUI();
        }

        void OnDisable()
        {
            _onChanged = null;
        }

        void BuildUI()
        {
            rootVisualElement.style.paddingLeft = 10f;
            rootVisualElement.style.paddingRight = 10f;
            rootVisualElement.style.paddingTop = 10f;
            rootVisualElement.style.paddingBottom = 10f;

            _titleLabel = new Label(_card != null
                ? _card.DisplayName + "    (id: " + _card.id + ")"
                : "(没有选中卡牌)");
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            rootVisualElement.Add(_titleLabel);

            var caption = new Label("战斗内升级效果：写这张牌在战斗里升级/强化后变成什么样。");
            caption.style.fontSize = 10f;
            caption.style.marginTop = 4f;
            caption.style.color = new StyleColor(new Color(0.58f, 0.62f, 0.70f));
            caption.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(caption);

            _textField = new TextField();
            _textField.multiline = true;
            _textField.style.flexGrow = 1f;
            _textField.style.minHeight = 150f;
            _textField.style.marginTop = 6f;
            _textField.value = _card != null && _card.combat_upgrade != null
                ? _card.combat_upgrade
                : string.Empty;
            _textField.RegisterValueChangedCallback(OnValueChanged);
            rootVisualElement.Add(_textField);

            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.alignItems = Align.Center;
            footer.style.marginTop = 8f;

            var autoHint = new Label("输入即时生效，直接关窗口即可");
            autoHint.style.fontSize = 10f;
            autoHint.style.color = new StyleColor(new Color(0.5f, 0.55f, 0.62f));
            footer.Add(autoHint);

            var done = new Button(Close) { text = "完成" };
            done.style.width = 72f;
            footer.Add(done);

            rootVisualElement.Add(footer);

            _textField.schedule.Execute(() => _textField.Focus());
        }

        void OnValueChanged(ChangeEvent<string> evt)
        {
            if (_card == null)
            {
                return;
            }

            _card.combat_upgrade = evt.newValue;
            if (_onChanged != null)
            {
                _onChanged(_card);
            }
        }
    }
}
