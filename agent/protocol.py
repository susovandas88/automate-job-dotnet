import json
import os
import sys

_HIDDEN = {"screenshot", "image", "resume"}


def call_host(payload):
    if os.environ.get("LANGSMITH_TRACING") != "true":
        return _exchange(payload)

    try:
        from langsmith import trace
    except Exception:
        return _exchange(payload)

    name = str((payload or {}).get("tool") or "tool")
    try:
        span = trace(name, run_type="tool", inputs=_public(payload), project_name="automate-job")
        run = span.__enter__()
    except Exception:
        return _exchange(payload)

    try:
        result = _exchange(payload)
        try:
            if name == "fill":
                _trace_fill_actions((payload or {}).get("actions") or [])
            run.end(outputs=_public(result))
        except Exception:
            pass
        return result
    except Exception:
        span.__exit__(*sys.exc_info())
        raise
    else:
        span.__exit__(None, None, None)


def _trace_fill_actions(actions):
    from langsmith import trace

    for action in actions:
        if not isinstance(action, dict) or not action.get("value") or _is_password(action):
            continue
        with trace(
            _action_tool(action),
            run_type="tool",
            inputs=_public(action),
            project_name="automate-job",
        ) as run:
            run.end(outputs={"ok": True})


def _action_tool(action):
    kind = str(action.get("kind") or action.get("type") or "").lower()
    if kind in ("select", "select-one", "dropdown"):
        return "select_option"
    if kind == "combobox":
        return "pick_custom_dropdown"
    if kind in ("radio", "checkbox"):
        return "choose_radio_or_checkbox"
    if kind == "file":
        return "upload_file"
    return "fill_text"


def _public(value):
    if isinstance(value, dict):
        hidden_value = _is_password(value)
        cleaned = {}
        for key, item in value.items():
            if key in _HIDDEN or (hidden_value and key == "value"):
                continue
            cleaned[key] = _public(item)
        return cleaned
    if isinstance(value, list):
        return [_public(item) for item in value]
    if isinstance(value, str) and len(value) > 500:
        return value[:500]
    return value


def _is_password(value):
    label = str(value.get("label") or "").lower()
    kind = str(value.get("kind") or value.get("type") or "").lower()
    return kind == "password" or "password" in label or "passcode" in label


def _exchange(payload):
    sys.stdout.write(json.dumps(payload) + "\n")
    sys.stdout.flush()
    line = sys.stdin.readline()
    if not line:
        raise RuntimeError("The application closed the agent.")
    return json.loads(line)
