using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 一张行动卡在 GraphView 里的节点表示。
    /// 这里是只读展示，所有编辑都在窗口右侧的面板里做——
    /// 节点内塞 TextField 会抢走节点的拖拽和选择事件，反而不好用。
    /// </summary>
    public class CardNodeView : Node
    {
        readonly CardData _card;

        public CardData Card
        {
            get { return _card; }
        }

        public Port InputPort { get; private set; }
        public Port OutputPort { get; private set; }

        /// <summary>点选时回调，用来把右侧编辑面板切到这张卡。</summary>
        /// <remarks>加 new 是因为 GraphElement 上有个同名的 OnSelected() 虚方法，不加会报 CS0108。</remarks>
        public new Action<CardData> OnSelected;

        /// <summary>双击时回调，用来打开「战斗内升级」填写窗口。</summary>
        public Action<CardData> OnUpgradeRequested;

        public CardNodeView(CardData card, bool withPorts)
        {
            _card = card;
            name = card.id;
            viewDataKey = card.id;

            style.width = 280f;
            style.minHeight = 80f;
            style.backgroundColor = new StyleColor(new Color(0.16f, 0.17f, 0.20f));
            ApplyBorder(new Color(0.30f, 0.33f, 0.40f));
            titleContainer.style.backgroundColor = new StyleColor(
                card.IsGroup ? new Color(0.26f, 0.23f, 0.16f) : RarityColor(card.rarity));

            extensionContainer.Add(BuildBody());

            if (withPorts)
            {
                InputPort = Port.Create<Edge>(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
                InputPort.portName = "前置";
                inputContainer.Add(InputPort);

                OutputPort = Port.Create<Edge>(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
                OutputPort.portName = "后续";
                outputContainer.Add(OutputPort);
            }

            RefreshTitle();
            RefreshExpandedState();
            RefreshPorts();

            RegisterCallback<MouseDownEvent>(OnMouseDown);
        }

        /// <summary>内存模型被改过之后刷新节点显示（标题 + 正文）。</summary>
        public void RefreshFromCard()
        {
            RefreshTitle();
            extensionContainer.Clear();
            extensionContainer.Add(BuildBody());
            RefreshExpandedState();
            RefreshPorts();
        }

        void RefreshTitle()
        {
            // 已填战斗内升级的卡在标题上加个记号，一眼能扫出来
            title = string.IsNullOrEmpty(_card.combat_upgrade)
                ? _card.DisplayName
                : _card.DisplayName + "  ⚔";
        }

        void OnMouseDown(MouseDownEvent evt)
        {
            if (evt.clickCount == 2)
            {
                if (OnUpgradeRequested != null)
                {
                    OnUpgradeRequested(_card);
                }
                return;
            }

            if (OnSelected != null)
            {
                OnSelected(_card);
            }
        }

        void ApplyBorder(Color color)
        {
            var brush = new StyleColor(color);
            style.borderTopWidth = 1f;
            style.borderBottomWidth = 1f;
            style.borderLeftWidth = 1f;
            style.borderRightWidth = 1f;
            style.borderTopColor = brush;
            style.borderBottomColor = brush;
            style.borderLeftColor = brush;
            style.borderRightColor = brush;
            style.borderTopLeftRadius = 6f;
            style.borderTopRightRadius = 6f;
            style.borderBottomLeftRadius = 6f;
            style.borderBottomRightRadius = 6f;
        }

        VisualElement BuildBody()
        {
            var body = new VisualElement();
            body.style.paddingLeft = 6f;
            body.style.paddingRight = 6f;
            body.style.paddingTop = 4f;
            body.style.paddingBottom = 6f;

            if (_card.IsGroup)
            {
                AddRow(body, "分组", _card.DisplayName, null);
                return body;
            }

            AddRow(body, "品级", _card.rarity, null);
            AddRow(body, "类别", _card.category, null);
            AddRow(body, "效果", _card.effect, null);
            if (_card.variables != null && !string.IsNullOrEmpty(_card.variables.raw))
            {
                AddRow(body, "变量", _card.variables.raw, null);
            }
            AddRow(body, "获取", _card.acquisition, null);
            AddRow(body, "意图", _card.design_intent, null);
            AddRow(body, "升级", _card.combat_upgrade, new Color(0.95f, 0.78f, 0.35f));

            if (_card.missing_fields != null && _card.missing_fields.Length > 0)
            {
                AddRow(body, "缺失", string.Join("、", _card.missing_fields), new Color(0.95f, 0.55f, 0.45f));
            }

            return body;
        }

        static void AddRow(VisualElement parent, string label, string value, Color? valueColor)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 2f;

            var key = new Label(label);
            key.style.width = 30f;
            key.style.minWidth = 30f;
            key.style.fontSize = 10f;
            key.style.color = new StyleColor(new Color(0.52f, 0.58f, 0.68f));
            row.Add(key);

            var val = new Label(value);
            val.style.fontSize = 11f;
            val.style.flexShrink = 1f;
            val.style.whiteSpace = WhiteSpace.Normal;
            val.style.color = new StyleColor(valueColor ?? new Color(0.86f, 0.88f, 0.92f));
            row.Add(val);

            parent.Add(row);
        }

        /// <summary>按品级给标题上色，方便在树里一眼看出强度分布。</summary>
        public static Color RarityColor(string rarity)
        {
            switch (rarity)
            {
                case "普通":
                    return new Color(0.36f, 0.40f, 0.46f);
                case "稀有":
                    return new Color(0.18f, 0.44f, 0.74f);
                case "史诗":
                    return new Color(0.50f, 0.28f, 0.70f);
                case "传说":
                    return new Color(0.82f, 0.58f, 0.14f);
                default:
                    return new Color(0.32f, 0.34f, 0.40f);
            }
        }
    }
}
