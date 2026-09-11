using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DungeonCardDesign.EditorTools
{
    /// <summary>行动卡树编辑器窗口：读取 exports/*.json 并可视化。</summary>
    public class CardTreeWindow : EditorWindow
    {
        CardTreeGraphView _graphView;
        DropdownField _fileDropdown;
        Toggle _showGroupsToggle;
        Label _statusLabel;
        Label _detailLabel;

        string[] _files = new string[0];
        bool _suppressCallbacks;
        Button _exportButton;
        bool _exporting;

        [MenuItem("Tools/Dungeon Card Design/行动卡树 (Canvas to GraphView)", priority = 0)]
        public static void Open()
        {
            CardTreeWindow window = GetWindow<CardTreeWindow>();
            window.titleContent = new GUIContent("行动卡树");
            window.minSize = new Vector2(760f, 520f);
            window.Show();
        }

        void OnEnable()
        {
            BuildUI();
            RefreshFileList();
        }

        void BuildUI()
        {
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.paddingLeft = 6f;
            toolbar.style.paddingRight = 6f;
            toolbar.style.paddingTop = 4f;
            toolbar.style.paddingBottom = 4f;
            toolbar.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.18f));

            _fileDropdown = new DropdownField("导出文件", new List<string>(), 0);
            _fileDropdown.style.minWidth = 260f;
            _fileDropdown.RegisterValueChangedCallback(evt => ReloadCurrent());
            toolbar.Add(_fileDropdown);

            toolbar.Add(new Button(RefreshFileList) { text = "重新扫描" });

            _showGroupsToggle = new Toggle("显示分组节点") { value = false };
            _showGroupsToggle.RegisterValueChangedCallback(evt => ReloadCurrent());
            toolbar.Add(_showGroupsToggle);

            // 调用项目内的便携版 Python，把 canvases/ 下的 .canvas 导出到 exports/
            _exportButton = new Button(RunExport) { text = "导出 JSON" };
            _exportButton.tooltip =
                "执行 dungeon-card-design/tools/python/python.exe canvas_to_json.py，\n"
                + "把 canvases/ 下的 .canvas 导出为 exports/ 下的 JSON，然后自动重新加载。";
            toolbar.Add(_exportButton);

            toolbar.Add(new Button(RevealExportFolder) { text = "打开导出目录" });

            _statusLabel = new Label(string.Empty);
            _statusLabel.style.marginLeft = 8f;
            _statusLabel.style.color = new StyleColor(new Color(0.65f, 0.70f, 0.80f));
            toolbar.Add(_statusLabel);

            rootVisualElement.Add(toolbar);

            _graphView = new CardTreeGraphView();
            _graphView.OnCardSelected = ShowCardDetail;
            rootVisualElement.Add(_graphView);

            _detailLabel = new Label("点选一张卡，这里显示它的原始文本与缺失字段。");
            _detailLabel.style.whiteSpace = WhiteSpace.Normal;
            _detailLabel.style.fontSize = 11f;
            _detailLabel.style.paddingLeft = 8f;
            _detailLabel.style.paddingRight = 8f;
            _detailLabel.style.paddingTop = 6f;
            _detailLabel.style.paddingBottom = 6f;
            _detailLabel.style.maxHeight = 130f;
            _detailLabel.style.backgroundColor = new StyleColor(new Color(0.14f, 0.14f, 0.16f));
            _detailLabel.style.color = new StyleColor(new Color(0.82f, 0.85f, 0.90f));
            rootVisualElement.Add(_detailLabel);
        }

        void RefreshFileList()
        {
            if (_fileDropdown == null)
            {
                return;
            }

            _files = CardTreeLoader.ListExportFiles();
            var choices = new List<string>();
            foreach (string file in _files)
            {
                choices.Add(Path.GetFileName(file));
            }
            if (choices.Count == 0)
            {
                choices.Add("(exports 目录里还没有 JSON)");
            }

            _suppressCallbacks = true;
            _fileDropdown.choices = choices;
            _fileDropdown.index = 0;
            _suppressCallbacks = false;

            ReloadCurrent();
        }

        void ReloadCurrent()
        {
            if (_suppressCallbacks || _graphView == null)
            {
                return;
            }

            int index = _fileDropdown != null ? _fileDropdown.index : -1;
            if (_files.Length == 0 || index < 0 || index >= _files.Length)
            {
                _graphView.Load(null, false);
                if (_statusLabel != null)
                {
                    _statusLabel.text = "没有可用的导出文件";
                }
                return;
            }

            CanvasExport data = CardTreeLoader.Load(_files[index]);
            bool showGroups = _showGroupsToggle != null && _showGroupsToggle.value;
            _graphView.Load(data, showGroups);

            if (_statusLabel == null)
            {
                return;
            }
            if (data == null || data.stats == null)
            {
                _statusLabel.text = "解析失败，详见 Console";
                return;
            }

            _statusLabel.text = string.Format(
                "卡片 {0} 张 / 连线 {1} 条 / 告警 {2} 条",
                data.stats.text_nodes,
                data.stats.edges,
                data.stats.warnings);
        }

        void ShowCardDetail(CardData card)
        {
            if (card == null || _detailLabel == null)
            {
                return;
            }

            var lines = new List<string>();
            lines.Add(string.Format("{0}   (id: {1})", card.DisplayName, card.id));
            if (card.missing_fields != null && card.missing_fields.Length > 0)
            {
                lines.Add("未填写字段: " + string.Join("、", card.missing_fields));
            }
            if (!string.IsNullOrEmpty(card.raw_text))
            {
                lines.Add(string.Empty);
                lines.Add(card.raw_text);
            }
            _detailLabel.text = string.Join("\n", lines);
        }

        void RevealExportFolder()
        {
            string dir = CardTreeLoader.ExportDirectoryFullPath;
            if (!Directory.Exists(dir))
            {
                EditorUtility.DisplayDialog("行动卡树", "目录还不存在:\n" + dir, "好");
                return;
            }
            EditorUtility.RevealInFinder(dir);
        }

        // ==================================================================
        // 「导出 JSON」相关实现
        //
        // 注意：这里刻意不写 using System.Diagnostics;
        //       因为 UnityEngine.Debug 会和 System.Diagnostics.Debug 重名，
        //       一旦引入这个命名空间，代码里所有的 Debug.Log 都会变成
        //       二义性编译错误。所以进程相关类型一律使用全限定名。
        // ==================================================================

        /// <summary>
        /// 设计目录相对 Unity 项目根目录的位置。
        /// 必须与 CardTreeLoader.DefaultExportDirectory 指向同一个 exports/，
        /// 否则导出完重新扫描时会读不到刚生成的文件。
        /// </summary>
        const string DesignRootRelativePath = "dungeon-card-design";

        /// <summary>Unity 项目根目录，即 Application.dataPath 的上一级（也就是 Assets 的父目录）。</summary>
        static string ProjectRootPath
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>便携版 Python 的完整路径。</summary>
        static string PortablePythonFullPath
        {
            get { return Path.Combine(ProjectRootPath, DesignRootRelativePath, "tools", "python", "python.exe"); }
        }

        /// <summary>导出脚本的完整路径。</summary>
        static string ExporterScriptFullPath
        {
            get { return Path.Combine(ProjectRootPath, DesignRootRelativePath, "scripts", "canvas_to_json.py"); }
        }

        /// <summary>存放 .canvas 的输入目录。</summary>
        static string CanvasDirectoryFullPath
        {
            get { return Path.Combine(ProjectRootPath, DesignRootRelativePath, "canvases"); }
        }

        /// <summary>存放导出 JSON 的输出目录。</summary>
        static string ExportDirectoryFullPath
        {
            get { return Path.Combine(ProjectRootPath, DesignRootRelativePath, "exports"); }
        }

        /// <summary>Python 进程最长等待时间，超时就强制结束，避免编辑器一直卡住。</summary>
        const int ExportTimeoutMilliseconds = 120000;

        /// <summary>点击「导出 JSON」：调用便携版 Python 把 canvases/ 导出到 exports/。</summary>
        void RunExport()
        {
            if (_exporting)
            {
                return;
            }

            string pythonPath = PortablePythonFullPath;
            string scriptPath = ExporterScriptFullPath;
            string canvasDir = CanvasDirectoryFullPath;
            string exportDir = ExportDirectoryFullPath;

            // 前置检查：把缺失的路径明确指出来，而不是等进程启动后报一个含糊的错
            if (!File.Exists(pythonPath))
            {
                Debug.LogError("[行动卡树] 找不到便携版 Python: " + pythonPath);
                EditorUtility.DisplayDialog(
                    "导出失败",
                    "找不到便携版 Python。\n\n"
                    + "请确认便携版 Python 已解压到 dungeon-card-design/tools/python/\n\n"
                    + "期望路径：\n" + pythonPath,
                    "好");
                return;
            }

            if (!File.Exists(scriptPath))
            {
                Debug.LogError("[行动卡树] 找不到导出脚本: " + scriptPath);
                EditorUtility.DisplayDialog("导出失败", "找不到导出脚本：\n" + scriptPath, "好");
                return;
            }

            if (!Directory.Exists(canvasDir))
            {
                Debug.LogError("[行动卡树] 找不到画布目录: " + canvasDir);
                EditorUtility.DisplayDialog("导出失败", "找不到画布目录：\n" + canvasDir, "好");
                return;
            }

            try
            {
                Directory.CreateDirectory(exportDir);
            }
            catch (Exception ex)
            {
                Debug.LogError("[行动卡树] 无法创建导出目录 " + exportDir + "\n" + ex);
                EditorUtility.DisplayDialog(
                    "导出失败",
                    "无法创建导出目录：\n" + exportDir + "\n\n" + ex.Message,
                    "好");
                return;
            }

            // 记住当前选中的文件，导出完尽量停在同一个画布上
            string previouslySelected = CurrentExportFilePath();

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonPath,
                // 脚本参数：脚本路径 -c 输入目录 -o 输出目录 --pretty --verbose
                // 路径可能含空格，所以统一加引号
                Arguments = string.Format(
                    "\"{0}\" -c \"{1}\" -o \"{2}\" --pretty --verbose",
                    scriptPath, canvasDir, exportDir),
                WorkingDirectory = ProjectRootPath,   // 工作目录固定为项目根目录，脚本的相对路径才正确
                UseShellExecute = false,              // 必须为 false，否则无法重定向 stdout/stderr
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,   // 脚本输出中文，按 UTF-8 解码
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,                    // 不弹出控制台黑窗
            };

            _exporting = true;
            if (_exportButton != null)
            {
                _exportButton.SetEnabled(false);
            }

            try
            {
                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = startInfo;

                    // 用事件异步收流，避免 stdout/stderr 缓冲区写满后互相等待（死锁）
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            stdout.AppendLine(args.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            stderr.AppendLine(args.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ExportTimeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                            // 进程可能刚好自己结束了，忽略
                        }

                        Debug.LogError("[行动卡树] 导出超时，已强制结束 Python 进程");
                        EditorUtility.DisplayDialog(
                            "导出失败",
                            "导出超时（超过 " + (ExportTimeoutMilliseconds / 1000) + " 秒），已强制结束 Python 进程。\n\n"
                            + Tail(stdout.ToString(), 1200),
                            "好");
                        return;
                    }

                    // MSDN 要求：带超时的 WaitForExit 返回 true 后再调一次无参版本，
                    // 确保异步的 stdout/stderr 回调已经全部执行完
                    process.WaitForExit();

                    int exitCode = process.ExitCode;
                    string stdoutText = stdout.ToString().TrimEnd();
                    string stderrText = stderr.ToString().TrimEnd();

                    if (!string.IsNullOrEmpty(stdoutText))
                    {
                        Debug.Log("[行动卡树] Python 输出：\n" + stdoutText);
                    }

                    if (exitCode == 0)
                    {
                        if (!string.IsNullOrEmpty(stderrText))
                        {
                            Debug.LogWarning("[行动卡树] Python 警告输出：\n" + stderrText);
                        }

                        Debug.Log("[行动卡树] 导出成功，输出目录：" + exportDir);
                        EditorUtility.DisplayDialog(
                            "导出成功",
                            "已导出到：\n" + exportDir + "\n\n" + Tail(stdoutText, 1200),
                            "好");

                        // 导出成功后重新扫描 exports/ 并刷新图（RefreshFileList 内部会调用 ReloadCurrent）
                        RefreshFileList();
                        SelectExportFile(previouslySelected);
                    }
                    else
                    {
                        string detail = string.IsNullOrEmpty(stderrText) ? stdoutText : stderrText;
                        Debug.LogError("[行动卡树] 导出失败，exit code = " + exitCode + "\n" + detail);
                        EditorUtility.DisplayDialog(
                            "导出失败",
                            "Python 返回非零退出码：" + exitCode + "\n\n" + Tail(detail, 1500),
                            "好");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[行动卡树] 启动 Python 进程失败：" + ex + "\nPython 路径：" + pythonPath);
                EditorUtility.DisplayDialog(
                    "导出失败",
                    "启动 Python 进程时出错：\n" + ex.Message + "\n\nPython 路径：\n" + pythonPath,
                    "好");
            }
            finally
            {
                _exporting = false;
                if (_exportButton != null)
                {
                    _exportButton.SetEnabled(true);
                }
            }
        }

        /// <summary>取当前下拉框选中的导出文件绝对路径，没有选中时返回 null。</summary>
        string CurrentExportFilePath()
        {
            if (_fileDropdown == null || _files == null)
            {
                return null;
            }
            int index = _fileDropdown.index;
            if (index < 0 || index >= _files.Length)
            {
                return null;
            }
            return _files[index];
        }

        /// <summary>重新扫描后，把下拉框选回原来那个导出文件（如果它还在）。</summary>
        void SelectExportFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || _fileDropdown == null || _files == null)
            {
                return;
            }

            for (int i = 0; i < _files.Length; i++)
            {
                if (string.Equals(_files[i], absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    _suppressCallbacks = true;
                    _fileDropdown.index = i;
                    _suppressCallbacks = false;
                    ReloadCurrent();
                    return;
                }
            }
        }

        /// <summary>弹窗里只显示末尾一小段，避免长日志把对话框撑爆。</summary>
        static string Tail(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "（脚本没有输出）";
            }

            text = text.TrimEnd();
            if (text.Length <= maxLength)
            {
                return text;
            }
            return "…（已省略前 " + (text.Length - maxLength) + " 个字符）\n" + text.Substring(text.Length - maxLength);
        }
    }
}
