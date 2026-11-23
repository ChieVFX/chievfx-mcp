# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class ToolBatchMixin:
    """tool-batch: several tool calls and pauses in one request, stopping at the first failed step."""

    def tool_batch(self, arguments: dict[str, Any], request_id: Any = None, notify: Any | None = None) -> dict[str, Any]:
        """Runs steps in order and keeps each step's output.

        Two shapes: steps:[{tool, args} | {waitMs}] for a sequence of different tools (stops at the first
        failure by default, since later steps usually depend on earlier ones), and the original
        tool + items for one tool over many argument sets (keeps going by default).
        """
        steps, legacy_tool = self.read_tool_batch_steps(arguments)
        stop_on_error = arguments.get("stopOnError")
        stop_on_error = (legacy_tool is None) if not isinstance(stop_on_error, bool) else stop_on_error

        rows: list[dict[str, Any]] = []
        images: list[dict[str, Any]] = []
        for index, step in enumerate(steps):
            if "waitMs" in step:
                wait_ms = min(step["waitMs"], HARD_TOOL_BATCH_WAIT_MS)
                time.sleep(wait_ms / 1000)
                rows.append({"index": index, "waitMs": wait_ms, "ok": True})
                continue

            tool_name = step["tool"]
            step_raw: dict[str, Any] = {}
            try:
                result = self.call_tool({"name": tool_name, "arguments": dict(step["args"])}, request_id, notify, step_raw)
            except Exception as exc:  # noqa: BLE001 - one step's failure is that step's answer.
                rows.append({"index": index, "tool": tool_name, "ok": False, "error": str(exc)})
                if stop_on_error:
                    break
                continue

            content = result.get("content") if isinstance(result, dict) else None
            blocks = content if isinstance(content, list) else []
            text = "\n".join(
                block["text"].strip("\n") for block in blocks if isinstance(block, dict) and block.get("type") == "text" and isinstance(block.get("text"), str)
            )
            step_images = [block for block in blocks if isinstance(block, dict) and block.get("type") == "image"]
            # A tool that answers instead of throwing (script-execute success:false, input ok:false) still failed.
            raw_step_result = step_raw.get("result")
            failure = None if isinstance(raw_step_result, dict) and raw_step_result.get("awaitedEndOk") else describe_reported_failure(raw_step_result, tool_name)
            row = {"index": index, "tool": tool_name, "ok": failure is None, "text": text}
            if failure is not None:
                row["error"] = failure
            if step_images:
                row["imageCount"] = len(step_images)
                images.extend({"index": index, "block": block} for block in step_images)
            rows.append(row)
            if failure is not None and stop_on_error:
                break

        failed_count = sum(1 for row in rows if not row["ok"])
        batch = {
            "success": failed_count == 0 and len(rows) == len(steps),
            "okCount": len(rows) - failed_count,
            "failedCount": failed_count,
            "notRunCount": len(steps) - len(rows),
            "totalCount": len(steps),
            "steps": rows,
        }
        if legacy_tool is not None:
            batch["tool"] = legacy_tool
        # Image blocks cannot ride inside the text; the caller turns them into content blocks.
        batch["_images"] = images
        return batch

    def read_tool_batch_steps(self, arguments: dict[str, Any]) -> tuple[list[dict[str, Any]], str | None]:
        raw_steps = arguments.get("steps")
        legacy_tool = arguments.get("tool")
        if raw_steps is not None and legacy_tool is not None:
            raise ToolCallError("tool-batch takes either 'steps' or 'tool' + 'items', not both.")

        if raw_steps is None:
            items = arguments.get("items")
            if not isinstance(legacy_tool, str) or not legacy_tool:
                raise ToolCallError("tool-batch requires 'steps' ([{tool, args}]) or string 'tool' with 'items'.")
            if not isinstance(items, list) or not items:
                raise ToolCallError("tool-batch requires non-empty array 'items'.")
            raw_steps = [{"tool": legacy_tool, "args": item} for item in items]
        elif not isinstance(raw_steps, list) or not raw_steps:
            raise ToolCallError("tool-batch requires non-empty array 'steps'.")

        known_tool_ids = {tool["name"] for tool in all_tools()}
        enabled_tool_ids = load_enabled_tool_ids()
        steps: list[dict[str, Any]] = []
        problems: list[str] = []
        # Every step is checked before the first one runs: a typo in step 5 must not leave steps 1-4 done.
        for index, raw in enumerate(raw_steps):
            if not isinstance(raw, dict):
                problems.append(f"step {index + 1}: must be an object")
                continue
            if "waitMs" in raw and "tool" in raw:
                problems.append(
                    f"step {index + 1}: a pause is a step of its own ({{waitMs}}); to wait for this step's end put awaitMs in its args"
                )
                continue
            if "waitMs" in raw:
                wait_ms = raw.get("waitMs")
                if isinstance(wait_ms, bool) or not isinstance(wait_ms, int) or wait_ms < 0:
                    problems.append(f"step {index + 1}: waitMs must be a non-negative integer")
                    continue
                steps.append({"waitMs": wait_ms})
                continue
            tool_name = raw.get("tool")
            args = raw.get("args", raw.get("arguments", {}))
            # A client names tools to the model as mcp__<server>__<tool>, and that is the name the model copies here.
            if isinstance(tool_name, str) and tool_name not in known_tool_ids and tool_name.startswith("mcp__"):
                short_name = tool_name.rsplit("__", 1)[-1]
                if short_name in known_tool_ids:
                    tool_name = short_name
            if not isinstance(tool_name, str) or not tool_name:
                problems.append(f"step {index + 1}: needs string 'tool' (or 'waitMs' for a pause)")
            elif tool_name == "tool-batch":
                problems.append(f"step {index + 1}: tool-batch cannot batch itself")
            elif tool_name not in known_tool_ids:
                similar = difflib.get_close_matches(tool_name, sorted(known_tool_ids), n=2, cutoff=0.8)
                problems.append(f"step {index + 1}: unknown tool '{tool_name}'" + (f" (did you mean {' or '.join(similar)}?)" if similar else ""))
            elif tool_name not in enabled_tool_ids:
                problems.append(f"step {index + 1}: tool '{tool_name}' is disabled")
            elif not isinstance(args, dict):
                problems.append(f"step {index + 1}: args must be an object")
            else:
                steps.append({"tool": tool_name, "args": args})
        if problems:
            raise ToolCallError("tool-batch did not run: " + "; ".join(problems) + ".")
        return steps, legacy_tool if isinstance(legacy_tool, str) else None
