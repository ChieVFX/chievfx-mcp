# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

_CATALOGS_CACHE: dict[str, Any] = {}
_CATALOGS_CACHE_MTIME: float | None = None
_INITIALIZE_INSTRUCTIONS_CACHE: dict[str, Any] = {}
_INITIALIZE_INSTRUCTIONS_CACHE_MTIME: float | None = None


def _catalogs_md_mtime() -> float | None:
    try:
        return float(CATALOGS_MD_PATH.stat().st_mtime)
    except OSError:
        return None


def _parse_bool(value: str) -> bool:
    lowered = value.strip().lower()
    if lowered in {"true", "1", "yes", "y"}:
        return True
    if lowered in {"false", "0", "no", "n"}:
        return False
    raise ValueError(f"Invalid bool: {value!r}")


def _split_md_records(md_text: str) -> list[str]:
    parts: list[str] = []
    cur: list[str] = []
    for raw_line in md_text.splitlines():
        if raw_line.strip() == "---":
            if cur:
                parts.append("\n".join(cur).strip("\n"))
                cur = []
            continue
        cur.append(raw_line)
    if cur:
        parts.append("\n".join(cur).strip("\n"))
    return [p for p in parts if p.strip()]


def _parse_key_value_block(record_text: str) -> dict[str, Any]:
    # Minimal parser for this repo’s catalog MD:
    # - scalar lines: key: value
    # - multiline blocks: key: | then raw lines until next "key:" line
    out: dict[str, Any] = {}
    lines = record_text.splitlines()
    i = 0
    while i < len(lines):
        raw = lines[i]
        stripped = raw.strip()
        if not stripped:
            i += 1
            continue
        if ":" not in stripped:
            i += 1
            continue
        key, rest = stripped.split(":", 1)
        key = key.strip()
        rest = rest.lstrip()
        if rest == "|":
            i += 1
            buf: list[str] = []
            while i < len(lines):
                nxt = lines[i]
                nxt_stripped = nxt.strip()
                if re.match(r"^[A-Za-z0-9_\\-]+:\\s+", nxt_stripped):
                    break
                buf.append(nxt.rstrip("\n"))
                i += 1
            out[key] = "\n".join(buf).rstrip()
            continue
        if rest.startswith("[") or rest.startswith("{"):
            try:
                out[key] = json.loads(rest)
                i += 1
                continue
            except json.JSONDecodeError:
                pass
        if rest.lower() in {"true", "false"}:
            out[key] = _parse_bool(rest)
            i += 1
            continue
        out[key] = rest
        i += 1
    return out


def load_text_catalogs_from_md() -> dict[str, Any]:
    global _CATALOGS_CACHE, _CATALOGS_CACHE_MTIME
    mtime = _catalogs_md_mtime()
    if mtime is not None and mtime == _CATALOGS_CACHE_MTIME and _CATALOGS_CACHE:
        return _CATALOGS_CACHE

    try:
        md_text = CATALOGS_MD_PATH.read_text(encoding="utf-8")
    except OSError:
        _CATALOGS_CACHE = {}
        _CATALOGS_CACHE_MTIME = mtime
        return _CATALOGS_CACHE

    records = _split_md_records(md_text)
    parsed: dict[str, Any] = {"resources": [], "resourceTemplates": [], "prompts": []}

    for rec in records:
        kv = _parse_key_value_block(rec)
        rtype = kv.get("type")
        if not isinstance(rtype, str):
            continue
        rtype = rtype.strip()

        if rtype == "resource":
            parsed["resources"].append(
                {
                    "id": kv["id"],
                    "uri": kv["uri"],
                    "name": kv["name"],
                    "description": kv["description"],
                    "mimeType": kv.get("mimeType", RESOURCE_MIME_TYPE),
                }
            )
        elif rtype == "resourceTemplate":
            tpl: dict[str, Any] = {
                "id": kv["id"],
                "uriTemplate": kv["uriTemplate"],
                "name": kv["name"],
                "description": kv["description"],
                "mimeType": kv.get("mimeType", RESOURCE_MIME_TYPE),
            }
            if "required" in kv:
                tpl["required"] = bool(kv["required"]) if isinstance(kv["required"], bool) else _parse_bool(str(kv["required"]))
            parsed["resourceTemplates"].append(tpl)
        elif rtype == "prompt":
            args = kv.get("arguments") or []
            message_role = kv.get("messageRole", "user")
            text = kv.get("text", "")
            parsed["prompts"].append(
                {
                    "name": kv["name"],
                    "title": kv["title"],
                    "description": kv["description"],
                    "category": kv["category"],
                    "arguments": args,
                    "messages": [{"role": message_role, "text": text}],
                }
            )

    _CATALOGS_CACHE = parsed
    _CATALOGS_CACHE_MTIME = mtime
    return _CATALOGS_CACHE


def _file_mtime(path: Path) -> float | None:
    try:
        return float(path.stat().st_mtime)
    except OSError:
        return None


def load_initialize_instruction_records_from_md() -> dict[str, Any]:
    global _INITIALIZE_INSTRUCTIONS_CACHE, _INITIALIZE_INSTRUCTIONS_CACHE_MTIME
    mtime = _file_mtime(INITIALIZE_INSTRUCTIONS_MD_PATH)
    if (
        mtime is not None
        and mtime == _INITIALIZE_INSTRUCTIONS_CACHE_MTIME
        and _INITIALIZE_INSTRUCTIONS_CACHE
    ):
        return _INITIALIZE_INSTRUCTIONS_CACHE

    try:
        md_text = INITIALIZE_INSTRUCTIONS_MD_PATH.read_text(encoding="utf-8")
    except OSError:
        _INITIALIZE_INSTRUCTIONS_CACHE = {"global": [], "tool": {}, "resource": {}, "resourceTemplate": {}, "prompt": {}}
        _INITIALIZE_INSTRUCTIONS_CACHE_MTIME = mtime
        return _INITIALIZE_INSTRUCTIONS_CACHE

    parsed: dict[str, Any] = {"global": [], "tool": {}, "resource": {}, "resourceTemplate": {}, "prompt": {}}
    for rec in _split_md_records(md_text):
        kv = _parse_key_value_block(rec)
        rtype = kv.get("type")
        text = kv.get("text")
        if not isinstance(rtype, str) or not isinstance(text, str) or not text.strip():
            continue
        rtype = rtype.strip()
        text = text.strip()
        if rtype == "global":
            parsed["global"].append(text)
            continue
        item_id = kv.get("id")
        if rtype in {"tool", "resource", "resourceTemplate", "prompt"} and isinstance(item_id, str) and item_id:
            parsed[rtype][item_id] = text

    _INITIALIZE_INSTRUCTIONS_CACHE = parsed
    _INITIALIZE_INSTRUCTIONS_CACHE_MTIME = mtime
    return _INITIALIZE_INSTRUCTIONS_CACHE
