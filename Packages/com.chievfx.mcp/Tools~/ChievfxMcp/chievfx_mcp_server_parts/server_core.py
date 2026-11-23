# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

from __future__ import annotations

class McpServerCore:
    def __init__(self, unity_url: str, bridge_dir: str, timeout_ms: int) -> None:
        self.unity_url = unity_url.rstrip("/")
        self.bridge_dir = Path(bridge_dir)
        self.request_dir = self.bridge_dir / "requests"
        self.response_dir = self.bridge_dir / "responses"
        self.operation_dir = self.bridge_dir / "operations"
        self.cancel_dir = self.bridge_dir / "cancel"
        self.state_path = self.bridge_dir / "state.json"
        self.event_path = self.bridge_dir / "events.json"
        self.timeout_seconds = max(timeout_ms, 1000) / 1000
        self.request_operation_ids: dict[str, str] = {}
        self.request_operation_lock = threading.Lock()
        self.bridge_call_lock = threading.Lock()
        self.wait_cancellations: dict[str, threading.Event] = {}
        self.wait_cancellation_lock = threading.Lock()
        self.active_event_waits: dict[str, dict[str, Any]] = {}
        self.active_event_wait_lock = threading.Lock()
        # Event cursor and time of the last tool reply that named an end to wait for (see _remember_async_action).
        self.last_async_action_cursor: int | None = None
        self.last_async_action_at = 0.0
        # Whether this session read the full tool list, and whether the one-shot nudge already went out.
        self.core_descriptor_read_marker_path = self.bridge_dir / CORE_DESCRIPTOR_READ_MARKER_FILENAME
        self.core_descriptors_read = self.read_core_descriptor_marker()
        self.core_descriptor_reminder_sent = False
        configure_extension_manifest_bridge_fetcher(self.fetch_extension_manifest_from_bridge)

    def read_core_descriptor_marker(self) -> bool:
        """True when the core descriptors were read recently enough to skip the notice.

        Best-effort by design: a missing, unreadable, or stale marker just means the notice can fire.
        """
        try:
            raw = json.loads(self.core_descriptor_read_marker_path.read_text(encoding="utf-8"))
        except Exception:
            return False
        if not isinstance(raw, dict):
            return False

        now = time.time()
        session = client_session_key()
        sessions = raw.get("sessions")
        if session is not None and isinstance(sessions, dict):
            session_read_at = sessions.get(session)
            return isinstance(session_read_at, (int, float)) and 0 <= now - float(session_read_at) <= CORE_DESCRIPTOR_SESSION_MARKER_TTL_SECONDS

        # A client that names no session, or a marker written before sessions were kept: the shared window.
        read_at = raw.get("readAtEpochSeconds")
        if not isinstance(read_at, (int, float)):
            return False

        age = now - float(read_at)
        return 0 <= age <= CORE_DESCRIPTOR_READ_MARKER_TTL_SECONDS

    def write_core_descriptor_marker(self) -> None:
        try:
            self.bridge_dir.mkdir(parents=True, exist_ok=True)
            now = time.time()
            previous = read_json_file(self.core_descriptor_read_marker_path) or {}
            sessions = previous.get("sessions") if isinstance(previous.get("sessions"), dict) else {}
            sessions = {
                key: value
                for key, value in sessions.items()
                if isinstance(value, (int, float)) and 0 <= now - float(value) <= CORE_DESCRIPTOR_SESSION_MARKER_TTL_SECONDS
            }
            session = client_session_key()
            if session is not None:
                sessions[session] = now
            write_json_file_atomic(
                self.core_descriptor_read_marker_path,
                {"readAtEpochSeconds": now, "uri": CORE_DESCRIPTOR_INSTRUCTIONS_URI, "sessions": sessions},
            )
        except Exception:
            # The in-memory flag still covers this process; persistence is an optimization.
            pass

    def fetch_extension_manifest_from_bridge(self) -> dict[str, Any] | None:
        if not self.bridge_dir or not self.state_path.exists():
            return None

        heartbeat_age = file_age_seconds(self.state_path, time.time())
        if heartbeat_age is None or heartbeat_age > HEARTBEAT_STALE_SECONDS:
            return None

        try:
            if not self.wait_for_bridge_ready(max_wait_seconds=2.0):
                return None
            bridge_result = self.call_unity_bridge("extension-capabilities-get", {})
            result = bridge_result.get("result")
            return result if isinstance(result, dict) else None
        except Exception:
            return None

    def handle_message(self, message: Any, notify: Any | None = None) -> Any:
        if isinstance(message, list):
            return [self.handle_message(item, notify) for item in message if "id" in item]

        if not isinstance(message, dict):
            return self.error_response(None, -32600, "Invalid JSON-RPC message.")

        request_id = message.get("id")
        method = message.get("method")
        params = message.get("params") or {}

        try:
            if method == "initialize":
                instructions = build_initialize_instructions()
                # Always advertise listChanged so selection edits in Unity can push
                # notifications/*/list_changed and the client re-fetches. Prompts must be
                # advertised even when currently empty, otherwise a 0 -> N change cannot be
                # signaled (the client would have been told prompts are unsupported).
                list_changed = {"listChanged": True}
                capabilities = {
                    "tools": dict(list_changed),
                    "resources": dict(list_changed),
                    "prompts": dict(list_changed),
                }
                return self.result_response(
                    request_id,
                    {
                        "protocolVersion": params.get("protocolVersion", "2024-11-05"),
                        "capabilities": capabilities,
                        "serverInfo": build_initialize_server_info(instructions),
                        "instructions": instructions,
                    },
                )

            if method == "notifications/initialized":
                # A bare reload re-handshakes (fresh instructions + bumped serverInfo.version),
                # but Cursor does not COMMIT the new initialize.instructions into the agent
                # context / INSTRUCTIONS.md dump until it receives a list_changed. Without this,
                # users had to manually toggle a tool after every reload to force the refresh.
                # Emitting a one-shot list_changed right after initialized provokes that commit
                # with the just-handshaked instructions. The selection is stable post-reload
                # (no concurrent edit), so there is no one-step lag like a live toggle has.
                self.schedule_post_initialize_list_changed(notify)
                return None

            if method == "notifications/cancelled":
                self.handle_cancelled_notification(params)
                return None

            if method == "ping":
                return self.result_response(request_id, {})

            if method == "tools/list":
                return self.result_response(request_id, {"tools": enabled_tools()})

            if method == "tools/call":
                return self.result_response(request_id, self.call_tool(params, request_id, notify))

            if method == "resources/list":
                return self.result_response(
                    request_id, {"resources": enabled_resources() + dynamic_category_resources()}
                )

            if method == "resources/templates/list":
                return self.result_response(request_id, {"resourceTemplates": enabled_resource_templates()})

            if method == "resources/read":
                return self.result_response(request_id, self.read_resource(params, request_id))

            if method == "prompts/list":
                prompts = enabled_prompts()
                return self.result_response(request_id, {"prompts": prompts})

            if method == "prompts/get":
                return self.result_response(request_id, self.get_prompt(params, request_id))

            if method == "shutdown":
                return self.result_response(request_id, {})

            return self.error_response(request_id, -32601, f"Method not found: {method}")
        except ResourceNotFoundError as exc:
            return self.error_response(request_id, -32002, str(exc))
        except PromptNotFoundError as exc:
            return self.error_response(request_id, -32003, str(exc))
        except PromptArgumentError as exc:
            return self.error_response(request_id, -32602, str(exc))
        except ValueError as exc:
            return self.error_response(request_id, -32000, str(exc))
        except Exception as exc:  # noqa: BLE001 - unexpected server failures only.
            print(traceback.format_exc(), file=sys.stderr)
            return self.error_response(request_id, -32000, str(exc))

    def schedule_post_initialize_list_changed(self, notify: Any | None) -> None:
        """Nudges Cursor to commit freshly-handshaked instructions after a reload.

        HTTP transport calls handle_message without a notify sink; there is no client
        connection to push to, so this is a stdio-only nudge.
        """
        if notify is None:
            return

        def emit() -> None:
            time.sleep(POST_INITIALIZE_LIST_CHANGED_DELAY_SECONDS)
            for kind in ("tools", "resources", "prompts"):
                notify({"jsonrpc": "2.0", "method": LIST_CHANGED_METHOD_BY_KIND[kind]})

        threading.Thread(target=emit, daemon=True).start()

    def handle_cancelled_notification(self, params: dict[str, Any]) -> None:
        cancelled_request_id = params.get("requestId")
        if cancelled_request_id is None:
            return

        request_key = str(cancelled_request_id)
        with self.wait_cancellation_lock:
            wait_cancel = self.wait_cancellations.get(request_key)
        if wait_cancel is not None:
            wait_cancel.set()
            self.mark_event_wait_cancellation_requested(request_key, params.get("reason"))

        with self.request_operation_lock:
            operation_id = self.request_operation_ids.get(request_key)

        if not operation_id:
            return

        reason = params.get("reason")
        marker = {
            "operationId": operation_id,
            "jsonRpcRequestId": request_key,
            "requestedAtUtc": utc_now_iso(),
            "reason": reason if isinstance(reason, str) else "client cancelled request",
        }
        cancel_path = self.cancel_dir / f"{operation_id}.json"
        write_json_file_atomic(cancel_path, marker)
        self.update_operation_record(
            operation_id,
            state="cancelRequested",
            progressMessage="Cancellation requested by MCP client.",
            cancellationRequested=True,
        )
        request_path = self.request_dir / f"{operation_id}.json"
        processing_path = self.request_dir / f"{operation_id}.json.processing"
        if request_path.exists() and not processing_path.exists():
            try:
                request_path.unlink()
            except OSError:
                return

            cancel_path.unlink(missing_ok=True)
            self.update_operation_record(
                operation_id,
                state="cancelled",
                completedAtUtc=utc_now_iso(),
                progressMessage="Cancelled before Unity started the operation.",
            )
            write_json_file_atomic(
                self.response_dir / f"{operation_id}.json",
                {"ok": False, "error": f"Bridge operation {operation_id} cancelled before Unity started it."},
            )

    def get_prompt(self, params: dict[str, Any], request_id: Any = None) -> dict[str, Any]:
        if not isinstance(params, dict):
            raise PromptArgumentError("prompts/get params must be an object.")

        name = params.get("name")
        if not isinstance(name, str):
            raise PromptArgumentError("prompts/get requires string param 'name'.")

        prompt = get_prompt_by_name(name)
        ensure_prompt_enabled(name)
        arguments = validate_prompt_arguments(prompt, params.get("arguments"))

        bridge_command = prompt.get("bridgeCommand")
        if prompt.get("dynamic") or isinstance(bridge_command, str):
            command = bridge_command if isinstance(bridge_command, str) and bridge_command else "prompt-get"
            bridge_result = self.call_unity_bridge(command, {"name": name, "arguments": arguments}, request_id)
            return coerce_bridge_prompt_result(prompt, bridge_result.get("result"))

        return render_static_prompt(prompt, arguments)

    def call_tool(
        self,
        params: dict[str, Any],
        request_id: Any = None,
        notify: Any | None = None,
        raw_sink: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        # Single chokepoint for every tool: dispatch, then surface any argument the tool does not
        # declare. Silently dropping a mistyped argument (outputPath vs savePath) sends the caller
        # hunting for an effect that never happened.
        name = params.get("name")
        await_ms, params = self._take_await_argument(params)
        renamed_arguments: list[str] = []
        if isinstance(name, str):
            resolved_arguments, renamed_arguments = resolve_argument_aliases(name, params.get("arguments"))
            if renamed_arguments:
                params = {**params, "arguments": resolved_arguments}
        # The cursor is taken before the call, so an end logged while the tool is still replying is
        # inside the window: the race that made a separate events-wait miss short actions cannot happen.
        await_cursor = self.event_stream_last_id(self.read_event_stream()) if await_ms is not None else 0
        raw_sink = raw_sink if raw_sink is not None else {}
        # The reply's own "how to wait" advice is moot once the server waits for the end itself.
        raw_sink["awaiting"] = await_ms is not None
        result = self._dispatch_tool_call(params, request_id, notify, raw_sink)
        self._remember_async_action(raw_sink.get("result"), await_cursor if await_ms is not None else None)
        if isinstance(name, str):
            result = with_unknown_argument_warning(result, name, params.get("arguments") or {})
            if await_ms is not None:
                meta = params.get("_meta")
                progress_token = meta.get("progressToken") if isinstance(meta, dict) else None
                with self._await_progress_ticker(progress_token, notify, name, await_ms):
                    result = self._await_tool_completion(result, name, raw_sink.get("result"), await_ms, await_cursor, request_id)
            renamed_arguments = renamed_arguments + raw_sink.get("argumentsRenamed", [])
            if renamed_arguments:
                attach_result_notice(result, "Arguments taken under the tool's names: " + ", ".join(renamed_arguments))
            result = self._with_core_descriptor_reminder(result, name)
        return result

    def _with_core_descriptor_reminder(self, result: Any, name: str) -> Any:
        """Nudge once per session, on the first tool call, if the full tool list was never read.

        An imperative in initialize.instructions is easy to skip — clients truncate it, and it competes
        with everything else in context. A line that arrives attached to the first actual Unity result
        lands when it is actionable.

        Stays silent on the read-only calls the precondition explicitly allows before that read
        (CORE_DESCRIPTOR_GRACE_TOOL_IDS): a notice contradicting the instruction it is enforcing
        teaches the caller to discount both.
        """
        if self.core_descriptors_read or self.core_descriptor_reminder_sent:
            return result
        if name in CORE_DESCRIPTOR_REMEDY_TOOL_IDS or name in CORE_DESCRIPTOR_GRACE_TOOL_IDS:
            return result

        delivered = attach_result_notice(
            result,
            f"! First ChievFX tool call this session and {CORE_DESCRIPTOR_INSTRUCTIONS_URI} has not "
            "been read. Read it now: startup instructions are truncated by most clients, so it is "
            "the only complete list of tools and argument signatures. No ReadMcpResourceTool? "
            f"Call {MCP_RESOURCE_READ_TOOL_ID} with that uri.",
        )
        if delivered:
            self.core_descriptor_reminder_sent = True
        return result

    def _dispatch_tool_call(
        self,
        params: dict[str, Any],
        request_id: Any = None,
        notify: Any | None = None,
        raw_sink: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        name = params.get("name")
        arguments = params.get("arguments") or {}
        if not isinstance(name, str):
            raise ToolCallError("tools/call requires string param 'name'.")

        core_tool_ids = {tool["name"] for tool in TOOLS}
        known_tool_ids = {tool["name"] for tool in all_tools()}
        if name == REMOTE_CMD_TOOL_ID and name not in known_tool_ids:
            # A disarmed remote-cmd is absent from all_tools() by design, and "unknown tool" would send
            # the agent looking for a typo instead of telling the operator which switch is off.
            raise RemoteCmdUnavailableError(remote_cmd_gate()["reason"])
        if name not in known_tool_ids:
            raise ToolCallError(f"Unknown ChievFX MCP tool '{name}'.")

        if name not in load_enabled_tool_ids():
            raise ToolCallError(
                f"ChievFX MCP tool '{name}' is disabled. Enable it in Window > ChievFX > MCP Tools, "
                "then reload MCP tools or restart Cursor."
            )

        if name == MCP_RESOURCE_READ_TOOL_ID:
            uri = arguments.get("uri")
            if not isinstance(uri, str) or not uri.strip():
                raise ToolCallError(f"{name} requires string 'uri', e.g. {CORE_DESCRIPTOR_INSTRUCTIONS_URI}.")
            # The resources/read path itself, so reading the core descriptors this way counts as having read them.
            contents = self.read_resource({"uri": uri.strip()}, request_id).get("contents", [])
            return {"content": [{"type": "text", "text": item.get("text", "")} for item in contents], "isError": False}

        if name == "tools-list-categories":
            result = list_tool_categories_for_agents()
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_tool_categories_text(result, read_include_disabled(arguments)),
                    }
                ],
                "isError": False,
            }

        if name == "tools-list-category":
            result = list_tool_category_for_agents(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_tool_category_text(result, read_include_disabled(arguments)),
                    }
                ],
                "isError": False,
            }

        if name == "tools-set-enabled-state":
            result = set_tools_enabled_state(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_set_enabled_state_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "tools-get-roles":
            result = get_tool_role_state(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_tool_role_catalog_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "tools-get-role":
            result = get_tool_role_details(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_tool_role_details_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "tools-set-role":
            result = set_tool_role(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_tool_role_set_compact_text(result),
                    }
                ],
                "isError": False,
            }

        if name == REMOTE_CMD_TOOL_ID:
            result = run_remote_cmd(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_remote_cmd_text(result),
                    }
                ],
                # A non-zero exit is the command's answer, not a tool failure: the agent needs the
                # output either way, and isError makes some clients discard it.
                "isError": False,
            }

        if name == "bridge-get-operation":
            return self.text_tool_result(self.get_bridge_operation(arguments), arguments)

        if name == "bridge-get-status":
            return self.text_tool_result(self.get_bridge_status(arguments), arguments)

        if name == "events-check-since":
            result = self.events_check_since(arguments)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_events_check_since_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "events-wait":
            return self.text_tool_result(self.events_wait(arguments, request_id), arguments)

        if name == "tool-batch":
            result = self.tool_batch(arguments, request_id, notify)
            images = result.pop("_images", [])
            if arguments.get("outputFormat") == "json":
                content = self.text_tool_result(result, arguments)["content"]
            else:
                content = [{"type": "text", "text": format_tool_batch_text(result)}]
            # A screenshot step is often the point of the batch (tap -> wait -> look), so its image travels
            # with the result instead of being reduced to a line of metadata.
            for image in images:
                content.append({"type": "text", "text": f"step {image['index'] + 1} image:"})
                content.append(image["block"])
            return {"content": content, "isError": False}

        progress_token = self.get_progress_token(params)
        if name == "recompile":
            result = self.recompile(arguments, request_id, progress_token, notify)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_recompile_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "editor-playmode-set":
            result = self.editor_playmode_set(arguments, request_id, progress_token, notify)
            if arguments.get("outputFormat") == "json":
                return self.text_tool_result(result, arguments)
            return {
                "content": [
                    {
                        "type": "text",
                        "text": format_editor_playmode_set_text(result),
                    }
                ],
                "isError": False,
            }

        if name == "script-execute" and "csharpPath" in arguments:
            arguments = read_script_source_file(arguments)
        if name == "script-execute" and "parameters" in arguments:
            arguments = parse_script_parameters(arguments)
        if name == "script-execute" and SCRIPT_STARTS_EDITOR_WORK_PATTERN.search(str(arguments.get("csharpCode") or "")):
            self._script_started_editor_work_at = time.monotonic()
        bridge_arguments = arguments
        force_ui_control_find_json = name == "ui-control-find" and arguments.get("outputFormat") != "json"
        force_ui_runtime_click_json = name == "ui-runtime-click" and arguments.get("outputFormat") != "json"
        force_ui_runtime_drag_json = name == "ui-runtime-drag" and arguments.get("outputFormat") != "json"
        force_ui_runtime_set_control_value_json = (
            name == "ui-runtime-set-control-value" and arguments.get("outputFormat") != "json"
        )
        force_ui_runtime_focus_json = name == "ui-runtime-focus" and arguments.get("outputFormat") != "json"
        force_ui_runtime_type_text_json = (
            name == "ui-runtime-type-text" and arguments.get("outputFormat") != "json"
        )
        force_ui_runtime_clear_focus_json = (
            name == "ui-runtime-clear-focus" and arguments.get("outputFormat") != "json"
        )
        if (
            force_ui_control_find_json
            or force_ui_runtime_click_json
            or force_ui_runtime_drag_json
            or force_ui_runtime_set_control_value_json
            or force_ui_runtime_focus_json
            or force_ui_runtime_type_text_json
            or force_ui_runtime_clear_focus_json
        ):
            bridge_arguments = dict(arguments)
            bridge_arguments["outputFormat"] = "json"

        # savePath is handled server-side (we already hold the resulting PNG bytes);
        # strip it so the Unity bridge never receives an argument it does not use.
        # An extension that declares savePath itself gets it untouched.
        raw_save_path = arguments.get("savePath")
        save_path = raw_save_path.strip() if isinstance(raw_save_path, str) else None
        if save_path and extension_owns_save_path(name):
            save_path = None
        if save_path:
            if bridge_arguments is arguments:
                bridge_arguments = dict(arguments)
            bridge_arguments.pop("savePath", None)

        if name == "frame-debugger-pick-pixel":
            bridge_result = self.run_frame_debugger_pixel_search(bridge_arguments, request_id)
        elif name == "frame-debugger-drawcall-screenshot":
            bridge_result = self.run_frame_debugger_drawcall_capture(bridge_arguments, request_id, progress_token, notify)
        elif name == "frame-debugger-drawcall-get":
            bridge_result = self.call_frame_debugger_draw_call(name, bridge_arguments, request_id, progress_token, notify)
        else:
            bridge_result = self.call_unity_bridge(name, bridge_arguments, request_id, progress_token, notify)
        if bridge_result.get("contentType") == "image" and name == "frame-debugger-drawcall-screenshot":
            bridge_result = without_unready_event_details(bridge_result)
        if bridge_result.get("contentType") == "image":
            content: list[dict[str, Any]] = [
                {
                    "type": "image",
                    "data": bridge_result["base64"],
                    "mimeType": bridge_result.get("mimeType", "image/png"),
                }
            ]
            metadata = bridge_result.get("metadata")
            if save_path:
                save_info = self.save_image_to_path(bridge_result["base64"], save_path)
                metadata = {**metadata, **save_info} if metadata is not None else save_info
            if metadata is not None:
                content.append(
                    {
                        "type": "text",
                        "text": format_tool_text(metadata, arguments),
                    }
                )

            result = {
                "content": content,
                "isError": False,
            }
            if metadata is not None:
                result["structuredContent"] = metadata
            return result

        result = bridge_result.get("result")
        if name.startswith("frame-debugger-") and isinstance(result, dict):
            result = self.settle_frame_debugger_result(name, arguments, result, request_id)
        if raw_sink is not None:
            raw_sink["result"] = result
            # Names the tool renamed itself (a point string into x, y, z) join the server's own renames in one line.
            if isinstance(result, dict) and isinstance(result.get("argumentsRenamed"), list):
                raw_sink["argumentsRenamed"] = [item for item in result["argumentsRenamed"] if isinstance(item, str) and item]
                result = {key: value for key, value in result.items() if key != "argumentsRenamed"}
            if raw_sink.get("awaiting"):
                result = without_wait_advice(result)
            else:
                result = with_wait_next(result)
        if force_ui_control_find_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                normalize_coords = arguments.get("normalizeCoords") if "normalizeCoords" in arguments else None
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_control_find_text(result, normalize_coords=normalize_coords),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_click_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_click_text(result),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_drag_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_drag_text(result),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_set_control_value_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_set_control_value_text(result),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_focus_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_focus_text(result),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_type_text_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_type_text_text(result),
                        }
                    ],
                    "isError": False,
                }

        if force_ui_runtime_clear_focus_json:
            if not isinstance(result, dict):
                retry_arguments = dict(arguments)
                retry_arguments["outputFormat"] = "json"
                bridge_result = self.call_unity_bridge(name, retry_arguments, request_id, progress_token, notify)
                result = bridge_result.get("result")
            if isinstance(result, dict):
                return {
                    "content": [
                        {
                            "type": "text",
                            "text": format_ui_runtime_clear_focus_text(result),
                        }
                    ],
                    "isError": False,
                }

        text = (
            format_reflection_method_find_text(result)
            if name == "reflection-method-find" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_reflection_method_find_single_text(result)
            if name == "reflection-method-find-single" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_editor_window_list_text(result)
            if name == "editor-window-list" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_editor_window_action_text(result)
            if name in {"editor-window-open", "editor-window-focus"} and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_editor_playmode_set_text(result)
            if name == "editor-playmode-set" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_package_list_text(result)
            if name == "package-list" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_package_search_text(result)
            if name == "package-search" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_package_mutation_text(result)
            if name in {"package-add", "package-remove"} and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_asset_find_text(result)
            if name == "asset-find" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_control_text(result)
            if name == "frame-debugger-control" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_groups_list_text(result)
            if name == "frame-debugger-groups-list" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_group_events_list_text(result)
            if name == "frame-debugger-group-events-list" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_drawcall_get_text(result)
            if name == "frame-debugger-drawcall-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_events_list_text(result)
            if name == "frame-debugger-events-list" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_frame_debugger_event_get_text(result)
            if name == "frame-debugger-event-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_gameobject_find_text(result)
            if name == "gameobject-find" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_gameobject_hierarchy_text(result)
            if name == "gameobject-hierarchy" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_ui_hierarchy_text(result)
            if name == "ugui-ui-hierarchy" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_ui_find_text(result)
            if name == "ugui-ui-find" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_rect_get_text(result)
            if name == "ugui-rect-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_textmeshpro_hierarchy_text(result)
            if name == "ugui-textmeshpro-hierarchy" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_textmeshpro_get_text(result)
            if name == "ugui-textmeshpro-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_gameobject_component_get_text(result)
            if name == "gameobject-component-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_gameobject_transform_get_text(result)
            if name == "gameobject-transform-get" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_runtime_probe_text(result)
            if name == "ui-runtime-probe"
            and arguments.get("outputFormat") != "json"
            and isinstance(result, dict)
            else format_ugui_sibling_draworder_set_text(result)
            if name == "ugui-sibling-draworder-set" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_sprite_configure_text(result)
            if name == "ugui-sprite-configure" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_image_set_text(result)
            if name == "ugui-image-set" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_grid_create_text(result)
            if name == "ugui-grid-create" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_element_create_text(result)
            if name in {"ugui-create-simple", "ugui-create-control"} and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_ugui_canvas_ensure_text(result)
            if name == "ugui-canvas-ensure" and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_camera_tool_text(name, result)
            if name in CAMERA_TOOL_NAMES and arguments.get("outputFormat") != "json" and isinstance(result, dict)
            else format_tool_result_text(name, result, arguments)
        )
        return {
            "content": [
                {
                    "type": "text",
                    "text": text,
                }
            ],
            "isError": False,
        }

    @staticmethod
    def save_image_to_path(base64_data: str, save_path: str) -> dict[str, Any]:
        """Write a base64-encoded image to disk for the optional savePath argument.

        Absolute paths (e.g. ``C:/Shots/view.png``) are used verbatim; relative paths
        (e.g. ``./Temp/view.png``) resolve against the Unity project folder. Returns a
        metadata fragment carrying either ``savedPath`` or ``savePathError``.
        """
        try:
            requested = Path(save_path).expanduser()
            target = requested if requested.is_absolute() else (PROJECT_ROOT / requested)
            target = target.resolve()
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(base64.b64decode(base64_data))
            return {"savedPath": str(target)}
        except Exception as exc:  # noqa: BLE001 - surface any failure to the caller as metadata
            return {"savePathError": f"Failed to save screenshot to '{save_path}': {exc}"}

    @staticmethod
    def text_tool_result(result: Any, arguments: dict[str, Any]) -> dict[str, Any]:
        return {
            "content": [
                {
                    "type": "text",
                    "text": format_tool_text(result, arguments),
                }
            ],
            "isError": False,
        }

    @staticmethod
    def get_progress_token(params: dict[str, Any]) -> Any | None:
        meta = params.get("_meta")
        if not isinstance(meta, dict):
            return None

        return meta.get("progressToken")
