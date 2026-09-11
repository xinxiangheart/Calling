# 便携版 Python 环境

本项目使用便携版 Python 3.12.10，不依赖系统安装的 Python。
整个环境自包含在 `tools/python/` 目录中，换机器时按本文档重建即可。

## 为什么用便携版

- 系统级 Python 安装可能被沙箱拦截（MSI 打包的安装器需要写 Package Cache）
- 便携版解压即用，不写系统目录，不需要管理员权限
- 项目自包含，换机器直接复制文件夹就能用

## 重建步骤

### 1. 下载便携版 Python

下载地址：
https://www.python.org/downloads/release/python-31210/

在页面底部找到 **Windows embeddable package (64-bit)**，下载：
`python-3.12.10-embed-amd64.zip`

### 2. 解压到指定路径

解压到：

```
dungeon-card-design/tools/python/
```

解压后目录结构应为：

```
dungeon-card-design/tools/python/
├── python.exe
├── python312.dll
├── python312.zip
├── python312._pth
├── Lib/
│   └── site-packages/
└── ...
```

### 3. 修改 `python312._pth`

用文本编辑器打开 `python312._pth`，内容应该是：

```
python312.zip
.
Lib\site-packages
import site
```

关键：

- `Lib\site-packages` 要显式挂载
- `import site` 要取消注释（去掉行首的 `#`）

### 4. 安装 pip

下载 `get-pip.py`：
https://bootstrap.pypa.io/get-pip.py

放到 `dungeon-card-design/tools/python/` 下，然后运行：

```
dungeon-card-design\tools\python\python.exe get-pip.py
```

安装完成后，验证：

```
dungeon-card-design\tools\python\python.exe -m pip --version
```

应输出类似：

```
pip 26.2.1 from ...\dungeon-card-design\tools\python\Lib\site-packages\pip (python 3.12)
```

### 5. 验证导出脚本可运行

从项目根目录（`Calling/`）执行：

```
dungeon-card-design\tools\python\python.exe dungeon-card-design\scripts\canvas_to_json.py --pretty --verbose
```

应输出无错误，并在 `dungeon-card-design/exports/` 生成 JSON。

## 注意事项

- `tools/python/` 已被 `.gitignore` 忽略，不会进入 git 仓库
- 换机器时，按本文档重新下载解压即可
- 如果 Python 版本升级，同步更新本文档中的版本号和下载地址
