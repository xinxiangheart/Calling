using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>一张行动卡在 GraphView 里的节点表示。</summary>
    public class CardNodeView : Node
    {
        readonly CardData _card;

        public CardData Card
        {
            get { return _card; }
        }

        public Port InputPort { get; private set; }
        public Port OutputPort { get; private set; }

        public Action<CardData> OnSelected;

        public CardNodeView(CardData card, bool withPorts)
        {
            _card = card;
            title = card.DisplayName;
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

            RefreshExpandedState();
            RefreshPorts();

            RegisterCallback<MouseDownEvent>(OnMouseDown);
        }

        void OnMouseDown(MouseDownEvent evt)
        {
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
                AddRow(body, "分组", _card.DisplayName);
                return body;
            }

            AddRow(body, "品级", _card.rarity);
            AddRow(body, "类别", _card.category);
            AddRow(body, "效果", _card.effect);
            if (_card.variables != null && !string.IsNullOrEmpty(_card.variables.raw))
            {
                AddRow(body, "变量", _card.variables.raw);
            }
            AddRow(body, "负面", _card.drawback);
            AddRow(body, "获取", _card.acquisition);
            if (_card.evolution != null && _card.evolution.targets != null && _card.evolution.targets.Length > 0)
            {
                AddRow(body, "进化", string.Join(" / ", _card.evolution.targets));
            }
            AddRow(body, "意图", _card.design_intent);

            if (_card.missing_fields != null && _card.missing_fields.Length > 0)
            {
                AddRow(body, "缺失", string.Join("、", _card.missing_fields));
            }

            return body;
        }

        static void AddRow(VisualElement parent, string label, string value)
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
            val.style.color = new StyleColor(new Color(0.86f, 0.88f, 0.92f));
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
