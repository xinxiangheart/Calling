#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""canvas_to_json.py - 把 Obsidian Canvas (.canvas) 导出为结构化 JSON。

Obsidian 的 .canvas 文件本身就是 JSON，结构如下::

    {
      "nodes": [
        {"id": "n1", "type": "text", "x": 0, "y": 0, "width": 250, "height": 60,
         "text": "【卡名】烈焰斩"},
        {"id": "g1", "type": "group", "label": "稀有度", "x": 0, "y": 0,
         "width": 600, "height": 400},
        {"id": "f1", "type": "file", "file": "notes/foo.md"},
        {"id": "l1", "type": "link", "url": "https://example.com"}
      ],
      "edges": [
        {"id": "e1", "fromNode": "n1", "toNode": "n2",
         "fromSide": "right", "toSide": "left", "label": "进化"}
      ]
    }

节点正文使用 ``【字段名】取值`` 的格式书写，字段规范见 ``FIELD_SPEC``::

    【卡名】烈焰斩
    【品级】稀有
    【类别】攻击
    【效果】造成 3 点伤害；自身失去 1 点生命。
    【变量】伤害:3; 自伤:1
    【获取】商店
    【设计意图】前期的核心输出手段。

注意：【负面】与【进化】已取消。【负面】的代价直接写进【效果】，
用 ``{变量}`` 表达数值；【进化】关系改由画布连线表达（见 ``edges``）。
老画布里残留的这两行会被静默忽略，不报错也不出现在导出结果里。

用法::

    python scripts/canvas_to_json.py                        # 导出 canvases/ 下全部 .canvas
    python scripts/canvas_to_json.py canvases/cards.canvas  # 只导出指定文件
    python scripts/canvas_to_json.py --pretty --verbose

退出码: 0 成功 / 1 出错 / 2 有告警且指定了 --strict
"""

from __future__ import annotations

import argparse
import json
import logging
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

SCHEMA_VERSION = "1.0"
LOGGER = logging.getLogger("canvas_to_json")

PROJECT_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_CANVAS_DIR = PROJECT_ROOT / "canvases"
DEFAULT_EXPORT_DIR = PROJECT_ROOT / "exports"

# --------------------------------------------------------------------------
# 字段规范
# --------------------------------------------------------------------------
FIELD_SPEC: Tuple[Tuple[str, str], ...] = (
    ("name", "卡名"),
    ("rarity", "品级"),
    ("category", "类别"),
    ("effect", "效果"),
    ("variables", "变量"),
    ("acquisition", "获取"),
    ("design_intent", "设计意图"),
)
FIELD_KEY_BY_LABEL: Dict[str, str] = {label: key for key, label in FIELD_SPEC}
FIELD_LABEL_BY_KEY: Dict[str, str] = {key: label for key, label in FIELD_SPEC}

# 已取消的字段标记。识别它们只是为了「不报警、不输出」：
# 【负面】合并进【效果】，【进化】改由画布连线表达。
# 老画布里残留的这些行会被静默丢弃，原文仍完整保留在 raw_text 里。
IGNORED_FIELD_LABELS = frozenset(("负面", "进化"))

FIELD_MARKER = re.compile(r"【\s*(?P<label>[^【】\n]+?)\s*】")
VARIABLE_ITEM_SPLIT = re.compile(r"[;；\n]+")
VARIABLE_PAIR_SPLIT = re.compile(r"[:：=]", re.UNICODE)

# 这些写法等价于「没有填」，解析时按空值处理，避免刷出无意义的告警
PLACEHOLDER_VALUES = {"", "无", "无。", "none", "n/a", "na", "-", "--", "—", "空"}


def is_placeholder(value: Optional[str]) -> bool:
    """判断字段值是不是「无」这类占位写法。"""
    if value is None:
        return True
    return value.strip().lower() in PLACEHOLDER_VALUES


class CanvasError(Exception):
    """可在 CLI 层友好展示的错误。"""


# --------------------------------------------------------------------------
# 文本解析
# --------------------------------------------------------------------------
def _at_line_start(text: str, index: int) -> bool:
    """判断 index 位置是不是本行第一个非空白字符。"""
    line_start = text.rfind("\n", 0, index) + 1
    return text[line_start:index].strip() == ""


def _line_number(text: str, index: int) -> int:
    return text.count("\n", 0, index) + 1


def parse_fields(text: str) -> Tuple[Dict[str, Any], Dict[str, str], List[str]]:
    """解析节点正文里的【字段】。返回 (标准字段, 额外字段, 告警列表)。

    只有命中 ``FIELD_SPEC`` 的【标记】才会切出新字段，所以正文里引用别的卡名
    （例如「由【烈焰斩】进化」）不会被误判成字段，会原样留在当前字段值里。
    出现在行首的未知【标记】仍按字段处理并给出告警，便于发现字段名写错。

    【变量】是唯一允许多行出现的字段：多行按出现顺序累积后用 ``;`` 连接，
    再交给 :func:`parse_variables` 统一解析，不会被后面的行覆盖掉。
    单行写法（本身就用 ``;`` 分隔）走的是同一条路径，两种写法结果一致。

    ``IGNORED_FIELD_LABELS`` 里的【标记】（当前是【负面】和【进化】）在行首出现时
    会被整段吃掉、既不报错也不写进结果——它们已从字段规范里取消。
    """
    warnings: List[str] = []
    fields: Dict[str, Any] = {}
    extra: Dict[str, str] = {}
    text = text or ""

    entries: List[Tuple[int, int, str, Optional[str]]] = []
    for marker in FIELD_MARKER.finditer(text):
        label = marker.group("label").strip()
        key = FIELD_KEY_BY_LABEL.get(label)
        if key is None and not _at_line_start(text, marker.start()):
            continue
        entries.append((marker.start(), marker.end(), label, key))

    if not entries:
        warnings.append("节点正文没有任何【字段】标记，只保留了 raw_text")
        return fields, extra, warnings

    seen: Dict[str, int] = {}
    variable_parts: List[str] = []
    variable_seen = False
    for index, (marker_start, value_start, label, key) in enumerate(entries):
        end = entries[index + 1][0] if index + 1 < len(entries) else len(text)
        value = text[value_start:end].strip()
        if key is None:
            if label in IGNORED_FIELD_LABELS:
                # 已取消字段：识别出来只为不报警，值直接丢弃（raw_text 里还能翻到原文）
                continue
            extra[label] = value
            warnings.append(
                "第 %d 行的【%s】不是已知字段，已归入 extra_fields（检查字段名是否写错）"
                % (_line_number(text, marker_start), label)
            )
            continue
        if key == "variables":
            # 多行【变量】：这里只累积，等全部字段扫完再统一拼；
            # 因此不会触发下面的「重复覆盖」告警。
            variable_seen = True
            if value:
                variable_parts.append(value)
            continue
        seen[key] = seen.get(key, 0) + 1
        if seen[key] > 1:
            warnings.append(
                "字段【%s】重复出现，后面的值覆盖了前面的值" % FIELD_LABEL_BY_KEY[key]
            )
        fields[key] = value

    if variable_seen:
        # 用 ; 连接后交给 parse_variables，输出顺序与源文件中的出现顺序一致
        fields["variables"] = "; ".join(variable_parts)

    return fields, extra, warnings


def parse_variables(raw: Optional[str]) -> Dict[str, Any]:
    """把 "伤害:3; 冷却:2" 解析成 {'items': {...}, 'unparsed': [...], 'raw': ...}。"""
    if is_placeholder(raw):
        return {"raw": None, "items": {}, "unparsed": []}
    items: Dict[str, str] = {}
    unparsed: List[str] = []
    for chunk in VARIABLE_ITEM_SPLIT.split(raw):
        chunk = chunk.strip()
        if not chunk:
            continue
        parts = VARIABLE_PAIR_SPLIT.split(chunk, maxsplit=1)
        if len(parts) == 2 and parts[0].strip():
            items[parts[0].strip()] = parts[1].strip()
        else:
            unparsed.append(chunk)
    return {"raw": raw, "items": items, "unparsed": unparsed}


def parse_name_from_unknown_text(text: str) -> Optional[str]:
    """没有【卡名】时，退而求其次用第一行非空文本当名字。"""
    for line in (text or "").splitlines():
        line = line.strip()
        if line:
            return line
    return None


# --------------------------------------------------------------------------
# 模型转换
# --------------------------------------------------------------------------
def _bbox(node: Dict[str, Any]) -> Optional[Tuple[float, float, float, float]]:
    try:
        x = float(node["x"])
        y = float(node["y"])
        w = float(node.get("width", 0))
        h = float(node.get("height", 0))
    except (KeyError, TypeError, ValueError):
        return None
    return (x, y, x + w, y + h)


def resolve_group_hierarchy(
    nodes: Sequence[Dict[str, Any]],
) -> Tuple[Dict[str, Optional[str]], List[Dict[str, Any]]]:
    """按几何包含关系算出每个节点所属的 group，以及嵌套的 group 树。"""
    warnings: List[str] = []
    groups: List[Tuple[str, Tuple[float, float, float, float]]] = []
    for node in nodes:
        if str(node.get("type")) != "group":
            continue
        box = _bbox(node)
        if box is None:
            warnings.append("group %s 缺少有效的几何信息，已跳过" % node.get("id"))
            continue
        groups.append((str(node.get("id")), box))

    def area(box: Tuple[float, float, float, float]) -> float:
        return max(0.0, box[2] - box[0]) * max(0.0, box[3] - box[1])

    def contains(outer: Tuple[float, float, float, float], inner: Tuple[float, float, float, float]) -> bool:
        return (
            inner[0] >= outer[0]
            and inner[1] >= outer[1]
            and inner[2] <= outer[2]
            and inner[3] <= outer[3]
        )

    parent_of: Dict[str, Optional[str]] = {}
    for node in nodes:
        node_id = str(node.get("id"))
        box = _bbox(node)
        if box is None:
            parent_of[node_id] = None
            continue
        candidates = [
            (gid, gbox)
            for gid, gbox in groups
            if gid != node_id and contains(gbox, box)
        ]
        # 选最小的那个容器 = 最近的父级
        candidates.sort(key=lambda item: area(item[1]))
        parent_of[node_id] = candidates[0][0] if candidates else None

    return parent_of, warnings


def build_card(
    node: Dict[str, Any],
    index: int,
    parent_of: Dict[str, Optional[str]],
) -> Tuple[Dict[str, Any], List[str]]:
    """把一个 canvas 节点转成卡片对象。"""
    warnings: List[str] = []
    node_type = str(node.get("type") or "text")
    node_id = str(node.get("id") or "__auto_node_%d" % index)
    if not node.get("id"):
        warnings.append("第 %d 个节点缺少 id，已生成占位 id %s" % (index, node_id))

    text = node.get("text")
    if text is not None and not isinstance(text, str):
        warnings.append("节点 %s 的 text 不是字符串，已按空文本处理" % node_id)
        text = None

    fields: Dict[str, Any] = {}
    extra: Dict[str, str] = {}
    if node_type == "text":
        fields, extra, parse_warnings = parse_fields(text or "")
        warnings.extend("%s: %s" % (node_id, w) for w in parse_warnings)
    else:
        parse_warnings = []

    missing = [FIELD_LABEL_BY_KEY[key] for key, _ in FIELD_SPEC if not fields.get(key) and key != "variables"]
    if node_type == "text" and "name" in fields:
        pass
    elif node_type == "text":
        fallback = parse_name_from_unknown_text(text or "")
        if fallback:
            fields["name"] = fallback
            warnings.append("节点 %s 未填写【卡名】，暂用正文首行代替" % node_id)

    card: Dict[str, Any] = {
        "id": node_id,
        "type": node_type,
        "name": fields.get("name"),
        "rarity": fields.get("rarity"),
        "category": fields.get("category"),
        "effect": fields.get("effect"),
        "variables": parse_variables(fields.get("variables")),
        "acquisition": fields.get("acquisition"),
        "design_intent": fields.get("design_intent"),
        "extra_fields": extra,
        "missing_fields": missing,
        "raw_text": text,
        "color": node.get("color"),
        "position": {"x": node.get("x"), "y": node.get("y")},
        "size": {"width": node.get("width"), "height": node.get("height")},
        "parent_group": parent_of.get(node_id),
        "outgoing": [],
        "incoming": [],
        "children": [],
    }

    if node_type == "file":
        card["file"] = node.get("file")
    elif node_type == "link":
        card["url"] = node.get("url")
    elif node_type == "group":
        card["label"] = node.get("label")
        card["background"] = node.get("background")

    return card, warnings


def build_edge(
    edge: Dict[str, Any],
    index: int,
    known_ids: Iterable[str],
) -> Tuple[Dict[str, Any], List[str]]:
    warnings: List[str] = []
    known = set(known_ids)
    edge_id = str(edge.get("id") or "__auto_edge_%d" % index)
    if not edge.get("id"):
        warnings.append("第 %d 条连线缺少 id，已生成占位 id %s" % (index, edge_id))

    source = edge.get("fromNode")
    target = edge.get("toNode")
    source = str(source) if source is not None else None
    target = str(target) if target is not None else None
    if source is None or target is None:
        warnings.append("连线 %s 缺少 fromNode/toNode，无法建立关系" % edge_id)
    else:
        if source not in known:
            warnings.append("连线 %s 的起点 %s 不存在于 nodes 中" % (edge_id, source))
        if target not in known:
            warnings.append("连线 %s 的终点 %s 不存在于 nodes 中" % (edge_id, target))

    record = {
        "id": edge_id,
        "from": source,
        "to": target,
        "label": edge.get("label"),
        "from_side": edge.get("fromSide"),
        "to_side": edge.get("toSide"),
        "color": edge.get("color"),
    }
    return record, warnings


def build_forest(cards: Sequence[Dict[str, Any]], edges: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """按连线关系生成森林，便于 AI/Unity 直接消费层级结构。"""
    children: Dict[str, List[str]] = {card["id"]: [] for card in cards}
    parents: Dict[str, List[str]] = {card["id"]: [] for card in cards}
    for edge in edges:
        source, target = edge.get("from"), edge.get("to")
        if source in children and target in parents:
            children[source].append(target)
            parents[target].append(source)

    roots = [card["id"] for card in cards if card["type"] != "group" and not parents[card["id"]]]
    ordered_ids = [card["id"] for card in cards]
    visited: set = set()

    def walk(node_id: str, trail: Tuple[str, ...]) -> Dict[str, Any]:
        visited.add(node_id)
        node: Dict[str, Any] = {"id": node_id, "children": []}
        for child_id in children.get(node_id, []):
            if child_id in trail:
                node["cyclic"] = True
                continue
            if child_id in visited:
                node["children"].append({"id": child_id, "children": [], "shared": True})
                continue
            node["children"].append(walk(child_id, trail + (child_id,)))
        return node

    forest = [walk(root, (root,)) for root in roots]
    # 剩下的都是环里或者被孤立的节点，也补进森林，保证不丢数据
    for node_id in ordered_ids:
        if node_id not in visited:
            forest.append(walk(node_id, (node_id,)))
    return forest


# --------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------
def read_canvas(path: Path) -> Dict[str, Any]:
    """读取并校验 .canvas 文件。"""
    if not path.exists():
        raise CanvasError("文件不存在: %s" % path)
    if not path.is_file():
        raise CanvasError("不是文件: %s" % path)

    try:
        raw = path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exc:
        raise CanvasError("文件不是 UTF-8 编码，无法解析: %s (%s)" % (path, exc)) from exc
    except OSError as exc:
        raise CanvasError("读取文件失败: %s (%s)" % (path, exc)) from exc

    if not raw.strip():
        raise CanvasError("文件为空: %s" % path)

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise CanvasError(
            "JSON 解析失败: %s (第 %d 行第 %d 列: %s)" % (path, exc.lineno, exc.colno, exc.msg)
        ) from exc

    if not isinstance(data, dict):
        raise CanvasError("顶层结构不是对象: %s (实际是 %s)" % (path, type(data).__name__))
    for key in ("nodes", "edges"):
        if key in data and not isinstance(data[key], list):
            raise CanvasError("字段 %r 应该是数组: %s" % (key, path))
    data.setdefault("nodes", [])
    data.setdefault("edges", [])
    return data


def convert(canvas_path: Path, canvas_dir: Optional[Path], data: Dict[str, Any]) -> Dict[str, Any]:
    """把 canvas 数据转成导出用的结构化 payload。"""
    warnings: List[str] = []
    nodes = data["nodes"]
    edges = data["edges"]

    parent_of, group_warnings = resolve_group_hierarchy(nodes)
    warnings.extend(group_warnings)

    cards: List[Dict[str, Any]] = []
    seen_ids: Dict[str, int] = {}
    for index, node in enumerate(nodes):
        if not isinstance(node, dict):
            warnings.append("第 %d 个节点不是对象，已跳过" % index)
            continue
        card, card_warnings = build_card(node, index, parent_of)
        warnings.extend(card_warnings)
        if card["id"] in seen_ids:
            seen_ids[card["id"]] += 1
            renamed = "%s__dup%d" % (card["id"], seen_ids[card["id"]])
            warnings.append("节点 id 重复: %s，已重命名为 %s" % (card["id"], renamed))
            card["id"] = renamed
        seen_ids.setdefault(card["id"], 1)
        cards.append(card)

    known_ids = [card["id"] for card in cards]
    edge_records: List[Dict[str, Any]] = []
    for index, edge in enumerate(edges):
        if not isinstance(edge, dict):
            warnings.append("第 %d 条连线不是对象，已跳过" % index)
            continue
        record, edge_warnings = build_edge(edge, index, known_ids)
        warnings.extend(edge_warnings)
        edge_records.append(record)

    by_id = {card["id"]: card for card in cards}
    for edge in edge_records:
        source, target = edge.get("from"), edge.get("to")
        if source in by_id and target in by_id:
            by_id[source]["outgoing"].append(edge["id"])
            by_id[target]["incoming"].append(edge["id"])
            if target not in by_id[source]["children"]:
                by_id[source]["children"].append(target)

    forest = build_forest([card for card in cards if card["type"] != "group"], edge_records)

    source_rel = None
    if canvas_dir is not None:
        try:
            source_rel = canvas_path.resolve().relative_to(canvas_dir.resolve().parent).as_posix()
        except ValueError:
            source_rel = None
    if source_rel is None:
        source_rel = canvas_path.name

    text_cards = [card for card in cards if card["type"] == "text"]
    return {
        "schema_version": SCHEMA_VERSION,
        "generated_at": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
        "source": {
            "canvas": source_rel,
            "filename": canvas_path.name,
            "bytes": canvas_path.stat().st_size if canvas_path.exists() else None,
        },
        "field_spec": [{"key": key, "label": label} for key, label in FIELD_SPEC],
        "stats": {
            "nodes": len(cards),
            "text_nodes": len(text_cards),
            "group_nodes": len([c for c in cards if c["type"] == "group"]),
            "other_nodes": len([c for c in cards if c["type"] not in ("text", "group")]),
            "edges": len(edge_records),
            "warnings": len(warnings),
        },
        "cards": cards,
        "edges": edge_records,
        "adjacency": {
            "out": {card["id"]: card["outgoing"] for card in cards},
            "in": {card["id"]: card["incoming"] for card in cards},
        },
        "forest": forest,
        "warnings": warnings,
    }


def write_json(payload: Dict[str, Any], target: Path, pretty: bool) -> None:
    """原子写入 JSON 文件（UTF-8，不转义中文）。"""
    target.parent.mkdir(parents=True, exist_ok=True)
    if pretty:
        text = json.dumps(payload, ensure_ascii=False, indent=2)
    else:
        text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    temp = target.with_name(target.name + ".tmp")
    try:
        temp.write_text(text + "\n", encoding="utf-8")
        temp.replace(target)
    except OSError as exc:
        raise CanvasError("写入 %s 失败: %s" % (target, exc)) from exc
    finally:
        if temp.exists():
            try:
                temp.unlink()
            except OSError:
                pass


def collect_canvases(targets: Sequence[str], canvas_dir: Path) -> List[Path]:
    """决定这次要导出哪些 .canvas 文件。"""
    if targets:
        found: List[Path] = []
        for item in targets:
            path = Path(item)
            if path.is_dir():
                found.extend(sorted(p for p in path.glob("*.canvas") if p.is_file()))
            elif path.exists():
                found.append(path)
            else:
                raise CanvasError("找不到目标: %s" % item)
        return found

    if not canvas_dir.exists():
        raise CanvasError("画布目录不存在: %s" % canvas_dir)
    canvases = sorted(p for p in canvas_dir.glob("*.canvas") if p.is_file())
    if not canvases:
        raise CanvasError("画布目录里没有 .canvas 文件: %s" % canvas_dir)
    return canvases


def setup_logging(verbose: bool, log_file: Optional[Path]) -> None:
    """配置控制台 + 可选文件日志，Windows 控制台也强制 UTF-8。"""
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass

    LOGGER.handlers.clear()
    LOGGER.setLevel(logging.DEBUG if verbose else logging.INFO)

    formatter = logging.Formatter("[%(asctime)s] %(levelname)-7s %(message)s", "%H:%M:%S")
    console = logging.StreamHandler(stream=sys.stdout)
    console.setLevel(logging.DEBUG if verbose else logging.INFO)
    console.setFormatter(formatter)
    LOGGER.addHandler(console)

    if log_file is not None:
        try:
            log_file.parent.mkdir(parents=True, exist_ok=True)
            file_handler = logging.FileHandler(log_file, encoding="utf-8")
        except OSError as exc:
            raise CanvasError("无法创建日志文件 %s: %s" % (log_file, exc)) from exc
        file_handler.setLevel(logging.DEBUG)
        file_handler.setFormatter(formatter)
        LOGGER.addHandler(file_handler)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="canvas_to_json.py",
        description="把 Obsidian Canvas (.canvas) 导出为结构化 JSON。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="字段规范: " + " ".join("【%s】" % label for _, label in FIELD_SPEC),
    )
    parser.add_argument(
        "targets",
        nargs="*",
        help="要导出的 .canvas 文件或目录；留空则导出 canvases/ 下的全部文件",
    )
    parser.add_argument("-o", "--out-dir", default=None, help="导出目录，默认 exports/")
    parser.add_argument("-c", "--canvas-dir", default=None, help="画布目录，默认 canvases/")
    parser.add_argument("--pretty", action="store_true", help="输出带缩进的可读 JSON")
    parser.add_argument("--stdout", action="store_true", help="把 JSON 打到标准输出，不写文件")
    parser.add_argument("--log-file", default=None, help="额外写入一份日志文件")
    parser.add_argument("--strict", action="store_true", help="出现告警时以退出码 2 结束")
    parser.add_argument("-v", "--verbose", action="store_true", help="输出调试日志")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)

    canvas_dir = Path(args.canvas_dir) if args.canvas_dir else DEFAULT_CANVAS_DIR
    out_dir = Path(args.out_dir) if args.out_dir else DEFAULT_EXPORT_DIR
    log_file = Path(args.log_file) if args.log_file else None

    try:
        setup_logging(args.verbose, log_file)
    except CanvasError as exc:
        print("错误: %s" % exc, file=sys.stderr)
        return 1

    LOGGER.debug("项目根目录: %s", PROJECT_ROOT)
    LOGGER.debug("画布目录: %s", canvas_dir)
    LOGGER.debug("导出目录: %s", out_dir)

    try:
        canvases = collect_canvases(args.targets, canvas_dir)
    except CanvasError as exc:
        LOGGER.error("%s", exc)
        return 1
    LOGGER.info("待处理画布 %d 个", len(canvases))

    index_entries: List[Dict[str, Any]] = []
    total_warnings = 0
    failures = 0

    for canvas_path in canvases:
        try:
            LOGGER.info("解析 %s", canvas_path)
            data = read_canvas(canvas_path)
            payload = convert(canvas_path, canvas_dir, data)
            stats = payload["stats"]
            LOGGER.info(
                "  卡片 %d 张（文本 %d / 分组 %d），连线 %d 条",
                stats["nodes"],
                stats["text_nodes"],
                stats["group_nodes"],
                stats["edges"],
            )
            for warning in payload["warnings"]:
                LOGGER.warning("  %s", warning)
            LOGGER.debug("  结构: %s", json.dumps(payload["adjacency"], ensure_ascii=False))
            total_warnings += stats["warnings"]

            if args.stdout and len(canvases) == 1:
                LOGGER.debug("按 --stdout 要求，仅打印 JSON 不写文件")
                text = json.dumps(payload, ensure_ascii=False, indent=2)
                print(text)
            else:
                target = out_dir / (canvas_path.stem + ".json")
                write_json(payload, target, args.pretty)
                LOGGER.info("  已写出 %s", target)

            index_entries.append(
                {
                    "canvas": payload["source"]["canvas"],
                    "output": canvas_path.stem + ".json",
                    "cards": stats["text_nodes"],
                    "edges": stats["edges"],
                    "warnings": stats["warnings"],
                }
            )
        except CanvasError as exc:
            failures += 1
            LOGGER.error("  %s", exc)
        except Exception as exc:  # 兜底：任何未预料的异常都要有明确日志
            failures += 1
            LOGGER.exception("  未预期的错误: %s", exc)

    if not args.stdout and len(index_entries) > 1:
        index_payload = {
            "schema_version": SCHEMA_VERSION,
            "generated_at": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
            "canvases": index_entries,
        }
        try:
            write_json(index_payload, out_dir / "_index.json", True)
            LOGGER.info("已写出 %s", out_dir / "_index.json")
        except CanvasError as exc:
            failures += 1
            LOGGER.error("%s", exc)

    LOGGER.info(
        "完成: 成功 %d / 失败 %d，告警 %d 条",
        len(index_entries),
        failures,
        total_warnings,
    )
    if failures:
        return 1
    if args.strict and total_warnings:
        LOGGER.error("--strict 已开启且存在告警，退出码 2")
        return 2
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n已中断", file=sys.stderr)
        sys.exit(130)
