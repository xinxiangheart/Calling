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
# 运行时量注册表（契约见 schema/变量与公式规范.md）
DEFAULT_RUNTIME_VARS = PROJECT_ROOT / "schema" / "runtime_vars.json"

# --------------------------------------------------------------------------
# 字段规范
# --------------------------------------------------------------------------
FIELD_SPEC: Tuple[Tuple[str, str], ...] = (
    ("name", "卡名"),
    ("rarity", "品级"),
    ("category", "类别"),
    ("effect", "效果"),
    ("variables", "变量"),
    ("keywords", "词条"),
    ("acquisition", "获取"),
    ("design_intent", "设计意图"),
)
FIELD_KEY_BY_LABEL: Dict[str, str] = {label: key for key, label in FIELD_SPEC}
FIELD_LABEL_BY_KEY: Dict[str, str] = {key: label for key, label in FIELD_SPEC}

# 允许多行出现的字段：多行按出现顺序累积，最后用 ``;`` 连接再统一解析，
# 所以不会被后面的行覆盖掉。单行写法本身就用 ``;`` 分隔，走的是同一条路径。
MULTI_LINE_FIELD_KEYS = frozenset(("variables", "keywords"))

# 可以不填的字段：不算进 ``missing_fields``。
# 【变量】和【词条】只有部分卡牌才有，缺了不该被标成「字段没填完」。
OPTIONAL_FIELD_KEYS = frozenset(("variables", "keywords"))

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

    ``MULTI_LINE_FIELD_KEYS`` 里的字段（当前是【变量】和【词条】）允许多行出现：
    多行按出现顺序累积后用 ``;`` 连接，再交给 :func:`parse_variables` /
    :func:`parse_keywords` 统一解析，不会被后面的行覆盖掉。
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
    multi_parts: Dict[str, List[str]] = {}
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
        if key in MULTI_LINE_FIELD_KEYS:
            # 多行字段（【变量】【词条】）：这里只累积，等全部字段扫完再统一拼；
            # 因此不会触发下面的「重复覆盖」告警。
            multi_parts.setdefault(key, []).append(value)
            continue
        seen[key] = seen.get(key, 0) + 1
        if seen[key] > 1:
            warnings.append(
                "字段【%s】重复出现，后面的值覆盖了前面的值" % FIELD_LABEL_BY_KEY[key]
            )
        fields[key] = value

    for key, parts in multi_parts.items():
        # 用 ; 连接后统一解析，输出顺序与源文件中的出现顺序一致
        fields[key] = "; ".join(part for part in parts if part)

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


def parse_keywords(raw: Optional[str]) -> List[str]:
    """把 "顽固; 湮灭" 解析成 ["顽固", "湮灭"]。

    按 ``;``（含全角）和换行切开，逐项去空白；重复的只保留第一次出现的那个，
    所以结果既是去重的，又保持了源文件里的顺序。
    "裂变2" 这种带数字的词条原样保留整个字符串。

    这里不校验词条名是否在合法列表里——词条语义由运行时战斗代码决定，
    工具侧只负责存字符串，新加词条不该被工具拦住。
    """
    if is_placeholder(raw):
        return []
    result: List[str] = []
    seen = set()
    for chunk in VARIABLE_ITEM_SPLIT.split(raw):
        name = chunk.strip()
        if not name or name in seen:
            continue
        seen.add(name)
        result.append(name)
    return result


# --------------------------------------------------------------------------
# 算式与运行时量
#
# 契约见 schema/变量与公式规范.md。核心约定三条：
#   1. 乘号只认 *。× 属于显示层，写进数据一律报错并提示改成 *
#   2. 花括号里只允许写一个变量名，不允许写算式
#   3. 算式只写在【变量】的值里
# --------------------------------------------------------------------------

# 写错位置的运算符 → 正确写法。给策划的报错里直接带出建议。
MISPLACED_OPERATOR_HINTS = {"×": "*", "✕": "*", "✖": "*", "÷": "/", "／": "/"}

# 值里出现这些字符，就说明它是算式而不是字面量。
# × ÷ 这些「长得像运算符」的字符也要算进来：它们不是合法运算符，
# 但必须让值被判定成算式，才能报出「请改用 *」而不是被当字面量静默放过。
FORMULA_MARK_RE = re.compile(r"[$+\-*/()×÷✕✖／]|[^\W\d]", re.UNICODE)

# 写得像乘号但不是乘号的单个字母。未定义时给一条专门的提示。
LATIN_MULTIPLY_LOOKALIKES = frozenset(("x", "X", "ｘ", "Ｘ"))

# 被引号包起来的值强制当字面量，引号本身会被剥掉
QUOTED_VALUE_RE = re.compile(r"^(?P<quote>[\"'])(?P<body>.*)(?P=quote)$", re.DOTALL)

# 效果文本里的占位符：半角 {} 和全角 ｛｝ 都认
PLACEHOLDER_RE = re.compile(r"[{｛]([^{}｛｝]+)[}｝]")

# 花括号里允许的内容：一个裸变量名
BARE_NAME_RE = re.compile(r"^[^\W\d]\w*$", re.UNICODE)

# 变量名的等级后缀：伤害_Lv1 → 基础名「伤害」
LEVEL_SUFFIX_RE = re.compile(r"^(?P<base>.+)_Lv(?P<level>\d+)$")

# 算式记号。顺序有讲究：reference 必须在 name 前面，否则 $ 会被当成普通字符。
EXPR_TOKEN_RE = re.compile(
    r"""
    \s*
    (?:
        (?P<number>\d+(?:\.\d+)?)
      | (?P<reference>\$[^\W\d]\w*(?:\.[^\W\d]\w*)?)
      | (?P<name>[^\W\d]\w*)
      | (?P<op>[+\-*/()])
      | (?P<bad>.)
    )
    """,
    re.VERBOSE | re.UNICODE,
)


class ExprError(Exception):
    """算式写错了。消息是给策划看的，尽量带着怎么改。"""


class RuntimeRegistry:
    """``schema/runtime_vars.json`` 的只读视图。

    文件缺失或损坏时**降级成空表**而不是让导出失败：这样导出照常跑完，
    所有 ``$`` 引用会集中报「注册表里没有这个量」，比整个脚本报错更容易定位。
    """

    def __init__(self) -> None:
        # 来源名 -> {量名: 量定义}
        self.sources: Dict[str, Dict[str, Dict[str, Any]]] = {}
        # 量名 -> 定义了它的来源列表（用于「省略来源」时的唯一性判断）
        self.by_name: Dict[str, List[str]] = {}
        self.default_source: Optional[str] = None
        self.loaded_from: Optional[str] = None

    @classmethod
    def load(cls, path: Any) -> Tuple["RuntimeRegistry", List[str]]:
        registry = cls()
        warnings: List[str] = []
        candidate = Path(path)

        if not candidate.is_file():
            warnings.append(
                "运行时量注册表不存在（%s），所有 $ 引用都会报未定义" % candidate
            )
            return registry, warnings

        try:
            data = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            warnings.append("运行时量注册表解析失败（%s）：%s" % (candidate, exc))
            return registry, warnings

        registry.loaded_from = str(candidate)
        registry.default_source = data.get("default_source")
        for source in data.get("sources") or []:
            source_name = source.get("name")
            if not source_name:
                continue
            entry: Dict[str, Dict[str, Any]] = {}
            for item in source.get("vars") or []:
                item_name = item.get("name")
                if not item_name:
                    continue
                entry[item_name] = item
                registry.by_name.setdefault(item_name, []).append(source_name)
            registry.sources[source_name] = entry
        return registry, warnings

    def has(self, source: str, name: str) -> bool:
        return name in self.sources.get(source, {})

    def find_sources(self, name: str) -> List[str]:
        """哪些来源里定义了这个量。用来判断「省略来源」会不会有歧义。"""
        return list(self.by_name.get(name, []))

    def meta(self, source: str, name: str) -> Dict[str, Any]:
        return self.sources.get(source, {}).get(name, {})

    def type_of(self, source: str, name: str) -> Optional[str]:
        return self.meta(source, name).get("type")


def analyze_expression(text: str) -> Tuple[List[str], List[Tuple[Optional[str], str]]]:
    """校验一个算式并收集它引用的名字，返回 (本卡变量名, 运行时量引用)。

    这里只做**语法校验和依赖收集**，不求值——值要到运行时才有。
    写错了抛 :class:`ExprError`，消息直接给策划看。
    """
    parser = _ExpressionParser(text)
    parser.parse()
    return parser.names, parser.runtime_refs


def split_reference(token: str) -> Tuple[Optional[str], str]:
    """把 ``$player.力量`` 拆成 ``("player", "力量")``；``$力量`` 拆成 ``(None, "力量")``。"""
    body = token[1:]
    if "." in body:
        source, name = body.split(".", 1)
        return source, name
    return None, body


class _ExpressionParser:
    """递归下降解析器：只校验语法 + 收集引用，不求值。

    文法见契约文档 §2.5::

        expression := term (('+' | '-') term)*
        term       := factor (('*' | '/') factor)*
        factor     := '-' factor | primary
        primary    := number | '$' reference | name | '(' expression ')'
        reference  := [source '.'] name
    """

    def __init__(self, text: str) -> None:
        self.text = text or ""
        self.tokens = tokenize_expression(self.text)
        self.pos = 0
        # 引用到的本卡变量名（不带 $）
        self.names: List[str] = []
        # 引用到的运行时量：(来源 或 None, 名字)
        self.runtime_refs: List[Tuple[Optional[str], str]] = []

    # ---- 记号流 --------------------------------------------------------
    def _peek(self) -> Optional[Tuple[str, str]]:
        return self.tokens[self.pos] if self.pos < len(self.tokens) else None

    def _accept_op(self, *operators: str) -> bool:
        token = self._peek()
        if token is not None and token[0] == "op" and token[1] in operators:
            self.pos += 1
            return True
        return False

    # ---- 文法 ----------------------------------------------------------
    def parse(self) -> None:
        if not self.tokens:
            raise ExprError("算式是空的")
        self._expression()
        leftover = self._peek()
        if leftover is not None:
            raise ExprError("算式末尾有多余内容「%s」" % leftover[1])

    def _expression(self) -> None:
        self._term()
        while self._accept_op("+", "-"):
            self._term()

    def _term(self) -> None:
        self._factor()
        while self._accept_op("*", "/"):
            self._factor()

    def _factor(self) -> None:
        if self._accept_op("-"):
            self._factor()
            return
        self._primary()

    def _primary(self) -> None:
        token = self._peek()
        if token is None:
            raise ExprError("算式在结尾处断了，少了一个数值或变量名")

        kind, value = token
        if kind == "number":
            self.pos += 1
            return
        if kind == "op" and value == "(":
            self.pos += 1
            self._expression()
            if not self._accept_op(")"):
                raise ExprError("括号没有闭合")
            return
        if kind == "op":
            raise ExprError("这里不该出现运算符「%s」" % value)
        if kind == "name":
            self.pos += 1
            self.names.append(value)
            return
        if kind == "reference":
            self.pos += 1
            self.runtime_refs.append(split_reference(value))
            return
        raise ExprError("无法识别的记号「%s」" % value)


def tokenize_expression(text: str) -> List[Tuple[str, str]]:
    """把算式切成记号，返回 ``[(kind, value)]``，kind ∈ number/reference/name/op。

    遇到非法字符抛 :class:`ExprError`；``×`` ``÷`` 会给出「请改用 * /」的提示。
    """
    tokens: List[Tuple[str, str]] = []
    pos = 0
    length = len(text or "")
    while pos < length:
        match = EXPR_TOKEN_RE.match(text, pos)
        if match is None or match.end() == pos:
            break
        pos = match.end()

        bad = match.group("bad")
        if bad is not None:
            hint = MISPLACED_OPERATOR_HINTS.get(bad)
            if hint:
                raise ExprError("「%s」不是合法运算符，乘号请写 *，除号请写 /" % bad)
            raise ExprError("算式里有无法识别的字符「%s」" % bad)

        for kind in ("number", "reference", "name", "op"):
            value = match.group(kind)
            if value is not None:
                tokens.append((kind, value))
                break
    return tokens


MAX_LEVEL = 3


def classify_value(raw: Optional[str]) -> Tuple[str, str]:
    """判定变量值的形态，返回 ``(kind, value)``，kind ∈ ``literal`` / ``formula``。

    规则见契约文档 §2.1，按顺序判定：
    占位写法 → 字面量；引号包起来 → 强制字面量；含 ``$``/运算符/裸标识符 → 算式。
    """
    text = (raw or "").strip()
    if text.lower() in PLACEHOLDER_VALUES:
        return "literal", text

    quoted = QUOTED_VALUE_RE.match(text)
    if quoted:
        return "literal", quoted.group("body")

    # 值里夹着空白（如 "2 3"）不是正经字面量，判成算式交给校验报错；
    # 否则它没有任何算式记号，会被当字面量静默放过。
    if re.search(r"\S\s+\S", text):
        return "formula", text

    if FORMULA_MARK_RE.search(text):
        return "formula", text
    return "literal", text


def _record(collection: List[Any], value: Any) -> None:
    """按出现顺序去重地追加。"""
    if value not in collection:
        collection.append(value)


def _pick_level(entry: Dict[str, Any], level: int) -> Optional[Dict[str, Any]]:
    """按等级取值：先找该等级的定义，再回退到不带后缀的定义。"""
    return entry["levels"].get(str(level)) or entry["levels"].get("base")


def _levels_map(entry: Dict[str, Any]) -> Dict[str, str]:
    """每个等级最终取到的字面量/算式文本，取不到的等级不出现在结果里。"""
    result: Dict[str, str] = {}
    for level in range(1, MAX_LEVEL + 1):
        picked = _pick_level(entry, level)
        if picked is not None:
            result[str(level)] = picked["value"]
    return result


def _group_variables(items: Dict[str, str], messages: List[str]) -> Dict[str, Dict[str, Any]]:
    """把 ``{"伤害_Lv1": "2", "伤害": "5"}`` 按基础名归并成一张表。

    归并是必要的：``伤害`` 和 ``伤害_Lv1`` 是同一个逻辑变量的不同等级，
    校验依赖关系时必须当成一个节点，否则循环引用检测会漏。
    """
    entries: Dict[str, Dict[str, Any]] = {}
    for key, raw_value in items.items():
        match = LEVEL_SUFFIX_RE.match(key)
        base = match.group("base") if match else key
        level = match.group("level") if match else None

        if match is not None:
            level_number = int(match.group("level"))
            if level_number < 1 or level_number > MAX_LEVEL:
                messages.append(
                    "警告：变量 %s 的等级超出了 Lv.1~Lv.%d 的范围，该等级不会被使用"
                    % (key, MAX_LEVEL)
                )
            level = str(level_number)

        entry = entries.get(base)
        if entry is None:
            entry = {"key": base, "levels": {}, "kinds": [], "refs": [], "runtime_refs": []}
            entries[base] = entry

        kind, value = classify_value(raw_value)
        entry["levels"][level or "base"] = {"kind": kind, "value": value, "source_key": key}

    return entries


def _check_bare_name(
    name: str,
    card_bases: Dict[str, Dict[str, Any]],
    registry: RuntimeRegistry,
    entry: Dict[str, Any],
    source_key: str,
    messages: List[str],
) -> None:
    """校验算式里的裸名字：本卡变量优先，其次运行时量注册表。"""
    if name in card_bases:
        _record(entry["refs"], name)
        return

    sources = registry.find_sources(name)
    if len(sources) == 1:
        # 省略 $ 来源的写法，等价于 $<唯一来源>.<name>
        _record(entry["runtime_refs"], (sources[0], name))
        return
    if len(sources) > 1:
        messages.append(
            "错误：%s 里的「%s」在多个来源里都有（%s），请写成 $来源.%s"
            % (source_key, name, "、".join(sources), name)
        )
        return
    if name in LATIN_MULTIPLY_LOOKALIKES:
        messages.append(
            "错误：%s 里的「%s」不是乘号，写乘法请用 *" % (source_key, name)
        )
        return
    messages.append(
        "错误：%s 里的「%s」既不是本卡变量，也不在运行时量注册表里（想写文字请加引号）"
        % (source_key, name)
    )


def _check_reference(
    source: Optional[str],
    name: str,
    registry: RuntimeRegistry,
    entry: Dict[str, Any],
    source_key: str,
    messages: List[str],
) -> None:
    """校验 $来源.名字 形式的引用。"""
    if source is None:
        sources = registry.find_sources(name)
        if len(sources) == 1:
            _record(entry["runtime_refs"], (sources[0], name))
            return
        if len(sources) > 1:
            messages.append(
                "错误：%s 里的 $%s 有歧义（%s 里都有这个名字），请写成 $来源.%s"
                % (source_key, name, "、".join(sources), name)
            )
            return
        messages.append("错误：%s 里的 $%s 不在运行时量注册表里" % (source_key, name))
        return

    if registry.has(source, name):
        _record(entry["runtime_refs"], (source, name))
        return
    if source in registry.sources:
        messages.append(
            "错误：%s 里的 $%s.%s —— 来源 %s 里没有「%s」这个量"
            % (source_key, source, name, source, name)
        )
        return
    messages.append(
        "错误：%s 里的 $%s.%s —— 注册表里没有来源 %s" % (source_key, source, name, source)
    )


def _find_lookalike_multiply(
    text: str,
    entries: Dict[str, Dict[str, Any]],
    registry: RuntimeRegistry,
) -> List[str]:
    """挑出算式里被当成乘号写的 x/X。

    只有在它既不是本卡变量、也不在运行时量注册表里时才算写错；
    真有个变量叫 x 的话不该误报。文法已经报错的前提下才会调用它，
    所以这里只做「谁看起来像乘号」的判断。
    """
    try:
        tokens = tokenize_expression(text)
    except ExprError:
        return []

    found: List[str] = []
    for kind, value in tokens:
        if kind != "name" or value not in LATIN_MULTIPLY_LOOKALIKES:
            continue
        if value in entries or registry.find_sources(value):
            continue
        _record(found, value)
    return found


def _analyze_variable_entries(
    entries: Dict[str, Dict[str, Any]],
    registry: RuntimeRegistry,
    messages: List[str],
) -> None:
    """逐条校验变量的算式，并把引用的名字收集到 entry 上。"""
    for entry in entries.values():
        for level_key, item in entry["levels"].items():
            _record(entry["kinds"], item["kind"])
            if item["kind"] != "formula":
                continue
            try:
                names, runtime_refs = analyze_expression(item["value"])
            except ExprError as exc:
                # 「2 X 3」这类假乘号会在文法层先撞成语法错，
                # 单独挑出来报「不是乘号」，别让策划猜那个 X 到底错在哪。
                lookalikes = _find_lookalike_multiply(
                    item["value"], entries, registry
                )
                if lookalikes:
                    messages.append(
                        "错误：变量 %s 里的「%s」不是乘号，写乘法请用 *"
                        % (item["source_key"], lookalikes[0])
                    )
                else:
                    messages.append(
                        "错误：变量 %s 的算式写错了（%s）" % (item["source_key"], exc)
                    )
                continue
            for name in names:
                _check_bare_name(name, entries, registry, entry, item["source_key"], messages)
            for source, name in runtime_refs:
                _check_reference(source, name, registry, entry, item["source_key"], messages)


def _find_cycles(entries: Dict[str, Dict[str, Any]]) -> List[str]:
    """在「本卡变量互相引用」的图上找环：A 用 B、B 又用 A 是算不出来的。"""
    messages: List[str] = []
    state: Dict[str, int] = {}
    stack: List[str] = []

    def visit(node: str) -> None:
        state[node] = 1
        stack.append(node)
        for dependency in entries.get(node, {}).get("refs", []):
            if dependency not in entries:
                continue
            status = state.get(dependency, 0)
            if status == 1:
                loop = stack[stack.index(dependency):] + [dependency]
                messages.append("错误：变量循环引用：%s" % " → ".join(loop))
            elif status == 0:
                visit(dependency)
        stack.pop()
        state[node] = 2

    for name in entries:
        if state.get(name, 0) == 0:
            visit(name)
    return messages


def _resolve_placeholder(
    name: str,
    entries: Dict[str, Dict[str, Any]],
    registry: RuntimeRegistry,
    messages: List[str],
) -> Dict[str, Any]:
    """把一个 {名字} 解析成分段记录。"""
    if name in entries:
        return {"kind": "card", "name": name, "levels": _levels_map(entries[name])}

    sources = registry.find_sources(name)
    if len(sources) == 1:
        source = sources[0]
        meta = registry.meta(source, name)
        return {
            "kind": "runtime",
            "source": source,
            "name": name,
            "type": meta.get("type"),
            "sample": meta.get("sample"),
        }
    if len(sources) > 1:
        messages.append(
            "错误：【效果】里的 {%s} 有歧义（%s 里都有这个名字），请写全 $来源.%s"
            % (name, "、".join(sources), name)
        )
        return {"kind": "error", "content": "{%s}" % name, "name": name}

    messages.append(
        "错误：【效果】引用了 {%s}，但它既不是本卡变量，也不在运行时量注册表里" % name
    )
    return {"kind": "error", "content": "{%s}" % name, "name": name}


def _build_effect_segments(
    effect: Optional[str],
    entries: Dict[str, Dict[str, Any]],
    registry: RuntimeRegistry,
    messages: List[str],
) -> List[Dict[str, Any]]:
    """把效果文本切成「原样文字 / 卡内变量 / 运行时量 / 解析失败」四种分段。

    ``kind`` 取值 text / card / runtime / error。
    解析失败的占位符不降级成 text——降级会让它看起来像普通文字，
    Unity 侧就没有任何信号把那个 ``{xxx}`` 标红。
    """
    text = effect or ""
    segments: List[Dict[str, Any]] = []
    cursor = 0

    for match in PLACEHOLDER_RE.finditer(text):
        if match.start() > cursor:
            segments.append({"kind": "text", "content": text[cursor:match.start()]})
        cursor = match.end()

        raw_name = match.group(1).strip()
        if not BARE_NAME_RE.match(raw_name):
            messages.append(
                "错误：【效果】里的 {%s} 不是变量名——花括号里不能写算式，"
                "算式要写进【变量】的值里，这里只引用变量名" % raw_name
            )
            segments.append(
                {"kind": "error", "content": match.group(0), "name": raw_name}
            )
            continue

        segments.append(_resolve_placeholder(raw_name, entries, registry, messages))

    if cursor < len(text):
        segments.append({"kind": "text", "content": text[cursor:]})
    if not segments and text:
        segments.append({"kind": "text", "content": text})
    return segments


def _collect_runtime_deps(
    entries: Dict[str, Dict[str, Any]],
    segments: List[Dict[str, Any]],
    registry: RuntimeRegistry,
) -> List[Dict[str, Any]]:
    """汇总这张卡用到的所有运行时量，供 Unity 运行时提前准备取值。"""
    deps: List[Dict[str, Any]] = []
    seen = set()

    def add(source: str, name: str) -> None:
        if (source, name) in seen:
            return
        seen.add((source, name))
        deps.append({"source": source, "name": name, "type": registry.type_of(source, name)})

    for entry in entries.values():
        for source, name in entry.get("runtime_refs", []):
            add(source, name)
    for segment in segments:
        if segment.get("kind") == "runtime":
            add(segment["source"], segment["name"])
    return deps


def _export_variables(entries: Dict[str, Dict[str, Any]]) -> List[Dict[str, Any]]:
    """把归并后的变量表导出成 bindings.variables。"""
    result: List[Dict[str, Any]] = []
    for base, entry in entries.items():
        kinds = entry["kinds"]
        if not kinds:
            kind = "literal"
        elif len(kinds) == 1:
            kind = kinds[0]
        else:
            kind = "mixed"

        item: Dict[str, Any] = {"key": base, "kind": kind, "levels": _levels_map(entry)}
        if entry["refs"]:
            item["refs"] = list(entry["refs"])
        if entry["runtime_refs"]:
            item["runtime_refs"] = ["%s.%s" % (s, n) for s, n in entry["runtime_refs"]]
        result.append(item)
    return result


def build_bindings(
    fields: Dict[str, Any],
    registry: RuntimeRegistry,
) -> Tuple[Optional[Dict[str, Any]], List[str]]:
    """生成一张卡的 ``bindings``，顺带跑完变量与效果的校验。

    ``bindings`` 是**派生字段**：值全部能从 variables + effect 推出来。
    之所以还是写进导出，是为了让 Unity 运行时不必再实现一遍
    「等级回退」「运行时量查表」这些规则——照抄即可。
    提示统一以「错误：」「警告：」开头，和 Unity 侧 VariableCodec 保持一致。
    """
    if registry is None:
        registry = RuntimeRegistry()

    messages: List[str] = []
    parsed = parse_variables(fields.get("variables"))
    entries = _group_variables(parsed["items"], messages)

    _analyze_variable_entries(entries, registry, messages)
    messages.extend(_find_cycles(entries))
    segments = _build_effect_segments(fields.get("effect"), entries, registry, messages)
    runtime_deps = _collect_runtime_deps(entries, segments, registry)

    # 只有「整句都是原样文字」才算空壳。error 段不是 text，
    # 所以写错的卡一定会带上 bindings，Unity 才有机会把那段标红。
    only_text = all(segment["kind"] == "text" for segment in segments)
    if not entries and not runtime_deps and only_text:
        # 既没变量、又没引用任何运行时量、效果里也没有占位符 → 不写空壳
        return None, messages

    bindings = {
        "levels": [str(level) for level in range(1, MAX_LEVEL + 1)],
        "variables": _export_variables(entries),
        "effect_segments": segments,
        "runtime_deps": runtime_deps,
    }
    return bindings, messages


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
    registry: RuntimeRegistry,
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

    missing = [
        FIELD_LABEL_BY_KEY[key]
        for key, _ in FIELD_SPEC
        if not fields.get(key) and key not in OPTIONAL_FIELD_KEYS
    ]
    if node_type == "text" and "name" in fields:
        pass
    elif node_type == "text":
        fallback = parse_name_from_unknown_text(text or "")
        if fallback:
            fields["name"] = fallback
            warnings.append("节点 %s 未填写【卡名】，暂用正文首行代替" % node_id)

    # 变量与效果的校验、以及 Unity 运行时要用的 bindings 都在这里算出来
    bindings: Optional[Dict[str, Any]] = None
    if node_type == "text":
        bindings, binding_messages = build_bindings(fields, registry)
        warnings.extend("%s: %s" % (node_id, message) for message in binding_messages)

    card: Dict[str, Any] = {
        "id": node_id,
        "type": node_type,
        "name": fields.get("name"),
        "rarity": fields.get("rarity"),
        "category": fields.get("category"),
        "effect": fields.get("effect"),
        "variables": parse_variables(fields.get("variables")),
        "keywords": parse_keywords(fields.get("keywords")),
        "bindings": bindings,
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

    # 没有词条的卡不写 keywords，免得每张卡都挂一个空数组
    if not card["keywords"]:
        del card["keywords"]
    # 既没变量又没引用运行时量的卡不写 bindings，同样是避免空壳噪音
    if not card["bindings"]:
        del card["bindings"]

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


def convert(
    canvas_path: Path,
    canvas_dir: Optional[Path],
    data: Dict[str, Any],
    registry: RuntimeRegistry,
) -> Dict[str, Any]:
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
        card, card_warnings = build_card(node, index, parent_of, registry)
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
    parser.add_argument(
        "--runtime-vars",
        default=None,
        help="运行时量注册表，默认 schema/runtime_vars.json",
    )
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
    runtime_vars_path = Path(args.runtime_vars) if args.runtime_vars else DEFAULT_RUNTIME_VARS

    try:
        setup_logging(args.verbose, log_file)
    except CanvasError as exc:
        print("错误: %s" % exc, file=sys.stderr)
        return 1

    LOGGER.debug("项目根目录: %s", PROJECT_ROOT)
    LOGGER.debug("画布目录: %s", canvas_dir)
    LOGGER.debug("导出目录: %s", out_dir)
    LOGGER.debug("运行时量注册表: %s", runtime_vars_path)

    # 注册表读不到时只降级告警，不让整个导出失败——否则一张卡写错了
    # 会导致所有画布都导不出来，排查成本反而更高。
    registry, registry_warnings = RuntimeRegistry.load(runtime_vars_path)
    for message in registry_warnings:
        LOGGER.warning("%s", message)
    if registry.loaded_from:
        LOGGER.info(
            "运行时量注册表 %d 个来源 / %d 个量",
            len(registry.sources),
            len(registry.by_name),
        )

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
            payload = convert(canvas_path, canvas_dir, data, registry)
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
