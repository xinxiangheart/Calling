using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>
    /// 把 Noto Sans SC 源字体重建成 TMP 的「动态」字体资源。
    ///
    /// 为什么要重建：从 Another-World 搬过来的 NotoSansSC SDF.asset 是静态字体，
    /// 字形表被烤死在资源里，只有 3952 个字形 —— 设计里用到的「湮」「孢」等字都没有，
    /// 而且文件 33.6 MB。动态字体只存一个空图集加一个源字体引用，缺哪个字运行时
    /// 现从 .otf 取哪个字，能覆盖全部两万多个汉字，资源本身只有几十 KB。
    ///
    /// 这是编辑器侧的一次性构建工具，不参与运行时。
    /// </summary>
    public static class TmpFontBuilder
    {
        // 源字体（OFL 授权，可以随项目分发）
        const string SourceFontPath = "Assets/TextMesh Pro/Fonts/NotoSansSC-Regular.otf";

        // 产物
        const string DynamicFontAssetPath =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/NotoSansSC Dynamic SDF.asset";

        // 从 Another-World 搬来的旧静态字体，重建成功后可删
        const string OldStaticFontAssetPath =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/NotoSansSC SDF.asset";

        const string LiberationFontAssetPath =
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        const string DistanceFieldShaderName = "TextMeshPro/Distance Field";

        // 采样参数取 TMP 自带 Font Asset Creator 的默认值。
        // 图集初始是 0x0，运行时按需长到 AtlasSize x AtlasSize，所以产物很小。
        const int SamplingPointSize = 90;
        const int AtlasPadding = 9;
        const int AtlasSize = 1024;

        // 回归自检样本：这几个字旧静态字体里没有，重建后必须全部命中
        const string RegressionSample = "孢弈渲湮篝耦→×·…";

        [MenuItem("Tools/Dungeon Card Design/重建中文字体 (Dynamic)", priority = 30)]
        public static void Rebuild()
        {
            // 播放模式下改资源容易和运行中的 TMP 打架，直接拦住
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("重建中文字体", "先退出播放模式再重建字体。", "好");
                return;
            }

            Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (sourceFont == null)
            {
                EditorUtility.DisplayDialog(
                    "找不到源字体",
                    "缺少源字体：\n" + SourceFontPath +
                    "\n\n完整路径：\n" + Path.GetFullPath(SourceFontPath),
                    "好");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DynamicFontAssetPath) != null &&
                !EditorUtility.DisplayDialog(
                    "已存在同名资源",
                    DynamicFontAssetPath + "\n\n已存在，重新生成会覆盖它。继续吗？",
                    "覆盖重建", "取消"))
            {
                return;
            }

            // 关键一步：Dynamic 模式下 CreateFontAsset 会把源字体引用一起存进资源，
            // 打包时 Unity 才知道要把 .otf 带上，运行时的字形才烤得出来。
            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFont, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true);

            if (fontAsset == null)
            {
                // CreateFontAsset 只在读不到字形时返回 null，最常见是导入设置关了 Include Font Data
                EditorUtility.DisplayDialog(
                    "字体加载失败",
                    "读不到字形。请在 Project 面板里选中：\n" + SourceFontPath +
                    "\n然后确认 Inspector 里「Include Font Data」是勾上的。",
                    "好");
                return;
            }

            fontAsset.name = "NotoSansSC Dynamic SDF";
            ApplyDistanceFieldMaterial(fontAsset);
            SaveAsAsset(fontAsset);
            bool settingsUpdated = PointTmpSettingsAt(fontAsset);
            Report(settingsUpdated, CheckCoverage(sourceFont));
        }

        /// <summary>
        /// TMP 的 CreateFontAsset 默认给的是 Mobile 版着色器，功能少一截。
        /// 这里换成完整的 Distance Field 着色器，并补齐缩放比率 ——
        /// 不补的话 _ScaleRatioA/B/C 还是旧值，字会发虚。
        /// </summary>
        static void ApplyDistanceFieldMaterial(TMP_FontAsset fontAsset)
        {
            Shader shader = Shader.Find(DistanceFieldShaderName);
            Material material = fontAsset.material;
            if (shader == null || material == null)
            {
                Debug.LogWarning("[中文字体] 找不到 " + DistanceFieldShaderName + "，沿用 TMP 默认材质。");
                return;
            }

            ShaderUtilities.GetShaderPropertyIDs();
            material.shader = shader;
            material.SetFloat(ShaderUtilities.ID_TextureWidth, AtlasSize);
            material.SetFloat(ShaderUtilities.ID_TextureHeight, AtlasSize);
            material.SetFloat(ShaderUtilities.ID_GradientScale, AtlasPadding + 1);
            material.SetFloat(ShaderUtilities.ID_WeightNormal, fontAsset.normalStyle);
            material.SetFloat(ShaderUtilities.ID_WeightBold, fontAsset.boldStyle);
            ShaderUtilities.UpdateShaderRatios(material);
            material.name = fontAsset.name + " Material";
        }

        /// <summary>
        /// 主资源必须先用 CreateAsset 落盘，图集和材质再作为子资源挂进同一个 .asset；
        /// 反过来的话它们是游离对象，存盘后引用会全丢。
        /// </summary>
        static void SaveAsAsset(TMP_FontAsset fontAsset)
        {
            AssetDatabase.DeleteAsset(DynamicFontAssetPath);
            AssetDatabase.CreateAsset(fontAsset, DynamicFontAssetPath);

            if (fontAsset.atlasTextures != null && fontAsset.atlasTextures.Length > 0 &&
                fontAsset.atlasTextures[0] != null)
            {
                fontAsset.atlasTextures[0].name = fontAsset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
            }

            if (fontAsset.material != null)
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// TMP Settings 的字段是私有的，只能走 SerializedObject。
        /// 默认字体和新字体都塞进回退列表：默认管新建的文本，
        /// 回退管别人硬编码了 LiberationSans 的文本 —— 那种文本里的中文只能靠回退找到字形。
        /// </summary>
        static bool PointTmpSettingsAt(TMP_FontAsset fontAsset)
        {
            TMP_Settings settings = AssetDatabase.LoadAssetAtPath<TMP_Settings>(TmpSettingsPath);
            if (settings == null)
                return false;

            TMP_FontAsset liberation = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(LiberationFontAssetPath);

            SerializedObject so = new SerializedObject(settings);
            so.FindProperty("m_defaultFontAsset").objectReferenceValue = fontAsset;

            SerializedProperty fallback = so.FindProperty("m_fallbackFontAssets");
            fallback.arraySize = liberation != null ? 2 : 1;
            fallback.GetArrayElementAtIndex(0).objectReferenceValue = fontAsset;
            if (liberation != null)
                fallback.GetArrayElementAtIndex(1).objectReferenceValue = liberation;

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>
        /// 直接用 FontEngine 问源字体有没有这些码点。
        /// 故意不走 fontAsset.HasCharacter —— 那会顺手把字形烤进图集，
        /// 产物立刻从几十 KB 涨到 1 MB 以上。
        /// </summary>
        static string CheckCoverage(Font sourceFont)
        {
            FontEngine.InitializeFontEngine();
            if (FontEngine.LoadFontFace(sourceFont, SamplingPointSize) != FontEngineError.Success)
                return "（源字体加载失败）";

            List<char> missing = new List<char>();
            foreach (char c in RegressionSample)
            {
                if (!FontEngine.TryGetGlyphIndex(c, out uint glyphIndex) || glyphIndex == 0)
                    missing.Add(c);
            }

            return missing.Count == 0 ? string.Empty : new string(missing.ToArray());
        }

        static void Report(bool settingsUpdated, string missing)
        {
            long assetSize = new FileInfo(Path.GetFullPath(DynamicFontAssetPath)).Length;
            long sourceSize = new FileInfo(Path.GetFullPath(SourceFontPath)).Length;

            string text =
                "产物：" + DynamicFontAssetPath + "\n" +
                "大小：" + (assetSize / 1024f).ToString("F1") + " KB（图集初始 0x0，按需生长）\n" +
                "源字体：" + (sourceSize / 1024f / 1024f).ToString("F1") + " MB，会随构建一起打包\n" +
                "采样：" + SamplingPointSize + "pt / padding " + AtlasPadding + " / 图集 " + AtlasSize + "\n\n" +
                "字形自检 " + RegressionSample + "：" +
                (missing.Length == 0 ? "全部命中" : "仍缺 " + missing) + "\n" +
                (settingsUpdated
                    ? "TMP Settings 已指向新字体。"
                    : "警告：TMP Settings 没改成功，请手动指定 Default Font Asset。");

            Debug.Log("[中文字体] " + text.Replace("\n", "   "));

            if (!File.Exists(Path.GetFullPath(OldStaticFontAssetPath)))
            {
                EditorUtility.DisplayDialog("重建完成", text, "好");
                return;
            }

            if (EditorUtility.DisplayDialog(
                    "重建完成",
                    text + "\n\n旧的静态字体还在：\n" + OldStaticFontAssetPath +
                    "\n（33.6 MB，占仓库体积）\n\n删掉它吗？",
                    "删除旧字体", "先留着"))
            {
                AssetDatabase.DeleteAsset(OldStaticFontAssetPath);
                AssetDatabase.Refresh();
                Debug.Log("[中文字体] 已删除旧静态字体：" + OldStaticFontAssetPath);
            }
        }
    }
}
