using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 把卡树数据渲染成可拖拽、可连线、可增删的图。
    /// 图只负责显示和交互，模型（CanvasExport）由窗口持有，
    /// 这里通过回调把「用户干了什么」告诉窗口。
    /// </summary>
    public class CardTreeGraphView : GraphView
    {
        const float CardWidth = 280f;
        const float CardHeight = 190f;
        const float NewCardGap = 90f;

        /// <summary>连线标签的 UQuery 名字，改标签时靠它把已有 Label 找回来。</summary>
        const string EdgeLabelName = "card-tree-edge-label";

        readonly Dictionary<string, CardNodeView> _nodeById = new Dictionary<string, CardNodeView>();

        /// <summary>重建视图期间置位，否则「我们自己删元素」会被误当成用户删除。</summary>
        bool _suppressModelCallbacks;

        /// <summary>点选某张卡时回调，用于把右侧面板切过去。</summary>
        public Action<CardData> OnCardSelected;

        /// <summary>双击某张卡时回调，用于打开「战斗内升级」填写窗口。</summary>
        public Action<CardData> OnUpgradeRequested;

        /// <summary>用户删掉了某张卡。</summary>
        public Action<CardData> OnCardRemoved;

        /// <summary>用户删掉了某条连线（用 viewDataKey 对应模型里的 edge id）。</summary>
        public Action<Edge> OnEdgeRemoved;

        /// <summary>用户在两点之间新拉了一条连线。</summary>
        public Action<CardData, CardData, Edge> OnEdgeConnected;

        /// <summary>用户拖动过节点，模型里的坐标需要跟着更新。</summary>
        public Action OnElementsMoved;

        /// <summary>用户从右键菜单点了「新建卡牌」。</summary>
        public Action OnCreateCardRequested;

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

            graphViewChanged = HandleGraphViewChanged;

            // 选中状态是 GraphView 内部在事件处理过程中改的，回调里直接读可能还是旧值，
            // 所以延到下一次编辑器 tick 再通知窗口，这样和内部处理顺序就无关了。
            RegisterCallback<MouseDownEvent>(evt => schedule.Execute(NotifySelectionMaybeChanged));
            RegisterCallback<KeyUpEvent>(evt => schedule.Execute(NotifySelectionMaybeChanged));
        }

        /// <summary>选中项可能变化时回调（点卡、点连线、点空白、框选都会触发）。</summary>
        public Action OnSelectionMaybeChanged;

        /// <summary>当前选中的卡牌，没选中返回 null。</summary>
        public CardData SelectedCardData
        {
            get
            {
                foreach (ISelectable selectable in selection)
                {
                    var node = selectable as CardNodeView;
                    if (node != null)
                    {
                        return node.Card;
                    }
                }
                return null;
            }
        }

        /// <summary>当前选中的连线，没选中返回 null。</summary>
        public Edge SelectedEdge
        {
            get
            {
                foreach (ISelectable selectable in selection)
                {
                    var edge = selectable as Edge;
                    if (edge != null)
                    {
                        return edge;
                    }
                }
                return null;
            }
        }

        void NotifySelectionMaybeChanged()
        {
            if (_suppressModelCallbacks)
            {
                return;
            }
            if (OnSelectionMaybeChanged != null)
            {
                OnSelectionMaybeChanged();
            }
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            return ports
                .Where(port => port.direction != startPort.direction
                               && port.node != startPort.node
                               && (port.capacity == Port.Capacity.Multi || !port.connections.Any()))
                .ToList();
        }

        /// <summary>右键菜单：加卡、删选中项。</summary>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            base.BuildContextualMenu(evt);

            evt.menu.AppendAction(
                "新建卡牌",
                action => RequestCreateCard(),
                DropdownMenuAction.AlwaysEnabled);

            evt.menu.AppendAction(
                "删除选中项",
                action => DeleteSelection(),
                DropdownMenuAction.AlwaysEnabled);
        }

        void RequestCreateCard()
        {
            if (OnCreateCardRequested != null)
            {
                OnCreateCardRequested();
            }
        }

        // ------------------------------------------------------------------
        // 加载 / 增量更新
        // ------------------------------------------------------------------

        public void Load(CanvasExport data, bool showGroupNodes)
        {
            _suppressModelCallbacks = true;
            try
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
                    AttachNode(card);
                }

                ApplyLayout(cards, edges);
                AddEdges(edges);
            }
            finally
            {
                _suppressModelCallbacks = false;
            }
        }

        /// <summary>往图里加一张卡（不动模型，模型由窗口负责）。返回新节点。</summary>
        public CardNodeView AddCardNode(CardData card)
        {
            CardNodeView node = AttachNode(card);
            node.SetPosition(new Rect(NextFreePosition(), new Vector2(CardWidth, CardHeight)));
            return node;
        }

        /// <summary>某一格的字段被改过之后，重绘那张卡。</summary>
        public void RefreshCard(string cardId)
        {
            CardNodeView node;
            if (cardId != null && _nodeById.TryGetValue(cardId, out node))
            {
                node.RefreshFromCard();
            }
        }

        /// <summary>选中某张卡，让右侧面板能立刻显示它。</summary>
        public void SelectCard(string cardId)
        {
            CardNodeView node;
            if (cardId == null || !_nodeById.TryGetValue(cardId, out node))
            {
                return;
            }
            ClearSelection();
            AddToSelection(node);
        }

        /// <summary>把图上节点的实际坐标写回模型，保证存盘时布局是最新的。</summary>
        public void SyncPositionsToModel()
        {
            foreach (CardNodeView node in _nodeById.Values)
            {
                Rect rect = node.GetPosition();
                if (node.Card.position == null)
                {
                    node.Card.position = new PositionData();
                }
                node.Card.position.x = rect.x;
                node.Card.position.y = rect.y;
            }
        }

        CardNodeView AttachNode(CardData card)
        {
            var node = new CardNodeView(card, !card.IsGroup);
            node.OnSelected = card2 => { if (OnCardSelected != null) OnCardSelected(card2); };
            node.OnUpgradeRequested = card2 => { if (OnUpgradeRequested != null) OnUpgradeRequested(card2); };
            _nodeById[card.id] = node;
            AddElement(node);
            return node;
        }

        // ------------------------------------------------------------------
        // 用户操作回传给窗口
        // ------------------------------------------------------------------

        GraphViewChange HandleGraphViewChanged(GraphViewChange change)
        {
            if (_suppressModelCallbacks)
            {
                return change;
            }

            List<CardData> removedCards = null;
            List<Edge> removedEdges = null;

            if (change.elementsToRemove != null)
            {
                foreach (GraphElement element in change.elementsToRemove)
                {
                    var node = element as CardNodeView;
                    if (node != null)
                    {
                        if (removedCards == null)
                        {
                            removedCards = new List<CardData>();
                        }
                        removedCards.Add(node.Card);
                        _nodeById.Remove(node.Card.id);
                        continue;
                    }

                    var edge = element as Edge;
                    if (edge != null)
                    {
                        if (removedEdges == null)
                        {
                            removedEdges = new List<Edge>();
                        }
                        removedEdges.Add(edge);
                    }
                }
            }

            if (removedEdges != null && OnEdgeRemoved != null)
            {
                foreach (Edge edge in removedEdges)
                {
                    OnEdgeRemoved(edge);
                }
            }

            if (removedCards != null && OnCardRemoved != null)
            {
                foreach (CardData card in removedCards)
                {
                    OnCardRemoved(card);
                }
            }

            if (change.edgesToCreate != null && change.edgesToCreate.Count > 0 && OnEdgeConnected != null)
            {
                foreach (Edge edge in change.edgesToCreate)
                {
                    var fromNode = edge.output != null ? edge.output.node as CardNodeView : null;
                    var toNode = edge.input != null ? edge.input.node as CardNodeView : null;
                    if (fromNode == null || toNode == null)
                    {
                        continue;
                    }
                    OnEdgeConnected(fromNode.Card, toNode.Card, edge);
                }
            }

            if (change.movedElements != null && change.movedElements.Count > 0 && OnElementsMoved != null)
            {
                OnElementsMoved();
            }

            return change;
        }

        // ------------------------------------------------------------------
        // 布局
        // ------------------------------------------------------------------

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

        /// <summary>没有有效坐标时，按连线层级从左到右排。</summary>
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
                    column * (CardWidth + NewCardGap),
                    row * (CardHeight + 40f),
                    CardWidth,
                    CardHeight));
            }
        }

        /// <summary>找到现有节点右侧的空位，用来放新卡，避免叠在一起。</summary>
        Vector2 NextFreePosition()
        {
            bool any = false;
            float maxRight = 0f;
            float top = 0f;
            foreach (CardNodeView node in _nodeById.Values)
            {
                Rect rect = node.GetPosition();
                if (!any)
                {
                    maxRight = rect.xMax;
                    top = rect.y;
                    any = true;
                }
                else
                {
                    maxRight = Mathf.Max(maxRight, rect.xMax);
                    top = Mathf.Min(top, rect.y);
                }
            }
            return any ? new Vector2(maxRight + NewCardGap, top) : new Vector2(0f, 0f);
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
                // 用 viewDataKey 记住模型里的 edge id，删除时才能对应回去
                graphEdge.viewDataKey = edge.id;
                AddElement(graphEdge);
                fromNode.OutputPort.Connect(graphEdge);
                toNode.InputPort.Connect(graphEdge);

                if (!string.IsNullOrEmpty(edge.label))
                {
                    AttachEdgeLabel(graphEdge, edge.label);
                }
            }
        }

        /// <summary>把连线 label 放在连线中点。</summary>
        public static void AttachEdgeLabel(Edge edge, string text)
        {
            var label = new Label(text);
            label.name = EdgeLabelName;
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

        /// <summary>改连线上显示的文字；本来没有标签且新文字为空时什么都不做。</summary>
        public static void SetEdgeLabel(Edge edge, string text)
        {
            if (edge == null || edge.edgeControl == null)
            {
                return;
            }

            Label label = edge.edgeControl.Q<Label>(EdgeLabelName);
            if (label == null)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                AttachEdgeLabel(edge, text);
                return;
            }

            label.text = text;
        }
    }
}
