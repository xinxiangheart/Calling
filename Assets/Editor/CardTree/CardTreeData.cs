using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DungeonCardDesign.EditorTools
{
    // 字段名必须和 exports/*.json 一致（Newtonsoft 按名字匹配）。
    //
    // 模型只建模"工具需要编辑"的字段，其余一律靠透传：
    //   读入时把原始 JSON 挂在 _rawRoot / _rawCard 上；
    //   存盘时把模型认识的字段覆盖回去，模型不认识的字段原样保留。
    // 所以 canvas 里新加一个【字段】、Python 多导出一个键，走一圈 Unity 都不会丢，
    // 代价是这些字段在 Unity 界面里看不见（要编辑就得在模型里补字段）。

    [Serializable]
    public class PositionData
    {
        public float x;
        public float y;
    }

    [Serializable]
    public class SizeData
    {
        public float width;
        public float height;
    }

    /// <summary>
    /// 一条变量。value 一律用字符串存：
    /// JSON 那边 Python 导出的就是 "伤害": "2"，来回走一圈不会变成 int 又变回字符串。
    /// </summary>
    [Serializable]
    public class VariableItem
    {
        public string key;
        public string value;

        /// <summary>int / float / text，留空按 text 处理（不校验类型）。</summary>
        public string type;

        /// <summary>数值下限，留空表示不校验。</summary>
        public string min;

        /// <summary>数值上限，留空表示不校验。</summary>
        public string max;
    }

    [Serializable]
    public class VariablesData
    {
        public string raw;
        public string[] unparsed;

        /// <summary>
        /// 结构化变量列表，面板上编辑的就是它。
        ///
        /// [JsonIgnore] 的原因：JSON 里同名的 items 是 {"伤害":"2"} 这种对象，
        /// 直接映射不到 List；转换由 VariableCodec 在处理 load/save 时手工完成。
        /// 另外类型的声明（type/min/max）存在兄弟键 meta 里，Python 不管它，靠透传保留。
        /// </summary>
        [JsonIgnore]
        public List<VariableItem> items = new List<VariableItem>();
    }

    [Serializable]
    public class SourceData
    {
        public string canvas;
        public string filename;
        public long bytes;
    }

    [Serializable]
    public class StatsData
    {
        public int nodes;
        public int text_nodes;
        public int group_nodes;
        public int other_nodes;
        public int edges;
        public int warnings;
    }

    [Serializable]
    public class CardData
    {
        public string id;
        public string type;
        public string name;
        public string rarity;
        public string category;
        public string effect;
        public VariablesData variables;
        public string acquisition;
        public string design_intent;
        // 战斗内升级效果，对应【战斗升级】。Unity 侧编辑，画布里暂未纳入字段规范。
        public string combat_upgrade;
        public string[] missing_fields;
        public string raw_text;
        public string color;
        public PositionData position;
        public SizeData size;
        public string parent_group;
        public string[] outgoing;
        public string[] incoming;
        public string[] children;

        /// <summary>
        /// 透传挂载点：这张卡在原始 JSON 里的样子。不参与序列化。
        /// 存盘时用它做底，把模型字段覆盖上去，从而保住 _raw 里模型不认识的字段。
        /// 新卡为 null，表示直接按模型序列化。
        /// </summary>
        [NonSerialized]
        public JToken _rawCard;

        /// <summary>计算属性，不参与序列化（否则存盘会多出 "IsGroup" 这种冗余键）。</summary>
        [JsonIgnore]
        public bool IsGroup
        {
            get { return type == "group"; }
        }

        /// <summary>计算属性，不参与序列化。</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(name) ? id : name; }
        }
    }

    [Serializable]
    public class EdgeData
    {
        public string id;
        public string from;
        public string to;
        public string label;
        public string from_side;
        public string to_side;
        public string color;
    }

    [Serializable]
    public class CanvasExport
    {
        public string schema_version;
        public string generated_at;
        public SourceData source;
        public StatsData stats;
        public CardData[] cards;
        public EdgeData[] edges;

        /// <summary>
        /// 透传挂载点：整份原始 JSON 的根对象。不参与序列化。
        /// 顶层那些模型没建模的键（field_spec / adjacency / forest / warnings …）靠它保留。
        /// </summary>
        [NonSerialized]
        public JObject _rawRoot;
    }

    /// <summary>读取 dungeon-card-design/exports/ 下的导出结果。</summary>
    public static class CardTreeLoader
    {
        public const string DefaultExportDirectory = "dungeon-card-design/exports";

        public static string ExportDirectoryFullPath
        {
            get { return Path.Combine(Directory.GetCurrentDirectory(), DefaultExportDirectory); }
        }

        public static string[] ListExportFiles()
        {
            string dir = ExportDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                return new string[0];
            }

            string[] files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }

        public static CanvasExport Load(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                Debug.LogWarning("[CardTree] 找不到导出文件: " + absolutePath);
                return null;
            }

            try
            {
                string json = File.ReadAllText(absolutePath);

                // 先解析成 JObject 保住原始结构，再映射到模型
                JObject root = JObject.Parse(json);
                CanvasExport data = root.ToObject<CanvasExport>();
                if (data == null)
                {
                    Debug.LogError("[CardTree] JSON 解析结果为空: " + absolutePath);
                    return null;
                }

                if (data.cards == null)
                {
                    data.cards = new CardData[0];
                }
                if (data.edges == null)
                {
                    data.edges = new EdgeData[0];
                }

                // 透传：把原始 JSON 挂上去，存盘时才能把不认识的字段原样写回
                data._rawRoot = root;
                AttachRawCards(root, data.cards);
                FixupVariables(data.cards);

                return data;
            }
            catch (Exception ex)
            {
                Debug.LogError("[CardTree] 解析失败: " + absolutePath + "\n" + ex);
                return null;
            }
        }

        /// <summary>按 id 把原始 JSON 里的卡对象挂到对应 CardData 上，供存盘时逐张合并。</summary>
        static void AttachRawCards(JObject root, CardData[] cards)
        {
            var rawCards = root["cards"] as JArray;
            if (rawCards == null)
            {
                return;
            }

            var cardById = new Dictionary<string, CardData>();
            foreach (CardData card in cards)
            {
                if (card != null && !string.IsNullOrEmpty(card.id) && !cardById.ContainsKey(card.id))
                {
                    cardById[card.id] = card;
                }
            }

            foreach (JToken token in rawCards)
            {
                var rawCard = token as JObject;
                if (rawCard == null || rawCard["id"] == null)
                {
                    continue;
                }

                CardData card;
                if (cardById.TryGetValue(rawCard["id"].ToString(), out card))
                {
                    card._rawCard = rawCard;
                }
            }
        }

        /// <summary>把每张卡 JSON 里的 variables.items / meta 还原成可编辑的变量列表。</summary>
        static void FixupVariables(CardData[] cards)
        {
            if (cards == null)
            {
                return;
            }

            foreach (CardData card in cards)
            {
                if (card == null)
                {
                    continue;
                }
                if (card.variables == null)
                {
                    card.variables = new VariablesData();
                }

                JToken variablesToken = card._rawCard != null ? card._rawCard["variables"] : null;
                card.variables.items = VariableCodec.ParseItems(variablesToken);
            }
        }
    }

    /// <summary>
    /// 变量与效果模板的编解码、取值、校验。
    ///
    /// 放在数据层是为了让窗口只管画界面，这里的逻辑能单独跑测试。
    /// 约定：
    ///   效果文本里用 {变量名} 引用变量，例如 "造成 {伤害} 点伤害"
    ///   变量名带 _LvN 后缀表示只在该等级生效（如 伤害_Lv1）；不带后缀的各等级共用
    ///   JSON 里 variables.items 是 {"伤害":"2"}，类型声明放在兄弟键 variables.meta
    /// </summary>
    public static class VariableCodec
    {
        /// <summary>等级上限，对应窗口上的 Lv.1~Lv.3 按钮。</summary>
        public const int MaxLevel = 3;

        /// <summary>类型声明存放的键名，挂在 variables 下面。</summary>
        public const string MetaKey = "meta";

        /// <summary>从 JSON 的 variables 对象还原变量列表，兼容对象和数组两种写法。</summary>
        public static List<VariableItem> ParseItems(JToken variablesToken)
        {
            var result = new List<VariableItem>();
            if (variablesToken == null)
            {
                return result;
            }

            JToken metaToken = variablesToken[MetaKey];
            JToken itemsToken = variablesToken["items"];

            // 格式一（Python 导出的就是这个）：{"items": {"伤害": "2"}}
            var itemsObject = itemsToken as JObject;
            if (itemsObject != null)
            {
                foreach (JProperty property in itemsObject.Properties())
                {
                    var item = new VariableItem
                    {
                        key = property.Name,
                        value = property.Value != null && property.Value.Type != JTokenType.Null
                            ? property.Value.ToString()
                            : string.Empty,
                    };
                    ApplyMeta(item, metaToken);
                    result.Add(item);
                }
                return result;
            }

            // 格式二：[{"key": "伤害", "value": "2"}]
            var itemsArray = itemsToken as JArray;
            if (itemsArray != null)
            {
                foreach (JToken token in itemsArray)
                {
                    var obj = token as JObject;
                    if (obj == null)
                    {
                        continue;
                    }

                    var item = new VariableItem
                    {
                        key = obj["key"] != null ? obj["key"].ToString() : string.Empty,
                        value = obj["value"] != null ? obj["value"].ToString() : string.Empty,
                        type = obj["type"] != null ? obj["type"].ToString() : null,
                        min = obj["min"] != null ? obj["min"].ToString() : null,
                        max = obj["max"] != null ? obj["max"].ToString() : null,
                    };
                    if (string.IsNullOrEmpty(item.type))
                    {
                        ApplyMeta(item, metaToken);
                    }
                    result.Add(item);
                }
            }

            return result;
        }

        /// <summary>把 meta 里的类型声明套到某个变量上。</summary>
        static void ApplyMeta(VariableItem item, JToken metaToken)
        {
            var meta = metaToken as JObject;
            if (meta == null)
            {
                return;
            }

            var entry = meta[item.key] as JObject;
            if (entry == null)
            {
                return;
            }

            if (entry["type"] != null) item.type = entry["type"].ToString();
            if (entry["min"] != null) item.min = entry["min"].ToString();
            if (entry["max"] != null) item.max = entry["max"].ToString();
        }

        /// <summary>把变量列表写回 JSON 的对象格式 {"伤害":"2"}；键为空的行跳过。</summary>
        public static JObject BuildItemsToken(List<VariableItem> items)
        {
            var result = new JObject();
            if (items == null)
            {
                return result;
            }

            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                result[item.key] = item.value ?? string.Empty;
            }
            return result;
        }

        /// <summary>把类型声明写回 JSON；类型/范围全空的项不写，免得文件里塞一堆空对象。</summary>
        public static JObject BuildMetaToken(List<VariableItem> items)
        {
            var result = new JObject();
            if (items == null)
            {
                return result;
            }

            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(item.type)
                    && string.IsNullOrEmpty(item.min)
                    && string.IsNullOrEmpty(item.max))
                {
                    continue;
                }

                var entry = new JObject();
                if (!string.IsNullOrEmpty(item.type)) entry["type"] = item.type;
                if (!string.IsNullOrEmpty(item.min)) entry["min"] = item.min;
                if (!string.IsNullOrEmpty(item.max)) entry["max"] = item.max;
                result[item.key] = entry;
            }
            return result;
        }

        /// <summary>半角/全角花括号都认，{伤害} 和 ｛伤害｝ 等价。</summary>
        static readonly Regex PlaceholderPattern = new Regex(@"[{｛]([^{}｛｝]+)[}｝]", RegexOptions.Compiled);

        /// <summary>按等级取值：先找 名字_Lv{level}，再退回不带后缀的 名字，都没有返回 null。</summary>
        public static string ResolveVariable(List<VariableItem> items, string baseName, int level)
        {
            if (items == null || string.IsNullOrEmpty(baseName))
            {
                return null;
            }

            string levelKey = baseName + "_Lv" + level;
            string fallback = null;
            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                if (item.key == levelKey)
                {
                    return item.value;
                }
                if (item.key == baseName)
                {
                    fallback = item.value;
                }
            }
            return fallback;
        }

        /// <summary>把效果模板里的 {变量名} 换成当前等级的值；取不到就原样留着，预览里一眼能看见。</summary>
        public static string RenderTemplate(string template, List<VariableItem> items, int level)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            return PlaceholderPattern.Replace(template, delegate(Match match)
            {
                string name = match.Groups[1].Value.Trim();
                string value = ResolveVariable(items, name, level);
                return value != null ? value : match.Value;
            });
        }

        /// <summary>模板里引用到的变量名（按出现顺序去重）。</summary>
        public static List<string> CollectPlaceholders(string template)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(template))
            {
                return result;
            }

            foreach (Match match in PlaceholderPattern.Matches(template))
            {
                string name = match.Groups[1].Value.Trim();
                if (name.Length > 0 && !result.Contains(name))
                {
                    result.Add(name);
                }
            }
            return result;
        }

        /// <summary>把变量列表重新拼成 raw 文本，免得 raw 和 items 各说各话。</summary>
        public static string BuildRaw(List<VariableItem> items)
        {
            if (items == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                parts.Add(item.key + "=" + (item.value ?? string.Empty));
            }
            return string.Join("; ", parts.ToArray());
        }

        /// <summary>
        /// 校验一张卡的变量和效果模板。返回逐条提示：
        /// 以 "错误：" 开头的会挡住结算，以 "警告：" 开头的只是提醒。
        ///
        /// 引用扫描范围只有【效果】一个字段：【负面】已取消，代价直接写进效果里，
        /// 所以 {自伤} 这类占位符也要写在【效果】中才算「被引用」。
        /// design_intent 之类的自由文本不参与，那里的花括号可能只是排版。
        /// </summary>
        public static List<string> Validate(CardData card)
        {
            var messages = new List<string>();
            if (card == null)
            {
                return messages;
            }

            List<VariableItem> items = card.variables != null ? card.variables.items : null;

            List<string> referenced = CollectPlaceholders(card.effect);

            // 1. 模板引用了，但没定义（同级也没有带后缀的）
            foreach (string name in referenced)
            {
                if (!HasVariable(items, name) && !HasAnyLevel(items, name))
                {
                    messages.Add("错误：效果引用了 {" + name + "}，但变量里没有定义");
                }
            }

            // 2. 定义了，但模板根本没引用
            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                if (!ReferencedAsBase(referenced, item.key))
                {
                    messages.Add("警告：变量 " + item.key + " 定义了但效果里没有引用");
                }
            }

            // 3. 同一个变量名定义多次
            var counts = new Dictionary<string, int>();
            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                counts[item.key] = (counts.ContainsKey(item.key) ? counts[item.key] : 0) + 1;
            }
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (pair.Value > 1)
                {
                    messages.Add("错误：变量 " + pair.Key + " 定义了 " + pair.Value + " 次");
                }
            }

            // 4. 类型与范围
            foreach (VariableItem item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.key))
                {
                    continue;
                }
                messages.AddRange(ValidateItem(item));
            }

            // 5. 等级没给全：引用了 {伤害}，但只定义了 伤害_Lv1
            foreach (string name in referenced)
            {
                if (HasVariable(items, name))
                {
                    continue;
                }

                var levels = new List<string>();
                for (int level = 1; level <= MaxLevel; level++)
                {
                    if (HasExact(items, name + "_Lv" + level))
                    {
                        levels.Add("Lv" + level);
                    }
                }
                if (levels.Count > 0 && levels.Count < MaxLevel)
                {
                    messages.Add("警告：{" + name + "} 只定义了 " + string.Join("/", levels.ToArray())
                        + "，没定义的等级会原样显示占位符");
                }
            }

            return messages;
        }

        /// <summary>类型 / 范围校验。type 留空表示不校验。</summary>
        static List<string> ValidateItem(VariableItem item)
        {
            var messages = new List<string>();
            string type = item.type != null ? item.type.Trim().ToLowerInvariant() : string.Empty;
            if (type.Length == 0 || type == "text" || type == "string")
            {
                return messages;
            }

            if (type == "int")
            {
                int parsedInt;
                if (!int.TryParse(item.value, out parsedInt))
                {
                    messages.Add("错误：" + item.key + " 声明为 int，但值 \"" + item.value + "\" 不是整数");
                    return messages;
                }
                CheckRange(item, parsedInt, messages);
                return messages;
            }

            if (type == "float")
            {
                float parsedFloat;
                if (!float.TryParse(item.value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedFloat))
                {
                    messages.Add("错误：" + item.key + " 声明为 float，但值 \"" + item.value + "\" 不是数字");
                    return messages;
                }
                CheckRange(item, parsedFloat, messages);
                return messages;
            }

            messages.Add("警告：" + item.key + " 的类型 \"" + item.type + "\" 不认识（只支持 int / float / text）");
            return messages;
        }

        static void CheckRange(VariableItem item, float value, List<string> messages)
        {
            float min;
            float max;
            if (!string.IsNullOrEmpty(item.min)
                && float.TryParse(item.min, NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                && value < min)
            {
                messages.Add("错误：" + item.key + " 的值 " + value + " 小于下限 " + min);
            }
            if (!string.IsNullOrEmpty(item.max)
                && float.TryParse(item.max, NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                && value > max)
            {
                messages.Add("错误：" + item.key + " 的值 " + value + " 大于上限 " + max);
            }
        }

        /// <summary>是否存在同名的变量（不带等级后缀）。</summary>
        static bool HasVariable(List<VariableItem> items, string key)
        {
            return HasExact(items, key);
        }

        static bool HasExact(List<VariableItem> items, string key)
        {
            if (items == null)
            {
                return false;
            }
            foreach (VariableItem item in items)
            {
                if (item != null && item.key == key)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否存在 名字_Lv1 ~ 名字_LvN 中的任意一个。</summary>
        static bool HasAnyLevel(List<VariableItem> items, string baseName)
        {
            for (int level = 1; level <= MaxLevel; level++)
            {
                if (HasExact(items, baseName + "_Lv" + level))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>变量的键是否被模板引用：同名，或者是 {基础名}_LvN 且基础名被引用。</summary>
        static bool ReferencedAsBase(List<string> referenced, string key)
        {
            foreach (string name in referenced)
            {
                if (name == key || key.StartsWith(name + "_Lv", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
