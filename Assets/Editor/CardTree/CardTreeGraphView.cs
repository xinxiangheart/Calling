using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>把导出的 Canvas JSON 渲染成可拖拽、可连线的行动卡树。</summary>
    public class CardTreeGraphView : GraphView
    {
        const float CardWidth = 280f;
        const float CardHeight = 190f;

        readonly Dictionary<string, CardNodeView> _nodeById = new Dictionary<string, CardNodeView>();

        /// <summary>点选某张卡时回调，用于底部详情面板。</summary>
        public Action<CardData> OnCardSelected;

        public CardTreeGraphView()
        {
            style.flexGrow = 1f;
            style.backgroundColor = new StyleColor(new Color(0.11f, 0.11f, 0.13f));

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new ContentZoomer());
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            return ports
                .Where(port => port.direction != startPort.direction
                               && port.node != startPort.node
                               && (port.capacity == Port.Capacity.Multi || !port.connections.Any()))
                .ToList();
        }

        public void Load(CanvasExport data, bool showGroupNodes)
        {
            DeleteElements(graphElements.ToList());
            _nodeById.Clear();

            if (data == null)
            {
                return;
            }

            CardData[] cards = data.cards ?? new CardData[0];
            EdgeData[] edges = data.edges ?? new EdgeData[0];

            foreach (CardData card in cards)
            {
                if (card == null || string.IsNullOrEmpty(card.id))
                {
                    continue;
                }
                if (card.IsGroup && !showGroupNodes)
                {
                    continue;
                }

                var node = new CardNodeView(card, !card.IsGroup);
                node.OnSelected = HandleCardSelected;
                _nodeById[card.id] = node;
                AddElement(node);
            }

            ApplyLayout(cards, edges);
            AddEdges(edges);
        }

        void HandleCardSelected(CardData card)
        {
            if (OnCardSelected != null)
            {
                OnCardSelected(card);
            }
        }

        void ApplyLayout(CardData[] cards, EdgeData[] edges)
        {
            var visible = cards.Where(c => c != null && _nodeById.ContainsKey(c.id)).ToList();
            if (visible.Count == 0)
            {
                return;
            }

            int distinctPositions = visible
                .Select(c => c.position == null
                    ? "none"
                    : (Mathf.Round(c.position.x) + "|" + Mathf.Round(c.position.y)))
                .Distinct()
                .Count();

            if (distinctPositions > 1)
            {
                foreach (CardData card in visible)
                {
                    float x = card.position != null ? card.position.x : 0f;
                    float y = card.position != null ? card.position.y : 0f;
                    _nodeById[card.id].SetPosition(new Rect(x, y, CardWidth, CardHeight));
                }
                return;
            }

            ApplyLayeredLayout(visible, edges);
        }

        /// <summary>画布里没有有效坐标时，按连线层级从左到右排。</summary>
        void ApplyLayeredLayout(List<CardData> cards, EdgeData[] edges)
        {
            var ids = new HashSet<string>(cards.Select(c => c.id));
            var depth = new Dictionary<string, int>();
            var parents = new Dictionary<string, List<string>>();
            foreach (CardData card in cards)
            {
                depth[card.id] = 0;
                parents[card.id] = new List<string>();
            }

            foreach (EdgeData edge in edges)
            {
                if (edge == null || !ids.Contains(edge.from) || !ids.Contains(edge.to))
                {
                    continue;
                }
                parents[edge.to].Add(edge.from);
            }

            for (int pass = 0; pass < cards.Count; pass++)
            {
                bool changed = false;
                foreach (CardData card in cards)
                {
                    foreach (string parent in parents[card.id])
                    {
                        int candidate = depth[parent] + 1;
                        if (candidate > depth[card.id] && candidate <= cards.Count)
                        {
                            depth[card.id] = candidate;
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }

            var rowInColumn = new Dictionary<int, int>();
            foreach (CardData card in cards.OrderBy(c => depth[c.id]).ThenBy(c => c.id, StringComparer.Ordinal))
            {
                int column = depth[card.id];
                int row = rowInColumn.ContainsKey(column) ? rowInColumn[column] : 0;
                rowInColumn[column] = row + 1;
                _nodeById[card.id].SetPosition(new Rect(
                    column * (CardWidth + 90f),
                    row * (CardHeight + 40f),
                    CardWidth,
                    CardHeight));
            }
        }

        void AddEdges(EdgeData[] edges)
        {
            foreach (EdgeData edge in edges)
            {
                if (edge == null)
                {
                    continue;
                }

                CardNodeView fromNode;
                CardNodeView toNode;
                if (!_nodeById.TryGetValue(edge.from ?? string.Empty, out fromNode))
                {
                    continue;
                }
                if (!_nodeById.TryGetValue(edge.to ?? string.Empty, out toNode))
                {
                    continue;
                }
                if (fromNode.OutputPort == null || toNode.InputPort == null)
                {
                    continue;
                }

                var graphEdge = new Edge { output = fromNode.OutputPort, input = toNode.InputPort };
                graphEdge.UpdateEdgeControl();
                AddElement(graphEdge);
                fromNode.OutputPort.Connect(graphEdge);
                toNode.InputPort.Connect(graphEdge);

                if (!string.IsNullOrEmpty(edge.label))
                {
                    AttachEdgeLabel(graphEdge, edge.label);
                }
            }
        }

        static void AttachEdgeLabel(Edge edge, string text)
        {
            var label = new Label(text);
            label.pickingMode = PickingMode.Ignore;
            label.style.position = Position.Absolute;
            label.style.fontSize = 10f;
            label.style.color = new StyleColor(new Color(0.78f, 0.84f, 0.95f));
            label.style.backgroundColor = new StyleColor(new Color(0.10f, 0.11f, 0.14f, 0.92f));
            label.style.paddingLeft = 4f;
            label.style.paddingRight = 4f;
            label.style.paddingTop = 1f;
            label.style.paddingBottom = 1f;
            label.style.borderTopLeftRadius = 3f;
            label.style.borderTopRightRadius = 3f;
            label.style.borderBottomLeftRadius = 3f;
            label.style.borderBottomRightRadius = 3f;

            edge.edgeControl.Add(label);
            edge.edgeControl.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                label.style.left = (edge.edgeControl.layout.width - label.layout.width) * 0.5f;
                label.style.top = (edge.edgeControl.layout.height - label.layout.height) * 0.5f;
            });
        }
    }
}
