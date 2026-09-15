# Calling — 项目约定

## AI 出图：一律走 ofoxai

本项目所有 AI 生图统一使用用户级技能 `imagegen-compat`，并**显式固定后端为 `ofoxai` / `qwen/qwen-image-3.0-pro`**。

全局配置 `C:\Users\22589\.codex\imagegen-compat.json` 的默认 provider 是 `zhipu`（智谱 glm-image），ofoxai 在里面只是 `refine_provider`——**不显式指定就会打到智谱**。所以本项目每次生图都必须带上 `--provider ofoxai --model qwen/qwen-image-3.0-pro`。

## 固定出图目录

```text
C:\Users\22589\Documents\GitHub\Calling\Assets\Art\Generated\
```

生成的图必须落在本仓库（Calling）内，**不许写到别的仓库**；回报时不许出现 Another-World 路径。


调用方式（PowerShell）：

```powershell
$py  = "C:\Users\22589\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
$gen = "C:\Users\22589\.codex\skills\imagegen-compat\scripts\gen_image.py"
$out = "C:\Users\22589\Documents\GitHub\Calling\Assets\Art\Generated"
$p   = Get-Content -Raw -Encoding UTF8 "<提示词文件>"

& $py $gen generate --provider ofoxai --model qwen/qwen-image-3.0-pro `
  --size <WxH> --prompt $p --name <slug> --out-dir $out
```

- 提示词先写成文件，再用 `Get-Content -Raw -Encoding UTF8` 读入；传 `--prompt` 时不要额外加引号。
- **必须显式带 `--out-dir $out`（本仓库路径）**：全局配置的 `default_out_dir` 指向 `Another-World\Assets\_Game\Art\Sprites\Generated`，漏掉就会把图写进另一个仓库。
- 出图后回报：实际落盘路径 + 尺寸，并确认路径前缀是 `C:\Users\22589\Documents\GitHub\Calling\`。
- 提示词取 `美术风格规范.md` 第六节；否定句必须写进正文（ofoxai 会静默忽略 `--negative`）。
- 全局配置里带 `proxy: http://127.0.0.1:10090`；该代理没起时会连不上，需要时用 `--proxy` 覆盖。
- 联网调用需要在沙箱外执行（提权）。
- 出图仍按 `美术风格规范.md` 的约定**按需手动执行**，本文件不自动触发生成。
