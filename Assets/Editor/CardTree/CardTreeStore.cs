using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 卡树的持久化：读写 Assets/Resources/CardTrees/*.json。
    ///
    /// 为什么放 Resources 下：Resources 是 Unity 的特殊目录，
    /// 运行时可以用 Resources.Load&lt;TextAsset&gt;("CardTrees/名字") 直接加载，
    /// 不需要额外配置 AssetBundle 或 Addressables。
    ///
    /// 存盘格式和 Python 导出的 JSON 是同一套字段，
    /// 所以策划在画布里画的卡和 Unity 里改的卡可以互相导入导出。
    /// </summary>
    public static class CardTreeStore
    {
        /// <summary>相对 Unity 项目根目录的卡树目录。必须在 Assets 下，Resources 才会被打进包。</summary>
        public const string TreeDirectoryAssetPath = "Assets/Resources/CardTrees";

        /// <summary>Unity 项目根目录（Assets 的上一级）。</summary>
        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>卡树目录的完整磁盘路径。</summary>
        public static string TreeDirectoryFullPath
        {
            get { return Path.Combine(ProjectRoot, TreeDirectoryAssetPath.Replace('/', Path.DirectorySeparatorChar)); }
        }

        public static void EnsureDirectory()
        {
            Directory.CreateDirectory(TreeDirectoryFullPath);
        }

        /// <summary>列出所有已保存的卡树（完整路径，按文件名排序）。目录不存在时返回空数组。</summary>
        public static string[] ListTreeFiles()
        {
            string dir = TreeDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                return new string[0];
            }
            string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>把名字里不能用于文件系统的字符换成下划线，避免用户输入怪名字导致写盘失败。</summary>
        public static string SanitizeName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return string.Empty;
            }

            string trimmed = rawName.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(trimmed.Length);
            foreach (char c in trimmed)
            {
                bool isInvalid = false;
                foreach (char bad in invalid)
                {
                    if (c == bad)
                    {
                        isInvalid = true;
                        break;
                    }
                }
                builder.Append(isInvalid ? '_' : c);
            }
            return builder.ToString().Trim();
        }

        /// <summary>按名字算出卡树文件的完整路径。</summary>
        public static string FullPathForName(string treeName)
        {
            return Path.Combine(TreeDirectoryFullPath, SanitizeName(treeName) + ".json");
        }

        /// <summary>把绝对路径转成 "Assets/..." 形式，AssetDatabase 需要这种路径。</summary>
        public static string ToAssetPath(string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
            int index = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized.Substring(index + 1);
            }
            return normalized;
        }

        /// <summary>新建一个空卡树。</summary>
        public static CanvasExport CreateEmptyTree(string treeName)
        {
            return new CanvasExport
            {
                schema_version = "1.0",
                cards = new CardData[0],
                edges = new EdgeData[0],
                source = new SourceData
                {
                    canvas = null,
                    filename = SanitizeName(treeName) + ".json",
                    bytes = 0,
                },
                stats = new StatsData(),
            };
        }

        /// <summary>保存卡树，返回写出的完整路径。同名会被直接覆盖。</summary>
        public static string Save(CanvasExport data, string treeName)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            string safeName = SanitizeName(treeName);
            if (string.IsNullOrEmpty(safeName))
            {
                throw new ArgumentException("卡树名字不能为空", "treeName");
            }

            EnsureDirectory();
            string target = FullPathForName(safeName);

            // 存盘前重算派生字段，保证文件内容和内存模型一致
            RefreshDerivedFields(data, safeName);

            // 透传写法：以原始 JSON 为底稿，把模型认识的字段覆盖上去。
            // 模型不认识的字段（canvas 里自加的、Python 新导出的）原样保留，不丢。
            JObject root = BuildRootForSave(data);
            OverwriteModelFields(root, data);

            // UTF-8 且不加 BOM，和 Python 导出的保持一致
            File.WriteAllText(target, root.ToString(Formatting.Indented), new UTF8Encoding(false));

            // 立刻让 Unity 导入，否则资源库要等下次刷新才认识这个文件
            AssetDatabase.ImportAsset(
                ToAssetPath(target),
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            return target;
        }

        /// <summary>重算 stats、generated_at、source，避免存进去的是过期的统计。</summary>
        public static void RefreshDerivedFields(CanvasExport data, string treeName)
        {
            if (data.cards == null)
            {
                data.cards = new CardData[0];
            }
            if (data.edges == null)
            {
                data.edges = new EdgeData[0];
            }
            if (string.IsNullOrEmpty(data.schema_version))
            {
                data.schema_version = "1.0";
            }

            if (data.source == null)
            {
                data.source = new SourceData();
            }
            data.source.filename = SanitizeName(treeName) + ".json";
            data.source.bytes = 0;

            int textNodes = 0;
            int groupNodes = 0;
            int otherNodes = 0;
            foreach (CardData card in data.cards)
            {
                if (card == null)
                {
                    continue;
                }
                if (card.IsGroup)
                {
                    groupNodes++;
                }
                else if (card.type == "text")
                {
                    textNodes++;
                }
                else
                {
                    otherNodes++;
                }
            }

            data.stats = new StatsData
            {
                nodes = data.cards.Length,
                text_nodes = textNodes,
                group_nodes = groupNodes,
                other_nodes = otherNodes,
                edges = data.edges.Length,
                warnings = 0,
            };

            data.generated_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        }

        // ==================================================================
        // 透传：保住模型不认识的字段
        // ==================================================================

        /// <summary>合并策略：数组整体替换；null 不覆盖已有值。</summary>
        static readonly JsonMergeSettings MergeSettings = new JsonMergeSettings
        {
            MergeArrayHandling = MergeArrayHandling.Replace,
            MergeNullValueHandling = MergeNullValueHandling.Ignore,
        };

        /// <summary>存盘底稿：有原始 JSON 就拿它当底（深拷贝），没有就从空对象开始。</summary>
        static JObject BuildRootForSave(CanvasExport data)
        {
            return data._rawRoot != null ? (JObject)data._rawRoot.DeepClone() : new JObject();
        }

        /// <summary>
        /// 把模型认识的字段覆盖到底稿上；模型里为 null 的字段一律跳过，
        /// 免得把原始 JSON 里已有的值抹成 null。
        /// </summary>
        static void OverwriteModelFields(JObject root, CanvasExport data)
        {
            JObject modelObject = JObject.FromObject(data);
            RemoveNulls(modelObject);

            // cards / edges 必须逐项按 id 合并，整体替换会把每张卡上的未知字段冲掉
            var modelCards = modelObject["cards"] as JArray;
            var modelEdges = modelObject["edges"] as JArray;
            modelObject.Remove("cards");
            modelObject.Remove("edges");

            root.Merge(modelObject, MergeSettings);

            JArray mergedCards = MergeItemArray(root["cards"] as JArray, modelCards);

            // 变量要等合并之后再覆盖：对象合并只会覆盖同名的键，
            // 如果先写进 modelObject，面板上删掉的变量会因为原始 JSON 里
            // 还留着而被原样保留下来（存一次回来又出现了）。
            ApplyVariableTokens(mergedCards, data);
            ApplyKeywordTokens(mergedCards, data);
            DropCardDerivedFields(mergedCards);
            root["cards"] = mergedCards;

            root["edges"] = MergeItemArray(root["edges"] as JArray, modelEdges);

            DropDerivedFields(root);
        }

        /// <summary>
        /// 派生字段名单：这些是 Python 从卡牌列表和连线推导出来的，不是设计数据本身。
        /// 在 Unity 里改完图之后它们就过期了，留着一份假数据比完全没有更危险，
        /// 所以存盘时直接丢掉。将来如果某个字段需要保留，从这份名单里去掉即可。
        /// </summary>
        static readonly string[] DerivedFieldNames = { "adjacency", "forest", "warnings" };

        /// <summary>丢弃派生字段。必须在顶层合并之后调用，否则底稿里的旧值又会被带回来。</summary>
        static void DropDerivedFields(JObject root)
        {
            foreach (string name in DerivedFieldNames)
            {
                root.Remove(name);
            }
        }

        /// <summary>
        /// 每张卡自己的派生字段。bindings 是 Python 从 variables + effect 推出来的
        /// 着色/取值表（契约 §5），在 Unity 里改了效果它就过期了——留着的话下次加载
        /// 会拿旧颜色骗人，所以存盘丢掉，等下次导出重新生成。
        /// </summary>
        static readonly string[] CardDerivedFieldNames = { "bindings" };

        /// <summary>丢弃每张卡上的派生字段。必须在卡片数组合并之后调用。</summary>
        static void DropCardDerivedFields(JArray cardsArray)
        {
            if (cardsArray == null)
            {
                return;
            }

            foreach (JToken token in cardsArray)
            {
                var card = token as JObject;
                if (card == null)
                {
                    continue;
                }

                foreach (string name in CardDerivedFieldNames)
                {
                    card.Remove(name);
                }
            }
        }

        /// <summary>
        /// 把每张卡的变量列表写回 variables.items / variables.meta / variables.raw。
        ///
        /// 必须显式写：VariablesData.items 标了 [JsonIgnore] 不参与序列化，
        /// 不写的话合并时会保留原始 items，面板上的编辑就白改了。
        /// raw 也顺手拼回一遍，避免 raw 和 items 两处说法不一致。
        ///
        /// 传入的是合并之后的卡片数组，必须在 MergeItemArray 之后调用：
        /// 直接赋值（variables["items"] = ...）是整体替换，
        /// 而 JObject.Merge 是递归合并、删不掉键。
        /// </summary>
        static void ApplyVariableTokens(JArray cardsArray, CanvasExport data)
        {
            if (cardsArray == null || data.cards == null)
            {
                return;
            }

            var cardById = new Dictionary<string, CardData>();
            foreach (CardData card in data.cards)
            {
                if (card != null && !string.IsNullOrEmpty(card.id))
                {
                    cardById[card.id] = card;
                }
            }

            foreach (JToken token in cardsArray)
            {
                var cardObject = token as JObject;
                if (cardObject == null || cardObject["id"] == null)
                {
                    continue;
                }

                CardData card;
                if (!cardById.TryGetValue(cardObject["id"].ToString(), out card))
                {
                    continue;
                }

                List<VariableItem> items = card.variables != null ? card.variables.items : null;

                JObject itemsToken = VariableCodec.BuildItemsToken(items);
                JObject metaToken = VariableCodec.BuildMetaToken(items);

                // 无变量的卡不写空壳：items / meta / raw 全空时把整个 variables 键删掉，
                // 免得每张卡都挂一份 {"items":{},"meta":{},"raw":""} 的无意义噪音。
                if (itemsToken.Count == 0
                    && metaToken.Count == 0
                    && (card.variables == null || string.IsNullOrEmpty(card.variables.raw)))
                {
                    cardObject.Remove("variables");
                    if (card.variables != null)
                    {
                        card.variables.raw = null;
                    }
                    continue;
                }

                var variables = cardObject["variables"] as JObject;
                if (variables == null)
                {
                    variables = new JObject();
                    cardObject["variables"] = variables;
                }

                variables["items"] = itemsToken;
                variables["meta"] = metaToken;

                // 同步内存模型：让"存下去的内容"和"界面上看到的"是同一份
                if (card.variables != null)
                {
                    card.variables.raw = VariableCodec.BuildRaw(items);
                }
                variables["raw"] = card.variables != null ? card.variables.raw : string.Empty;
            }
        }

        /// <summary>
        /// 把每张卡的词条列表写回 keywords 数组。
        ///
        /// 只在有词条时才写；空列表直接把整个键删掉，免得每张没词条的卡都挂一个
        /// "keywords": [] 的无意义噪音。
        ///
        /// 和 ApplyVariableTokens 同理，必须在 MergeItemArray 之后调用：
        /// 数组本身是整体替换，但"删掉多余的键" Merge 做不到，只能自己来。
        /// </summary>
        static void ApplyKeywordTokens(JArray cardsArray, CanvasExport data)
        {
            if (cardsArray == null || data.cards == null)
            {
                return;
            }

            var cardById = new Dictionary<string, CardData>();
            foreach (CardData card in data.cards)
            {
                if (card != null && !string.IsNullOrEmpty(card.id))
                {
                    cardById[card.id] = card;
                }
            }

            foreach (JToken token in cardsArray)
            {
                var cardObject = token as JObject;
                if (cardObject == null || cardObject["id"] == null)
                {
                    continue;
                }

                CardData card;
                if (!cardById.TryGetValue(cardObject["id"].ToString(), out card))
                {
                    continue;
                }

                if (card.keywords == null || card.keywords.Count == 0)
                {
                    cardObject.Remove("keywords");
                    continue;
                }

                var array = new JArray();
                foreach (string keyword in card.keywords)
                {
                    // 列表里混进 null（手改 JSON 才可能）时写成空串，
                    // 交给校验去报"空白词条"，而不是往 JSON 里塞一个 null
                    array.Add(keyword != null ? keyword : string.Empty);
                }
                cardObject["keywords"] = array;
            }
        }

        /// <summary>按 id 把新数组逐项合并到旧数组上，每一项都保住模型不认识的字段。</summary>
        static JArray MergeItemArray(JArray rawItems, JArray modelItems)
        {
            var result = new JArray();
            if (modelItems == null)
            {
                // 模型里没有这一项 = 被删光了，输出空数组
                return result;
            }

            var rawById = new Dictionary<string, JObject>();
            if (rawItems != null)
            {
                foreach (JToken token in rawItems)
                {
                    var rawItem = token as JObject;
                    if (rawItem == null || rawItem["id"] == null)
                    {
                        continue;
                    }
                    string rawId = rawItem["id"].ToString();
                    if (!string.IsNullOrEmpty(rawId) && !rawById.ContainsKey(rawId))
                    {
                        rawById[rawId] = rawItem;
                    }
                }
            }

            foreach (JToken token in modelItems)
            {
                var modelItem = token as JObject;
                if (modelItem == null)
                {
                    continue;
                }

                JObject rawItem;
                string id = modelItem["id"] != null ? modelItem["id"].ToString() : null;
                if (!string.IsNullOrEmpty(id) && rawById.TryGetValue(id, out rawItem))
                {
                    // 深拷贝再合并：原 token 还挂在别的父节点上
                    var merged = (JObject)rawItem.DeepClone();
                    merged.Merge(modelItem, MergeSettings);
                    result.Add(merged);
                }
                else
                {
                    // 新建的卡/线：没有原始对象，直接按模型输出
                    result.Add(modelItem.DeepClone());
                }
            }

            return result;
        }

        /// <summary>递归删掉值为 null 的键：这里 null 表示"模型没说，别动原始值"。</summary>
        static void RemoveNulls(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var names = new List<string>();
                foreach (JProperty property in obj.Properties())
                {
                    names.Add(property.Name);
                }

                foreach (string name in names)
                {
                    JProperty property = obj.Property(name);
                    if (property == null)
                    {
                        continue;
                    }
                    if (property.Value.Type == JTokenType.Null)
                    {
                        property.Remove();
                    }
                    else
                    {
                        RemoveNulls(property.Value);
                    }
                }
                return;
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (JToken item in array)
                {
                    RemoveNulls(item);
                }
            }
        }
    }
}
