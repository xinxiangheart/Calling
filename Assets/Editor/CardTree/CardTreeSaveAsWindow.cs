using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 「保存 / 另存为」的命名小窗口。
    ///
    /// Unity 没有自带的输入框对话框，用 EditorUtility.SaveFilePanel 又会
    /// 把用户带到任意目录、还能存到 Resources 之外，所以自己做一个只填名字的窗口。
    /// </summary>
    public class CardTreeSaveAsWindow : EditorWindow
    {
        const float WindowHeight = 138f;

        string _defaultName = "新卡树";
        Action<string> _onConfirm;

        TextField _nameField;
        Label _hintLabel;

        /// <summary>打开命名窗口。确认后会带着清洗过的名字回调 onConfirm。</summary>
        public static void Open(string defaultName, Action<string> onConfirm)
        {
            CardTreeSaveAsWindow window = CreateInstance<CardTreeSaveAsWindow>();
            window.titleContent = new GUIContent("保存卡树");
            window.minSize = new Vector2(380f, WindowHeight);
            window.maxSize = new Vector2(2000f, WindowHeight);
            window._defaultName = defaultName;
            window._onConfirm = onConfirm;
            window.ShowUtility();
            window.Focus();
        }

        void OnEnable()
        {
            BuildUI();
        }

        void OnDisable()
        {
            // 窗口关掉后不再持有回调，避免意外触发
            _onConfirm = null;
        }

        void BuildUI()
        {
            rootVisualElement.style.paddingLeft = 10f;
            rootVisualElement.style.paddingRight = 10f;
            rootVisualElement.style.paddingTop = 10f;
            rootVisualElement.style.paddingBottom = 10f;

            var caption = new Label("卡树名称（保存到 " + CardTreeStore.TreeDirectoryAssetPath + "/ 下）");
            caption.style.fontSize = 10f;
            caption.style.color = new StyleColor(new Color(0.58f, 0.62f, 0.70f));
            rootVisualElement.Add(caption);

            _nameField = new TextField();
            _nameField.value = _defaultName;
            _nameField.isDelayed = true;
            _nameField.RegisterValueChangedCallback(evt => UpdateHint());
            _nameField.RegisterCallback<KeyDownEvent>(OnKeyDown);
            rootVisualElement.Add(_nameField);

            _hintLabel = new Label();
            _hintLabel.style.fontSize = 10f;
            _hintLabel.style.marginTop = 4f;
            _hintLabel.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(_hintLabel);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 8f;

            var cancel = new Button(Close) { text = "取消" };
            cancel.style.width = 72f;
            buttons.Add(cancel);

            var confirm = new Button(Confirm) { text = "保存" };
            confirm.style.width = 72f;
            confirm.style.marginLeft = 6f;
            buttons.Add(confirm);

            rootVisualElement.Add(buttons);

            UpdateHint();
            _nameField.schedule.Execute(() => _nameField.Focus());
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                Confirm();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                Close();
                evt.StopPropagation();
            }
        }

        void UpdateHint()
        {
            if (_hintLabel == null || _nameField == null)
            {
                return;
            }

            string safeName = CardTreeStore.SanitizeName(_nameField.value);
            if (string.IsNullOrEmpty(safeName))
            {
                _hintLabel.text = "名字不能为空";
                return;
            }

            string target = CardTreeStore.FullPathForName(safeName);
            _hintLabel.text = File.Exists(target)
                ? "同名文件已存在，保存会覆盖：" + Path.GetFileName(target)
                : "将新建：" + Path.GetFileName(target);
        }

        void Confirm()
        {
            string safeName = CardTreeStore.SanitizeName(_nameField != null ? _nameField.value : null);
            if (string.IsNullOrEmpty(safeName))
            {
                EditorUtility.DisplayDialog("保存卡树", "卡树名字不能为空。", "好");
                return;
            }

            // 先摘下回调再 Close，否则 OnDisable 会把它清掉
            Action<string> callback = _onConfirm;
            _onConfirm = null;
            Close();
            if (callback != null)
            {
                callback(safeName);
            }
        }
    }
}
