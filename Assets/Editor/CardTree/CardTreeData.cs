using System;
using System.IO;
using UnityEngine;

namespace DungeonCardDesign.EditorTools
{
    // 这里的字段名必须和 exports/*.json 完全一致，JsonUtility 靠名字匹配。
    // JSON 里的 variables.items / adjacency / extra_fields 是动态键对象，
    // JsonUtility 解析不了字典，所以这里不建模，需要时读 raw 字段或 raw_text。

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

    [Serializable]
    public class VariablesData
    {
        public string raw;
        public string[] unparsed;
    }

    [Serializable]
    public class EvolutionData
    {
        public string raw;
        public string[] targets;
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
        public string drawback;
        public string acquisition;
        public EvolutionData evolution;
        public string design_intent;
        public string[] missing_fields;
        public string raw_text;
        public string color;
        public PositionData position;
        public SizeData size;
        public string parent_group;
        public string[] outgoing;
        public string[] incoming;
        public string[] evolution_target_ids;
        public string[] children;

        public bool IsGroup
        {
            get { return type == "group"; }
        }

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
                CanvasExport data = JsonUtility.FromJson<CanvasExport>(json);
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
                return data;
            }
            catch (Exception ex)
            {
                Debug.LogError("[CardTree] 解析失败: " + absolutePath + "\n" + ex);
                return null;
            }
        }
    }
}
