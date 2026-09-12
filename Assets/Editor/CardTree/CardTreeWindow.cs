using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 行动卡树编辑器窗口。
    ///
    /// 三块职责：
    ///   1. 数据源：把 canvas 导出（dungeon-card-design/exports）导入进来，编辑后存成卡树（Assets/Resources/CardTrees）
    ///   2. 图上交互：右键新建卡牌、拖动位置、拉连线、删除
    ///   3. 右侧面板：编辑全部字段，以及每张卡独立的「战斗内升级」填写窗口
    ///
    /// 注意：这里刻意不写 using System.Diagnostics;
    ///       因为 UnityEngine.Debug 会和 System.Diagnostics.Debug 重名，
    ///       一旦引入这个命名空间，所有 Debug.Log 都会变成二义性编译错误。
    ///       同理不引入 UnityEditor.Experimental.GraphView，Edge 用全限定名写。
    /// </summary>
    public class CardTreeWindow : EditorWindow
    {
        // ---------------------------------------------------------------- 常量
        const string DesignRootRelativePath = "dungeon-card-design";
        const int ExportTimeoutMilliseconds = 120000;
        const float EditorPanelWidth = 344f;

        // ---------------------------------------------------------------- 视图
        CardTreeGraphView _graphView;
        DropdownField _treeDropdown;
        DropdownField _exportDropdown;
        Toggle _showGroupsToggle;
        Label _statusLabel;
        Button _exportButton;
        HelpBox _exportHint;

        VisualElement _cardSection;
        VisualElement _edgeSection;
        Label _editorEmptyHint;
        Label _missingLabel;
        TextField _rawField;
        Label _edgeInfoLabel;
        TextField _edgeLabelField;

        // 变量列表 / 效果预览 / 校验
        VisualElement _variableRowsHost;
        // 词条列表
        VisualElement _keywordRowsHost;
        Button[] _levelButtons;
        Label _previewLabel;
        Label _validationLabel;
        int _previewLevel = 1;

        // ---------------------------------------------------------------- 数据
        CanvasExport _model;
        string _currentTreeName;
        string _currentTreePath;
        CardData _selectedCard;
        UnityEditor.Experimental.GraphView.Edge _selectedEdge;
        bool _dirty;

        string[] _treeFiles = new string[0];
        string[] _exportFiles = new string[0];

        bool _suppressDropdownCallbacks;
        bool _suppressFieldCallbacks;

        /// <summary>面板字段与模型字段的绑定，用于切换卡牌时批量回填。</summary>
        class FieldBinding
        {
            public TextField Field;
            public Func<string> Getter;
        }

        readonly List<FieldBinding> _bindings = new List<FieldBinding>();

        [MenuItem("Tools/Dungeon Card Design/行动卡树 (Canvas to GraphView)", priority = 0)]
        public static void Open()
        {
            CardTreeWindow window = GetWindow<CardTreeWindow>();
            window.titleContent = new GUIContent("行动卡树");
            window.minSize = new Vector2(900f, 560f);
            window.Show();
        }

        // 菜单入口：万一工具条被挤变形，这些操作在菜单里永远找得到。
        // 这里故意不写 %s 之类的全局快捷键——那会把 Unity 自己的 Ctrl+S（保存场景）顶掉。
        [MenuItem("Tools/Dungeon Card Design/新建卡树", priority = 20)]
        static void MenuNewTree()
        {
            GetWindow<CardTreeWindow>().NewTree();
        }

        [MenuItem("Tools/Dungeon Card Design/保存当前卡树", priority = 21)]
        static void MenuSaveTree()
        {
            GetWindow<CardTreeWindow>().SaveCurrent();
        }

        [MenuItem("Tools/Dungeon Card Design/另存为…", priority = 22)]
        static void MenuSaveTreeAs()
        {
            GetWindow<CardTreeWindow>().SaveAs();
        }

        void OnEnable()
        {
            BuildUI();
            RefreshTreeList(null);
            RefreshExportList(null);
            UpdateStatus();
        }

        // ==================================================================
        // 界面搭建
        // ==================================================================

        void BuildUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            // 快捷键：焦点在窗口内任何控件（包括文本输入框）时，按键都会冒泡到这里
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnWindowKeyDown);

            // 点窗口里任何地方都让导出提示消失。用 TrickleDown 是为了抢在按钮自己的
            // 回调之前执行——否则「导出 JSON」这一次点击会把刚设好的提示立刻清掉。
            rootVisualElement.RegisterCallback<ClickEvent>(
                evt => HideExportHint(), TrickleDown.TrickleDown);

            rootVisualElement.Add(BuildTreeToolbar());
            rootVisualElement.Add(BuildCanvasToolbar());
            rootVisualElement.Add(BuildExportHint());

            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1f;
            rootVisualElement.Add(body);

            _graphView = new CardTreeGraphView();
            _graphView.OnCardSelected = OnCardSelectedFromGraph;
            _graphView.OnUpgradeRequested = OnUpgradeRequestedFromGraph;
            _graphView.OnCreateCardRequested = CreateNewCard;
            _graphView.OnCardRemoved = OnCardRemovedFromGraph;
            _graphView.OnEdgeRemoved = OnEdgeRemovedFromGraph;
            _graphView.OnEdgeConnected = OnEdgeConnectedFromGraph;
            _graphView.OnElementsMoved = OnElementsMovedInGraph;
            _graphView.OnSelectionMaybeChanged = OnGraphSelectionMaybeChanged;
            body.Add(_graphView);

            body.Add(BuildEditorPanel());
        }

        /// <summary>窗口内快捷键：Ctrl/Cmd+S 保存，再加 Shift 走「另存为」。</summary>
        void OnWindowKeyDown(KeyDownEvent evt)
        {
            bool ctrl = (evt.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            if (!ctrl || evt.keyCode != KeyCode.S)
            {
                return;
            }

            // 吃掉这次按键，免得 Unity 顺手把场景也存一遍
            evt.StopPropagation();
            evt.PreventDefault();

            if ((evt.modifiers & EventModifiers.Shift) != 0)
            {
                SaveAs();
            }
            else
            {
                SaveCurrent();
            }
        }

        VisualElement BuildTreeToolbar()
        {
            VisualElement toolbar = NewToolbar();

            // 初始给一个占位选项：DropdownField 的构造函数在 choices 为空时会下标越界
            _treeDropdown = new DropdownField("卡树", new List<string> { "(还没有卡树)" }, 0);
            // 固定宽度且不伸缩：下拉框默认会把整行撑满，把后面的按钮挤到窗口外面看不见
            _treeDropdown.style.flexGrow = 0f;
            _treeDropdown.style.flexShrink = 0f;
            _treeDropdown.style.width = 240f;
            _treeDropdown.style.marginRight = 6f;
            _treeDropdown.RegisterValueChangedCallback(evt => OnTreeDropdownChanged());
            toolbar.Add(_treeDropdown);

            toolbar.Add(NewToolbarButton(NewTree, "新建卡树", 84f));

            Button saveButton = NewToolbarButton(SaveCurrent, "保存", 64f);
            saveButton.tooltip = "保存卡树（Ctrl+S）";
            toolbar.Add(saveButton);

            Button saveAsButton = NewToolbarButton(SaveAs, "另存为…", 84f);
            saveAsButton.tooltip = "换个名字另存（Ctrl+Shift+S）";
            toolbar.Add(saveAsButton);

            toolbar.Add(NewToolbarButton(RemoveSelectedCards, "删除选中卡牌", 96f));

            _statusLabel = new Label(string.Empty);
            _statusLabel.style.marginLeft = 10f;
            _statusLabel.style.flexGrow = 1f;
            _statusLabel.style.flexShrink = 0f;
            _statusLabel.style.color = new StyleColor(new Color(0.65f, 0.70f, 0.80f));
            toolbar.Add(_statusLabel);

            return toolbar;
        }

        VisualElement BuildCanvasToolbar()
        {
            VisualElement toolbar = NewToolbar();
            toolbar.style.borderTopWidth = 1f;
            toolbar.style.borderTopColor = new StyleColor(new Color(0.10f, 0.10f, 0.12f));

            _exportDropdown = new DropdownField("Canvas 导出", new List<string> { "(还没有导出)" }, 0);
            _exportDropdown.style.flexGrow = 0f;
            _exportDropdown.style.flexShrink = 0f;
            _exportDropdown.style.width = 240f;
            _exportDropdown.style.marginRight = 6f;
            toolbar.Add(_exportDropdown);

            toolbar.Add(NewToolbarButton(ImportSelectedExport, "导入到编辑器", 96f));

            _showGroupsToggle = new Toggle("显示分组节点") { value = false };
            _showGroupsToggle.style.flexShrink = 0f;
            _showGroupsToggle.RegisterValueChangedCallback(evt => ReloadView());
            toolbar.Add(_showGroupsToggle);

            _exportButton = NewToolbarButton(RunExport, "导出 JSON", 84f);
            _exportButton.tooltip =
                "执行 dungeon-card-design/tools/python/python.exe canvas_to_json.py，\n"
                + "把 canvases/ 下的 .canvas 导出为 exports/ 下的 JSON。";
            toolbar.Add(_exportButton);

            toolbar.Add(NewToolbarButton(RevealExportFolder, "打开导出目录", 96f));
            toolbar.Add(NewToolbarButton(RevealTreeFolder, "打开卡树目录", 96f));

            return toolbar;
        }

        /// <summary>
        /// 导出结果提示条。窗口是 UI Toolkit，用不了 IMGUI 的 EditorGUILayout.HelpBox，
        /// 这里用等价的 UIElements.HelpBox（同样有 Info / Warning / Error 三档）。
        /// </summary>
        VisualElement BuildExportHint()
        {
            _exportHint = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            _exportHint.style.display = DisplayStyle.None;
            _exportHint.style.marginLeft = 6f;
            _exportHint.style.marginRight = 6f;
            _exportHint.style.marginTop = 4f;
            _exportHint.style.marginBottom = 0f;
            return _exportHint;
        }

        void ShowExportHint(string fileName)
        {
            if (_exportHint == null)
            {
                return;
            }

            _exportHint.text = string.IsNullOrEmpty(fileName)
                ? "导出完成，点「导入到编辑器」查看 exports/ 下的结果"
                : "已导出到 exports/" + fileName + "，点「导入到编辑器」查看";
            _exportHint.style.display = DisplayStyle.Flex;
        }

        void HideExportHint()
        {
            if (_exportHint != null)
            {
                _exportHint.style.display = DisplayStyle.None;
            }
        }

        static VisualElement NewToolbar()
        {
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            // 窗口拉窄时让按钮换行，而不是被压缩到看不见
            toolbar.style.flexWrap = Wrap.Wrap;
            toolbar.style.paddingLeft = 6f;
            toolbar.style.paddingRight = 6f;
            toolbar.style.paddingTop = 4f;
            toolbar.style.paddingBottom = 4f;
            toolbar.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.18f));
            return toolbar;
        }

        /// <summary>
        /// 工具条按钮的统一尺寸策略：不参与伸缩，并给一个最小宽度。
        /// 必须显式约束——同一行里的 BaseField（下拉框）默认会吃掉整行宽度，
        /// 不约束的话后面的按钮会被挤到窗口外面，看起来就像"根本没有这些按钮"。
        /// </summary>
        static Button NewToolbarButton(Action onClick, string text, float minWidth)
        {
            var button = new Button(onClick) { text = text };
            button.style.flexGrow = 0f;
            button.style.flexShrink = 0f;
            button.style.minWidth = minWidth;
            button.style.marginLeft = 4f;
            return button;
        }

        VisualElement BuildEditorPanel()
        {
            var panel = new VisualElement();
            panel.style.width = EditorPanelWidth;
            panel.style.minWidth = 280f;
            panel.style.borderLeftWidth = 1f;
            panel.style.borderLeftColor = new StyleColor(new Color(0.10f, 0.10f, 0.12f));
            panel.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.17f));

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1f;
            panel.Add(scroll);

            var content = new VisualElement();
            content.style.paddingLeft = 8f;
            content.style.paddingRight = 8f;
            content.style.paddingTop = 8f;
            content.style.paddingBottom = 8f;
            scroll.Add(content);

            _editorEmptyHint = new Label(
                "在左侧点选一张卡牌来编辑，\n"
                + "或右键空白处「新建卡牌」。\n\n"
                + "双击卡牌可以打开「战斗内升级」填写窗口。");
            _editorEmptyHint.style.whiteSpace = WhiteSpace.Normal;
            _editorEmptyHint.style.color = new StyleColor(new Color(0.55f, 0.60f, 0.70f));
            content.Add(_editorEmptyHint);

            content.Add(BuildCardSection());
            content.Add(BuildEdgeSection());

            return panel;
        }

        VisualElement BuildCardSection()
        {
            _cardSection = new VisualElement();

            AddTextRow(_cardSection, "卡名", c => c.name, (c, v) => c.name = v, false, null);
            AddTextRow(_cardSection, "品级", c => c.rarity, (c, v) => c.rarity = v, false, "普通 / 稀有 / 史诗 / 传说");
            AddTextRow(_cardSection, "类别", c => c.category, (c, v) => c.category = v, false, "攻击 / 技能 / 能力 / 诅咒");
            AddTextRow(
                _cardSection,
                "效果模板",
                c => c.effect,
                (c, v) => c.effect = v,
                true,
                "可用 {变量名} 引用变量，例如 造成 {伤害} 点伤害");

            // 变量不再是纯文本，改成带等级的结构化列表
            _cardSection.Add(BuildVariableSection());
            _cardSection.Add(BuildKeywordSection());
            AddTextRow(_cardSection, "获取", c => c.acquisition, (c, v) => c.acquisition = v, false, null);
            AddTextRow(_cardSection, "设计意图", c => c.design_intent, (c, v) => c.design_intent = v, true, null);

            // 战斗内升级：面板里能直接写，也能开独立窗口写
            AddTextRow(
                _cardSection,
                "战斗内升级效果",
                c => c.combat_upgrade,
                (c, v) => c.combat_upgrade = v,
                true,
                "这张牌在战斗里升级后变成什么样");

            var openUpgrade = new Button(OpenUpgradeWindowForSelection) { text = "打开独立填写窗口" };
            openUpgrade.style.marginTop = 4f;
            openUpgrade.style.marginBottom = 8f;
            _cardSection.Add(openUpgrade);

            _missingLabel = new Label(string.Empty);
            _missingLabel.style.fontSize = 10f;
            _missingLabel.style.whiteSpace = WhiteSpace.Normal;
            _missingLabel.style.color = new StyleColor(new Color(0.95f, 0.62f, 0.45f));
            _cardSection.Add(_missingLabel);

            var rawCaption = new Label("原始文本（只读，来自 canvas 导出）");
            rawCaption.style.fontSize = 10f;
            rawCaption.style.marginTop = 8f;
            rawCaption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            _cardSection.Add(rawCaption);

            _rawField = new TextField();
            _rawField.multiline = true;
            _rawField.isReadOnly = true;
            _rawField.style.minHeight = 96f;
            _cardSection.Add(_rawField);

            return _cardSection;
        }

        VisualElement BuildEdgeSection()
        {
            _edgeSection = new VisualElement();
            _edgeSection.style.borderTopWidth = 1f;
            _edgeSection.style.borderTopColor = new StyleColor(new Color(0.26f, 0.28f, 0.34f));
            _edgeSection.style.paddingTop = 8f;
            _edgeSection.style.marginTop = 4f;

            var caption = new Label("连线");
            caption.style.unityFontStyleAndWeight = FontStyle.Bold;
            _edgeSection.Add(caption);

            _edgeInfoLabel = new Label(string.Empty);
            _edgeInfoLabel.style.fontSize = 10f;
            _edgeInfoLabel.style.whiteSpace = WhiteSpace.Normal;
            _edgeInfoLabel.style.color = new StyleColor(new Color(0.60f, 0.65f, 0.75f));
            _edgeSection.Add(_edgeInfoLabel);

            var labelCaption = new Label("连线标签");
            labelCaption.style.fontSize = 10f;
            labelCaption.style.marginTop = 4f;
            labelCaption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            _edgeSection.Add(labelCaption);

            _edgeLabelField = new TextField();
            _edgeLabelField.isDelayed = true;
            _edgeLabelField.RegisterValueChangedCallback(OnEdgeLabelChanged);
            _edgeSection.Add(_edgeLabelField);

            var removeEdge = new Button(RemoveSelectedEdge) { text = "删除这条连线" };
            removeEdge.style.marginTop = 6f;
            _edgeSection.Add(removeEdge);

            return _edgeSection;
        }

        TextField AddTextRow(
            VisualElement parent,
            string labelText,
            Func<CardData, string> getter,
            Action<CardData, string> setter,
            bool multiline,
            string hint)
        {
            var row = new VisualElement();
            row.style.marginBottom = 5f;

            var caption = new Label(labelText);
            caption.style.fontSize = 10f;
            caption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            row.Add(caption);

            if (!string.IsNullOrEmpty(hint))
            {
                var hintLabel = new Label(hint);
                hintLabel.style.fontSize = 9f;
                hintLabel.style.whiteSpace = WhiteSpace.Normal;
                hintLabel.style.color = new StyleColor(new Color(0.45f, 0.49f, 0.58f));
                row.Add(hintLabel);
            }

            var field = new TextField();
            field.multiline = multiline;
            field.style.marginLeft = 0f;
            field.style.marginRight = 0f;
            field.style.marginTop = 1f;
            field.style.marginBottom = 0f;
            if (multiline)
            {
                field.style.minHeight = 54f;
            }
            field.RegisterValueChangedCallback(evt =>
            {
                if (_suppressFieldCallbacks || _selectedCard == null)
                {
                    return;
                }
                setter(_selectedCard, evt.newValue);
                RefreshSelectedNode();
            });
            row.Add(field);

            parent.Add(row);

            _bindings.Add(new FieldBinding
            {
                Field = field,
                Getter = () => _selectedCard == null ? string.Empty : (getter(_selectedCard) ?? string.Empty),
            });

            return field;
        }

        // ==================================================================
        // 变量列表 / 预览 / 校验
        // ==================================================================

        VisualElement BuildVariableSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 6f;

            var caption = new Label("变量");
            caption.style.fontSize = 10f;
            caption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            section.Add(caption);

            var hint = new Label("带 _LvN 后缀只在该等级生效（如 伤害_Lv1）；不带后缀的各个等级共用");
            hint.style.fontSize = 9f;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.color = new StyleColor(new Color(0.45f, 0.49f, 0.58f));
            section.Add(hint);

            // 表头
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.marginTop = 3f;
            header.Add(NewVariableHeader("名称", 92f));
            header.Add(NewVariableHeader("值", 56f));
            header.Add(NewVariableHeader("类型", 46f));
            section.Add(header);

            _variableRowsHost = new VisualElement();
            section.Add(_variableRowsHost);

            var addButton = new Button(AddVariableRow) { text = "+ 添加变量" };
            addButton.style.marginTop = 4f;
            section.Add(addButton);

            // 预览等级
            var levelRow = new VisualElement();
            levelRow.style.flexDirection = FlexDirection.Row;
            levelRow.style.alignItems = Align.Center;
            levelRow.style.marginTop = 8f;

            var levelCaption = new Label("预览等级");
            levelCaption.style.fontSize = 10f;
            levelCaption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            levelRow.Add(levelCaption);

            _levelButtons = new Button[VariableCodec.MaxLevel];
            for (int level = 1; level <= VariableCodec.MaxLevel; level++)
            {
                int captured = level;
                var button = new Button(() => SetPreviewLevel(captured)) { text = "Lv." + level };
                button.style.flexShrink = 0f;
                button.style.minWidth = 44f;
                button.style.marginLeft = 4f;
                levelRow.Add(button);
                _levelButtons[level - 1] = button;
            }
            section.Add(levelRow);

            var previewCaption = new Label("预览");
            previewCaption.style.fontSize = 10f;
            previewCaption.style.marginTop = 6f;
            previewCaption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            section.Add(previewCaption);

            _previewLabel = new Label(string.Empty);
            // 按契约 §6 给变量/运行时量/错误分色，需要富文本
            _previewLabel.enableRichText = true;
            _previewLabel.style.whiteSpace = WhiteSpace.Normal;
            _previewLabel.style.paddingLeft = 4f;
            _previewLabel.style.paddingRight = 4f;
            _previewLabel.style.paddingTop = 3f;
            _previewLabel.style.paddingBottom = 3f;
            _previewLabel.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f));
            _previewLabel.style.color = new StyleColor(new Color(0.92f, 0.92f, 0.96f));
            section.Add(_previewLabel);

            var checkCaption = new Label("校验");
            checkCaption.style.fontSize = 10f;
            checkCaption.style.marginTop = 6f;
            checkCaption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            section.Add(checkCaption);

            _validationLabel = new Label(string.Empty);
            _validationLabel.style.whiteSpace = WhiteSpace.Normal;
            _validationLabel.style.fontSize = 10f;
            section.Add(_validationLabel);

            RefreshLevelButtons();
            return section;
        }

        // ==================================================================
        // 词条列表
        // ==================================================================

        VisualElement BuildKeywordSection()
        {
            var section = new VisualElement();
            section.style.marginBottom = 8f;

            var caption = new Label("词条");
            caption.style.fontSize = 10f;
            caption.style.color = new StyleColor(new Color(0.56f, 0.61f, 0.72f));
            section.Add(caption);

            var hint = new Label("只写词条名，效果由运行时战斗代码实现；裂变X 的 X 要写正整数");
            hint.style.fontSize = 9f;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.color = new StyleColor(new Color(0.45f, 0.49f, 0.58f));
            section.Add(hint);

            _keywordRowsHost = new VisualElement();
            _keywordRowsHost.style.marginTop = 3f;
            section.Add(_keywordRowsHost);

            var addButton = new Button(AddKeywordRow) { text = "+ 添加词条" };
            addButton.style.marginTop = 4f;
            section.Add(addButton);

            return section;
        }

        /// <summary>
        /// 按当前选中卡的词条重建输入行。切换卡牌、增删词条时调用；
        /// 打字时不调用，否则输入焦点会掉。
        /// </summary>
        void RefreshKeywordRows()
        {
            if (_keywordRowsHost == null)
            {
                return;
            }

            _keywordRowsHost.Clear();
            if (_selectedCard == null)
            {
                return;
            }
            if (_selectedCard.keywords == null)
            {
                _selectedCard.keywords = new List<string>();
            }

            // 行不会在打字时重建，所以下标可以安全地捕获
            List<string> keywords = _selectedCard.keywords;
            for (int index = 0; index < keywords.Count; index++)
            {
                int captured = index;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 2f;

                // 复用变量行的窄输入框样式，两者外观保持一致
                TextField field = NewVariableField(keywords[index], 168f);
                field.tooltip = "例如 顽固 / 湮灭 / 裂变2";
                field.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressFieldCallbacks) return;
                    keywords[captured] = evt.newValue;
                    OnKeywordEdited();
                });
                row.Add(field);

                var remove = new Button(() => RemoveKeywordRow(captured)) { text = "×" };
                remove.style.flexShrink = 0f;
                remove.style.minWidth = 22f;
                remove.style.marginLeft = 2f;
                row.Add(remove);

                _keywordRowsHost.Add(row);
            }
        }

        void AddKeywordRow()
        {
            if (_selectedCard == null)
            {
                return;
            }
            if (_selectedCard.keywords == null)
            {
                _selectedCard.keywords = new List<string>();
            }

            _selectedCard.keywords.Add("新词条");
            RefreshKeywordRows();
            OnKeywordEdited();
        }

        void RemoveKeywordRow(int index)
        {
            if (_selectedCard == null || _selectedCard.keywords == null)
            {
                return;
            }

            List<string> keywords = _selectedCard.keywords;
            if (index < 0 || index >= keywords.Count)
            {
                return;
            }

            keywords.RemoveAt(index);
            RefreshKeywordRows();
            OnKeywordEdited();
        }

        /// <summary>词条改动后：只刷校验和节点，不重建行，否则输入焦点会掉。</summary>
        void OnKeywordEdited()
        {
            RefreshPreview();
            MarkDirty();

            if (_selectedCard != null && _graphView != null)
            {
                _graphView.RefreshCard(_selectedCard.id);
            }
        }

        static Label NewVariableHeader(string text, float width)
        {
            var label = new Label(text);
            label.style.width = width;
            label.style.minWidth = width;
            label.style.marginRight = 2f;
            label.style.fontSize = 9f;
            label.style.color = new StyleColor(new Color(0.50f, 0.55f, 0.66f));
            return label;
        }

        static TextField NewVariableField(string value, float width)
        {
            var field = new TextField();
            field.value = value ?? string.Empty;
            field.style.width = width;
            field.style.minWidth = width;
            field.style.flexGrow = 0f;
            field.style.flexShrink = 0f;
            field.style.marginLeft = 0f;
            field.style.marginRight = 2f;
            return field;
        }

        /// <summary>按当前选中卡的变量重建输入行。切换卡牌时调用；打字时不调用，否则焦点会掉。</summary>
        void RefreshVariableRows()
        {
            if (_variableRowsHost == null)
            {
                return;
            }

            _variableRowsHost.Clear();
            if (_selectedCard == null)
            {
                return;
            }
            if (_selectedCard.variables == null)
            {
                _selectedCard.variables = new VariablesData();
            }

            List<VariableItem> items = _selectedCard.variables.items;
            for (int index = 0; index < items.Count; index++)
            {
                VariableItem item = items[index];
                if (item == null)
                {
                    continue;
                }

                int captured = index;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 2f;

                TextField keyField = NewVariableField(item.key, 92f);
                keyField.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressFieldCallbacks) return;
                    item.key = evt.newValue;
                    OnVariableEdited();
                });
                row.Add(keyField);

                TextField valueField = NewVariableField(item.value, 56f);
                valueField.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressFieldCallbacks) return;
                    item.value = evt.newValue;
                    OnVariableEdited();
                });
                row.Add(valueField);

                TextField typeField = NewVariableField(item.type, 46f);
                typeField.tooltip = "int / float / text，留空表示不校验";
                typeField.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressFieldCallbacks) return;
                    item.type = evt.newValue;
                    OnVariableEdited();
                });
                row.Add(typeField);

                var remove = new Button(() => RemoveVariableRow(captured)) { text = "×" };
                remove.style.flexShrink = 0f;
                remove.style.minWidth = 22f;
                remove.style.marginLeft = 2f;
                row.Add(remove);

                _variableRowsHost.Add(row);
            }
        }

        void AddVariableRow()
        {
            if (_selectedCard == null)
            {
                return;
            }
            if (_selectedCard.variables == null)
            {
                _selectedCard.variables = new VariablesData();
            }

            _selectedCard.variables.items.Add(new VariableItem
            {
                key = "新变量",
                value = "0",
                type = "int",
            });

            RefreshVariableRows();
            RefreshPreview();
            MarkDirty();
        }

        void RemoveVariableRow(int index)
        {
            if (_selectedCard == null || _selectedCard.variables == null)
            {
                return;
            }

            List<VariableItem> items = _selectedCard.variables.items;
            if (index < 0 || index >= items.Count)
            {
                return;
            }

            items.RemoveAt(index);
            RefreshVariableRows();
            RefreshPreview();
            MarkDirty();
        }

        /// <summary>变量行改动后：只刷预览/校验和节点，不重建行，否则输入焦点会掉。</summary>
        void OnVariableEdited()
        {
            RefreshPreview();
            RefreshMissingFields();
            MarkDirty();

            if (_selectedCard != null && _graphView != null)
            {
                _graphView.RefreshCard(_selectedCard.id);
            }
        }

        void SetPreviewLevel(int level)
        {
            _previewLevel = level;
            RefreshLevelButtons();
            RefreshPreview();
        }

        void RefreshLevelButtons()
        {
            if (_levelButtons == null)
            {
                return;
            }

            for (int i = 0; i < _levelButtons.Length; i++)
            {
                if (_levelButtons[i] == null)
                {
                    continue;
                }
                bool active = (i + 1) == _previewLevel;
                _levelButtons[i].style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
                _levelButtons[i].style.color = new StyleColor(active
                    ? new Color(0.95f, 0.80f, 0.35f)
                    : new Color(0.80f, 0.83f, 0.88f));
            }
        }

        /// <summary>按当前预览等级渲染效果模板，并跑一遍校验。</summary>
        void RefreshPreview()
        {
            if (_previewLabel == null || _validationLabel == null)
            {
                return;
            }

            if (_selectedCard == null)
            {
                _previewLabel.text = string.Empty;
                _validationLabel.text = string.Empty;
                return;
            }

            List<VariableItem> items = _selectedCard.variables != null
                ? _selectedCard.variables.items
                : new List<VariableItem>();

            // 有 bindings 就按契约 §6 分色渲染；没有（纯文字卡、或 JSON 是本次改动
            // 之前导出的）就退回原来的纯文本渲染，功能不倒退。
            IList<EffectSegment> segments = _selectedCard.bindings != null
                ? _selectedCard.bindings.effect_segments
                : null;

            string rendered = segments != null && segments.Count > 0
                ? CardSegmentRenderer.ToRichText(segments, _previewLevel, out _)
                : VariableCodec.RenderTemplate(_selectedCard.effect, items, _previewLevel);

            _previewLabel.text = string.IsNullOrEmpty(rendered)
                ? "(效果为空)"
                : "Lv." + _previewLevel + "   " + rendered;

            List<string> messages = VariableCodec.Validate(_selectedCard);
            if (messages.Count == 0)
            {
                _validationLabel.text = "没有发现问题";
                _validationLabel.style.color = new StyleColor(new Color(0.55f, 0.78f, 0.55f));
                return;
            }

            bool hasError = false;
            foreach (string message in messages)
            {
                if (message.StartsWith("错误", StringComparison.Ordinal))
                {
                    hasError = true;
                    break;
                }
            }

            _validationLabel.text = string.Join("\n", messages.ToArray());
            _validationLabel.style.color = new StyleColor(hasError
                ? new Color(0.95f, 0.55f, 0.45f)
                : new Color(0.90f, 0.75f, 0.40f));
        }

        // ==================================================================
        // 选中 / 面板回填
        // ==================================================================

        void OnCardSelectedFromGraph(CardData card)
        {
            _selectedCard = card;
            _selectedEdge = null;
            PopulateFields();
        }

        /// <summary>
        /// GraphView 的选中状态变化后同步面板。
        /// 点空白处时故意保留当前编辑对象，免得面板被清空。
        /// </summary>
        void OnGraphSelectionMaybeChanged()
        {
            if (_graphView == null)
            {
                return;
            }

            CardData card = _graphView.SelectedCardData;
            if (card != null)
            {
                _selectedCard = card;
                _selectedEdge = null;
                PopulateFields();
                return;
            }

            UnityEditor.Experimental.GraphView.Edge edge = _graphView.SelectedEdge;
            if (edge != null)
            {
                _selectedCard = null;
                _selectedEdge = edge;
                PopulateFields();
            }
        }

        void PopulateFields()
        {
            bool hasCard = _selectedCard != null;
            bool hasEdge = _selectedEdge != null;

            _cardSection.style.display = hasCard ? DisplayStyle.Flex : DisplayStyle.None;
            _edgeSection.style.display = hasEdge ? DisplayStyle.Flex : DisplayStyle.None;
            _editorEmptyHint.style.display = (hasCard || hasEdge) ? DisplayStyle.None : DisplayStyle.Flex;

            _suppressFieldCallbacks = true;
            try
            {
                foreach (FieldBinding binding in _bindings)
                {
                    binding.Field.SetValueWithoutNotify(hasCard ? binding.Getter() : string.Empty);
                }

                _rawField.SetValueWithoutNotify(
                    hasCard && _selectedCard.raw_text != null ? _selectedCard.raw_text : string.Empty);

                if (hasEdge)
                {
                    EdgeData data = FindEdgeData(_selectedEdge);
                    _edgeInfoLabel.text = data != null
                        ? data.from + "   →   " + data.to
                        : "(这条连线还没写进数据)";
                    _edgeLabelField.SetValueWithoutNotify(data != null && data.label != null ? data.label : string.Empty);
                }
                else
                {
                    _edgeInfoLabel.text = string.Empty;
                    _edgeLabelField.SetValueWithoutNotify(string.Empty);
                }
            }
            finally
            {
                _suppressFieldCallbacks = false;
            }

            if (hasCard)
            {
                RefreshMissingFields();
            }

            // 变量行、词条行和预览都跟着当前选中的卡走
            RefreshVariableRows();
            RefreshKeywordRows();
            RefreshPreview();
        }

        /// <summary>重算「哪些字段还没填」，顺便写回模型，存盘时数据才是准的。</summary>
        void RefreshMissingFields()
        {
            if (_selectedCard == null)
            {
                return;
            }

            CardData c = _selectedCard;
            var missing = new List<string>();
            if (string.IsNullOrEmpty(c.name)) missing.Add("卡名");
            if (string.IsNullOrEmpty(c.rarity)) missing.Add("品级");
            if (string.IsNullOrEmpty(c.category)) missing.Add("类别");
            if (string.IsNullOrEmpty(c.effect)) missing.Add("效果");
            if (string.IsNullOrEmpty(c.acquisition)) missing.Add("获取");
            if (string.IsNullOrEmpty(c.design_intent)) missing.Add("设计意图");

            c.missing_fields = missing.ToArray();
            _missingLabel.text = missing.Count > 0
                ? "未填写：" + string.Join("、", missing)
                : "六个字段都已填写";
        }

        void RefreshSelectedNode()
        {
            if (_selectedCard == null)
            {
                return;
            }
            if (_graphView != null)
            {
                _graphView.RefreshCard(_selectedCard.id);
            }
            RefreshMissingFields();
            RefreshPreview();
            MarkDirty();
        }

        // ==================================================================
        // 图的增删改
        // ==================================================================

        void CreateNewCard()
        {
            if (_model == null)
            {
                EditorUtility.DisplayDialog("新建卡牌", "请先「新建卡树」或「导入到编辑器」。", "好");
                return;
            }

            var card = new CardData
            {
                id = "card-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                type = "text",
                name = "新卡牌",
                combat_upgrade = string.Empty,
                position = new PositionData(),
                size = new SizeData { width = 280f, height = 190f },
                variables = new VariablesData(),
                outgoing = new string[0],
                incoming = new string[0],
                children = new string[0],
                missing_fields = new string[0],
            };

            if (_model.cards == null)
            {
                _model.cards = new CardData[0];
            }
            Array.Resize(ref _model.cards, _model.cards.Length + 1);
            _model.cards[_model.cards.Length - 1] = card;

            if (_graphView != null)
            {
                _graphView.AddCardNode(card);
                _graphView.SelectCard(card.id);
            }

            _selectedCard = card;
            _selectedEdge = null;
            PopulateFields();
            MarkDirty();
            Debug.Log("[行动卡树] 已新建卡牌: " + card.id);
        }

        void RemoveSelectedCards()
        {
            if (_graphView != null)
            {
                _graphView.DeleteSelection();
            }
        }

        void OnCardRemovedFromGraph(CardData card)
        {
            if (_model == null || card == null)
            {
                return;
            }

            if (_model.cards != null)
            {
                var cards = new List<CardData>(_model.cards);
                cards.RemoveAll(c => c != null && c.id == card.id);
                _model.cards = cards.ToArray();
            }

            RemoveEdgesTouching(card.id);

            if (_selectedCard == card)
            {
                _selectedCard = null;
                PopulateFields();
            }

            MarkDirty();
            Debug.Log("[行动卡树] 已删除卡牌: " + card.id);
        }

        void OnEdgeRemovedFromGraph(UnityEditor.Experimental.GraphView.Edge edge)
        {
            if (_model == null || edge == null || string.IsNullOrEmpty(edge.viewDataKey) || _model.edges == null)
            {
                return;
            }

            string edgeId = edge.viewDataKey;
            var edges = new List<EdgeData>(_model.edges);
            if (edges.RemoveAll(e => e != null && e.id == edgeId) == 0)
            {
                return;
            }
            _model.edges = edges.ToArray();

            if (_selectedEdge == edge)
            {
                _selectedEdge = null;
                PopulateFields();
            }

            MarkDirty();
        }

        void OnEdgeConnectedFromGraph(
            CardData from,
            CardData to,
            UnityEditor.Experimental.GraphView.Edge edge)
        {
            if (_model == null || from == null || to == null)
            {
                return;
            }

            string edgeId = "edge-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (edge != null)
            {
                // 记住 id，将来删除这条连线时才能对应回模型
                edge.viewDataKey = edgeId;
            }

            if (_model.edges == null)
            {
                _model.edges = new EdgeData[0];
            }
            Array.Resize(ref _model.edges, _model.edges.Length + 1);
            _model.edges[_model.edges.Length - 1] = new EdgeData
            {
                id = edgeId,
                from = from.id,
                to = to.id,
                label = null,
            };

            MarkDirty();
            Debug.Log("[行动卡树] 新建连线: " + from.id + " -> " + to.id + "（可在右侧填连线标签）");
        }

        void OnElementsMovedInGraph()
        {
            if (_graphView != null)
            {
                _graphView.SyncPositionsToModel();
            }
            MarkDirty();
        }

        void RemoveEdgesTouching(string cardId)
        {
            if (_model == null || _model.edges == null)
            {
                return;
            }
            var edges = new List<EdgeData>(_model.edges);
            if (edges.RemoveAll(e => e != null && (e.from == cardId || e.to == cardId)) == 0)
            {
                return;
            }
            _model.edges = edges.ToArray();
        }

        EdgeData FindEdgeData(UnityEditor.Experimental.GraphView.Edge edge)
        {
            if (_model == null || _model.edges == null || edge == null || string.IsNullOrEmpty(edge.viewDataKey))
            {
                return null;
            }
            string id = edge.viewDataKey;
            foreach (EdgeData data in _model.edges)
            {
                if (data != null && data.id == id)
                {
                    return data;
                }
            }
            return null;
        }

        void OnEdgeLabelChanged(ChangeEvent<string> evt)
        {
            if (_suppressFieldCallbacks || _selectedEdge == null)
            {
                return;
            }

            EdgeData data = FindEdgeData(_selectedEdge);
            if (data == null)
            {
                return;
            }

            data.label = evt.newValue;
            CardTreeGraphView.SetEdgeLabel(_selectedEdge, evt.newValue);
            MarkDirty();
        }

        void RemoveSelectedEdge()
        {
            if (_graphView == null || _selectedEdge == null)
            {
                return;
            }

            // 走 GraphView 的删除流程，graphViewChanged 才会回调，模型才能同步
            _graphView.ClearSelection();
            _graphView.AddToSelection(_selectedEdge);
            _graphView.DeleteSelection();
        }

        // ==================================================================
        // 卡树的打开 / 保存 / 切换
        // ==================================================================

        /// <summary>下拉框在没有任何文件时也要有个合法选项，否则 DropdownField 会下标越界。</summary>
        const string NoTreePlaceholder = "(还没有卡树)";
        const string NoExportPlaceholder = "(还没有导出)";

        void NewTree()
        {
            // 先问名字再建树：命名即落盘到 Assets/Resources/CardTrees/
            CardTreeSaveAsWindow.Open("新卡树", CreateTreeWithName);
        }

        void CreateTreeWithName(string treeName)
        {
            _model = CardTreeStore.CreateEmptyTree(treeName);
            _currentTreeName = treeName;
            _selectedCard = null;
            _selectedEdge = null;

            ReloadView();
            PopulateFields();
            SaveModelAs(treeName);

            Debug.Log("[行动卡树] 已新建卡树: " + treeName);
        }

        void SaveCurrent()
        {
            if (_model == null)
            {
                EditorUtility.DisplayDialog(
                    "保存卡树",
                    "当前没有打开的卡树。\n先「新建卡树」，或从上面导入一个 canvas 导出。",
                    "好");
                return;
            }

            if (string.IsNullOrEmpty(_currentTreeName))
            {
                SaveAs();
                return;
            }

            SaveModelAs(_currentTreeName);
        }

        void SaveAs()
        {
            if (_model == null)
            {
                EditorUtility.DisplayDialog("另存为", "当前没有打开的卡树。", "好");
                return;
            }

            CardTreeSaveAsWindow.Open(
                string.IsNullOrEmpty(_currentTreeName) ? "新卡树" : _currentTreeName,
                SaveModelAs);
        }

        void SaveModelAs(string treeName)
        {
            if (_model == null)
            {
                return;
            }

            // 存盘前先把图上节点的实际坐标写回模型，否则存进去的还是旧布局
            if (_graphView != null)
            {
                _graphView.SyncPositionsToModel();
            }

            try
            {
                _currentTreePath = CardTreeStore.Save(_model, treeName);
                _currentTreeName = treeName;
                _dirty = false;
                RefreshTreeList(treeName);
                UpdateStatus();
                Debug.Log("[行动卡树] 已保存: " + _currentTreePath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[行动卡树] 保存失败: " + treeName + "\n" + ex);
                EditorUtility.DisplayDialog(
                    "保存卡树",
                    "保存失败：\n" + ex.Message + "\n\n目标目录：\n" + CardTreeStore.TreeDirectoryFullPath,
                    "好");
            }
        }

        void OnTreeDropdownChanged()
        {
            if (_suppressDropdownCallbacks || _treeDropdown == null)
            {
                return;
            }

            string fullPath = FindTreePathByName(_treeDropdown.value);
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            if (_dirty && !string.IsNullOrEmpty(_currentTreeName))
            {
                bool save = EditorUtility.DisplayDialog(
                    "切换卡树",
                    "当前卡树有未保存的改动：\n" + _currentTreeName + "\n\n要先保存吗？",
                    "保存并切换",
                    "放弃改动");
                if (save)
                {
                    SaveCurrent();
                }
            }

            LoadTree(fullPath);
        }

        /// <summary>下拉框显示的是文件名，这里换算回完整路径。</summary>
        string FindTreePathByName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName) || _treeFiles == null)
            {
                return null;
            }

            foreach (string file in _treeFiles)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), displayName, StringComparison.Ordinal))
                {
                    return file;
                }
            }
            return null;
        }

        void LoadTree(string fullPath)
        {
            CanvasExport data = CardTreeLoader.Load(fullPath);
            if (data == null)
            {
                EditorUtility.DisplayDialog("打开卡树", "读取失败：\n" + fullPath, "好");
                return;
            }

            _model = data;
            _currentTreePath = fullPath;
            _currentTreeName = Path.GetFileNameWithoutExtension(fullPath);
            _selectedCard = null;
            _selectedEdge = null;
            _dirty = false;

            ReloadView();
            PopulateFields();
            UpdateStatus();
            Debug.Log("[行动卡树] 已打开: " + fullPath);
        }

        /// <summary>重新扫描 Assets/Resources/CardTrees/，并尽量把下拉框停在 preferred 上。</summary>
        void RefreshTreeList(string preferred)
        {
            _treeFiles = CardTreeStore.ListTreeFiles();

            var choices = new List<string>();
            foreach (string file in _treeFiles)
            {
                choices.Add(Path.GetFileNameWithoutExtension(file));
            }

            if (_treeDropdown == null)
            {
                return;
            }

            if (choices.Count == 0)
            {
                choices.Add(NoTreePlaceholder);
            }

            string target = string.IsNullOrEmpty(preferred) ? _currentTreeName : preferred;

            _suppressDropdownCallbacks = true;
            try
            {
                // 换 choices 会让 DropdownField 重置 value，这里必须屏蔽回调
                _treeDropdown.choices = choices;
                _treeDropdown.SetValueWithoutNotify(
                    !string.IsNullOrEmpty(target) && choices.Contains(target) ? target : choices[0]);
            }
            finally
            {
                _suppressDropdownCallbacks = false;
            }
        }

        /// <summary>用当前模型整张重建图。</summary>
        void ReloadView()
        {
            if (_graphView == null)
            {
                return;
            }

            bool showGroups = _showGroupsToggle != null && _showGroupsToggle.value;
            _selectedEdge = null;
            _graphView.Load(_model, showGroups);

            // 重建后旧节点对象已经没了，但 CardData 还在模型里，可以按 id 重新选中
            if (_selectedCard != null && _model != null && _model.cards != null)
            {
                bool stillExists = false;
                foreach (CardData card in _model.cards)
                {
                    if (card == _selectedCard)
                    {
                        stillExists = true;
                        break;
                    }
                }

                if (stillExists)
                {
                    _graphView.SelectCard(_selectedCard.id);
                }
                else
                {
                    _selectedCard = null;
                }
            }
        }

        void MarkDirty()
        {
            _dirty = true;
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (_statusLabel == null)
            {
                return;
            }

            string name = string.IsNullOrEmpty(_currentTreeName) ? "(未命名)" : _currentTreeName;
            int cardCount = _model != null && _model.cards != null ? _model.cards.Length : 0;
            int edgeCount = _model != null && _model.edges != null ? _model.edges.Length : 0;

            _statusLabel.text = string.Format(
                "{0}{1}    {2} 卡 / {3} 连线",
                name,
                _dirty ? " *" : string.Empty,
                cardCount,
                edgeCount);
        }

        void RevealTreeFolder()
        {
            string dir = CardTreeStore.TreeDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            EditorUtility.RevealInFinder(dir);
        }

        // ==================================================================
        // 战斗内升级填写窗口
        // ==================================================================

        void OpenUpgradeWindowForSelection()
        {
            if (_selectedCard == null)
            {
                EditorUtility.DisplayDialog("战斗内升级", "请先在左侧点选一张卡牌，或直接双击节点。", "好");
                return;
            }

            CardUpgradeWindow.Open(_selectedCard, OnUpgradeEdited);
        }

        void OnUpgradeRequestedFromGraph(CardData card)
        {
            CardUpgradeWindow.Open(card, OnUpgradeEdited);
        }

        /// <summary>升级窗口里每敲一个字都会回调到这里，实时刷新节点上的「升级」一行。</summary>
        void OnUpgradeEdited(CardData card)
        {
            if (card == null)
            {
                return;
            }

            if (_graphView != null)
            {
                _graphView.RefreshCard(card.id);
            }

            MarkDirty();
        }

        // ==================================================================
        // Python 导出
        // ==================================================================

        // 所有路径都从 Application.dataPath 推导，不写死盘符，换机器不用改
        static string ProjectRootPath
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        static string PortablePythonFullPath
        {
            get
            {
                return Path.Combine(ProjectRootPath, DesignRootRelativePath, "tools", "python", "python.exe");
            }
        }

        static string ExporterScriptFullPath
        {
            get
            {
                return Path.Combine(ProjectRootPath, DesignRootRelativePath, "scripts", "canvas_to_json.py");
            }
        }

        static string CanvasDirectoryFullPath
        {
            get
            {
                return Path.Combine(ProjectRootPath, DesignRootRelativePath, "canvases");
            }
        }

        static string ExportDirectoryFullPath
        {
            get
            {
                return Path.Combine(ProjectRootPath, DesignRootRelativePath, "exports");
            }
        }

        /// <summary>重新扫描 exports/，尽量把下拉框停在 preferred 上。</summary>
        void RefreshExportList(string preferred)
        {
            _exportFiles = CardTreeLoader.ListExportFiles();

            var choices = new List<string>();
            foreach (string file in _exportFiles)
            {
                choices.Add(Path.GetFileName(file));
            }

            if (_exportDropdown == null)
            {
                return;
            }

            if (choices.Count == 0)
            {
                choices.Add(NoExportPlaceholder);
            }

            _suppressDropdownCallbacks = true;
            try
            {
                _exportDropdown.choices = choices;
                _exportDropdown.SetValueWithoutNotify(
                    !string.IsNullOrEmpty(preferred) && choices.Contains(preferred) ? preferred : choices[0]);
            }
            finally
            {
                _suppressDropdownCallbacks = false;
            }
        }

        string CurrentExportFilePath()
        {
            if (_exportDropdown == null || string.IsNullOrEmpty(_exportDropdown.value))
            {
                return null;
            }

            foreach (string file in _exportFiles)
            {
                if (string.Equals(Path.GetFileName(file), _exportDropdown.value, StringComparison.Ordinal))
                {
                    return file;
                }
            }
            return null;
        }

        /// <summary>把选中的 canvas 导出读进编辑器。注意：这是导入画布快照，不覆盖 Resources 里的卡树。</summary>
        void ImportSelectedExport()
        {
            string path = CurrentExportFilePath();
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog(
                    "导入 canvas 导出",
                    "exports/ 下还没有 JSON。\n先点「导出 JSON」，或检查目录：\n" + ExportDirectoryFullPath,
                    "好");
                return;
            }

            if (_dirty && !string.IsNullOrEmpty(_currentTreeName))
            {
                bool save = EditorUtility.DisplayDialog(
                    "导入 canvas 导出",
                    "当前卡树有未保存的改动：\n" + _currentTreeName + "\n\n要先保存吗？",
                    "先保存再导入",
                    "直接导入");
                if (save)
                {
                    SaveCurrent();
                }
            }

            CanvasExport data = CardTreeLoader.Load(path);
            if (data == null)
            {
                EditorUtility.DisplayDialog("导入 canvas 导出", "读取失败：\n" + path, "好");
                return;
            }

            _model = data;
            _currentTreePath = null;
            _currentTreeName = Path.GetFileNameWithoutExtension(path);
            _selectedCard = null;
            _selectedEdge = null;
            _dirty = false;

            ReloadView();
            PopulateFields();
            UpdateStatus();
            Debug.Log("[行动卡树] 已导入 canvas 导出: " + path + "（要存进 Resources 请点「另存为…」）");
        }

        /// <summary>调用便携版 Python 跑 canvas_to_json.py，把 canvases/ 导出成 exports/ 下的 JSON。</summary>
        void RunExport()
        {
            string python = PortablePythonFullPath;
            string script = ExporterScriptFullPath;

            if (!File.Exists(python))
            {
                EditorUtility.DisplayDialog(
                    "导出 JSON",
                    "找不到便携版 Python：\n" + python
                        + "\n\n请确认便携版 Python 已解压到 dungeon-card-design/tools/python/",
                    "好");
                return;
            }

            if (!File.Exists(script))
            {
                EditorUtility.DisplayDialog("导出 JSON", "找不到导出脚本：\n" + script, "好");
                return;
            }

            if (!Directory.Exists(CanvasDirectoryFullPath))
            {
                EditorUtility.DisplayDialog("导出 JSON", "找不到 canvas 目录：\n" + CanvasDirectoryFullPath, "好");
                return;
            }

            Directory.CreateDirectory(ExportDirectoryFullPath);

            // 记下导出前每个 JSON 的写入时间，导出后据此判断这次实际写了哪些文件
            Dictionary<string, DateTime> exportSnapshot = SnapshotExportTimes();

            if (_exportButton != null)
            {
                _exportButton.SetEnabled(false);
                _exportButton.text = "导出中…";
            }

            int exitCode = -1;
            string stdout = string.Empty;
            string stderr = string.Empty;
            bool timedOut = false;

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = python,
                    Arguments = string.Format(
                        "\"{0}\" -c \"{1}\" -o \"{2}\" --pretty --verbose",
                        script,
                        CanvasDirectoryFullPath,
                        ExportDirectoryFullPath),
                    // 工作目录设成项目根目录，脚本里的相对路径才落得准
                    WorkingDirectory = ProjectRootPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = startInfo;

                    // 异步读输出，避免子进程写满管道后双方互等（死锁）
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ExportTimeoutMilliseconds))
                    {
                        timedOut = true;
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception killError)
                        {
                            Debug.LogWarning("[行动卡树] 终止导出进程失败: " + killError.Message);
                        }
                    }
                    else
                    {
                        // 带超时的 WaitForExit 不保证异步输出读完，再调一次无参版本收尾
                        process.WaitForExit();
                        stdout = outputBuilder.ToString();
                        stderr = errorBuilder.ToString();
                        exitCode = process.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                stderr = ex.ToString();
                exitCode = -1;
                Debug.LogError("[行动卡树] 启动导出进程失败: " + ex);
            }
            finally
            {
                if (_exportButton != null)
                {
                    _exportButton.SetEnabled(true);
                    _exportButton.text = "导出 JSON";
                }
            }

            if (timedOut)
            {
                EditorUtility.DisplayDialog(
                    "导出 JSON",
                    "导出超时（超过 " + (ExportTimeoutMilliseconds / 1000) + " 秒），已终止进程。",
                    "好");
                return;
            }

            if (exitCode == 0)
            {
                // 只刷新下拉列表，不自动把导出结果灌进编辑器，免得覆盖还没保存的改动。
                // 下拉框选中这次真正写过的文件，提示里的「点『导入到编辑器』查看」才说得通。
                string freshExport = FindFreshExportFile(exportSnapshot);
                RefreshExportList(freshExport);
                ShowExportHint(freshExport);

                Debug.Log("[行动卡树] 导出成功（exit=0）\n" + stdout);
                EditorUtility.DisplayDialog("导出 JSON", "导出成功。\n\n" + Tail(stdout, 600), "好");
            }
            else
            {
                string detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                Debug.LogError("[行动卡树] 导出失败（exit=" + exitCode + "）\n" + stderr + "\n" + stdout);
                EditorUtility.DisplayDialog(
                    "导出 JSON",
                    "导出失败，退出码 " + exitCode + "。\n\n" + Tail(detail, 900),
                    "好");
            }
        }

        /// <summary>导出前记下 exports/ 下每个 JSON 的写入时间，用来判断这次跑了哪些文件。</summary>
        static Dictionary<string, DateTime> SnapshotExportTimes()
        {
            var result = new Dictionary<string, DateTime>();
            string dir = ExportDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                return result;
            }

            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                result[Path.GetFileName(file)] = File.GetLastWriteTimeUtc(file);
            }
            return result;
        }

        /// <summary>
        /// 找出这次导出真正写过的文件（新增的，或写入时间变了的），取名字排最前的一个。
        /// _index.json 是脚本顺带写的索引，不算卡树的导出结果，跳过。
        /// </summary>
        static string FindFreshExportFile(Dictionary<string, DateTime> before)
        {
            string dir = ExportDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var fresh = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "_index.json", StringComparison.Ordinal))
                {
                    continue;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(file);
                DateTime previous;
                if (before == null || !before.TryGetValue(name, out previous) || previous != stamp)
                {
                    fresh.Add(name);
                }
            }

            fresh.Sort(StringComparer.OrdinalIgnoreCase);
            return fresh.Count > 0 ? fresh[0] : null;
        }

        void RevealExportFolder()
        {
            string dir = ExportDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            EditorUtility.RevealInFinder(dir);
        }

        /// <summary>取输出末尾若干字符，弹窗里只展示最有用的那一段。</summary>
        static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(脚本没有输出)";
            }
            if (text.Length <= maxChars)
            {
                return text;
            }
            return "…" + text.Substring(text.Length - maxChars);
        }

    }
}
