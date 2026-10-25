import time
from typing import TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.types import interrupt

from agent.experience import experience_needed
from agent.guard import choose_action, guard_fields, manual_apply
from agent.runlog import describe_actions


def application_step(buttons):
    text = " ".join(buttons or []).lower()
    return "back to job posting" in text


def application_fields(fields):
    kept = []
    for field in fields or []:
        label = (field.get("label") or "").lower()
        if any(part in label for part in ("rechercher", "search", "dark mode", "cookie")):
            continue
        kept.append(field)
    return kept


def redirect_button(buttons):
    import re

    for text in buttons or []:
        label = (text or "").strip()
        if not label or re.search(r"favorite|search|dark mode|cookie|rechercher", label, re.IGNORECASE):
            continue
        if re.search(r"easy apply|apply manually|apply now|^apply$|start (your )?application|continue to application", label, re.IGNORECASE):
            return label
    return ""


class ApplyState(TypedDict, total=False):
    step: int
    resume: str
    profile: dict
    fields: list
    buttons: list
    screenshot: str
    signature: str
    stuck: bool
    proposed: list
    actions: list
    filled: list
    blank: list
    route: str
    model_error: str
    attempt: int
    gate_waiting: bool


def build_apply_graph(session, decide, write=None, checkpointer=None):
    def log(message):
        if write:
            write(message)

    def observe(state: ApplyState) -> dict:
        page = session.observe()
        waiting_on_captcha = False
        if page.get("wall") == "captcha":
            log("CAPTCHA is on the form; waiting for you")
            answered = interrupt({
                "kind": "captcha",
                "message": "A CAPTCHA is on this form. Solve it in Chrome, then continue.",
            })
            if answered is False or str(answered).strip().lower() == "false":
                log("CAPTCHA still needs you")
                waiting_on_captcha = True
            else:
                page = session.observe()
                waiting_on_captcha = page.get("wall") == "captcha"
                if waiting_on_captcha:
                    log("CAPTCHA is still on the form")
        fields = application_fields(page.get("fields") or [])
        buttons = page.get("buttons") or []
        signature = "|".join(f"{field.get('key')}:{field.get('label')}" for field in fields)
        signature += "#" + "|".join(buttons)
        step = state.get("step", 0) + 1
        stuck = waiting_on_captcha or (signature == state.get("signature") and step > 1)
        log(
            f"step {step} observe fields=[{', '.join(field.get('label') or '' for field in fields) or 'none'}] "
            f"buttons=[{', '.join(buttons) or 'none'}] stuck={stuck}"
        )
        return {
            "step": step,
            "fields": fields,
            "buttons": buttons,
            "screenshot": page.get("screenshot") or "",
            "signature": signature,
            "stuck": stuck,
        }

    def decide_node(state: ApplyState) -> dict:
        if state.get("stuck") or state.get("step", 0) > 8 or not state.get("fields"):
            log(f"step {state.get('step')} decide skipped")
            return {"proposed": [], "screenshot": ""}
        try:
            proposed = decide(state)
            log(f"step {state.get('step')} decide {describe_actions(proposed)}")
            return {"proposed": proposed, "screenshot": ""}
        except Exception as exception:
            log(f"step {state.get('step')} decide error {exception}")
            return {"proposed": [], "model_error": str(exception)[:300], "screenshot": ""}

    def guard(state: ApplyState) -> dict:
        actions = guard_fields(
            state.get("fields") or [],
            state.get("proposed") or [],
            state.get("resume") or "",
            state.get("profile") or {},
        )
        log(f"step {state.get('step')} guard {describe_actions(actions)}")
        return {"actions": actions}

    def act(state: ApplyState) -> dict:
        if state.get("stuck") or state.get("step", 0) > 8:
            log(f"step {state.get('step')} stop because the page did not change")
            return {
                "filled": list(state.get("filled") or []),
                "blank": list(state.get("blank") or []),
                "route": "stop",
                "screenshot": "",
            }

        filled = list(state.get("filled") or [])
        blank = list(state.get("blank") or [])
        for action in state.get("actions") or []:
            if action.get("keep"):
                continue
            label = action.get("label") or "Field"
            value = action.get("value")
            if not value:
                if "extension" not in label.lower():
                    blank.append(label)
            else:
                filled.append(f"{label}: {value}")

        typed = [action for action in state.get("actions") or [] if action.get("value")]
        if typed:
            session.fill(typed)

        route = "stop"
        manual = manual_apply(state.get("buttons") or [])
        redirect = redirect_button(state.get("buttons") or [])
        if state.get("stuck") or state.get("step", 0) > 8:
            route = "stop"
        elif experience_needed(state.get("fields") or [], state.get("buttons") or [], state.get("profile") or {}):
            session.fill_experience()
            route = "observe"
        elif not state.get("fields") and manual:
            session.click_named(manual)
            route = "observe"
        elif not state.get("fields") and application_step(state.get("buttons") or []):
            route = "stop"
        elif not state.get("fields") and redirect:
            session.click_named(redirect)
            route = "observe"
        elif not state.get("fields") and session.click_apply():
            route = "observe"
        elif choose_action(state.get("buttons") or []) == "advance":
            session.click_advance()
            route = "observe"

        log(f"step {state.get('step')} act route={route}")
        return {"filled": filled, "blank": blank, "route": route, "screenshot": ""}

    def open_attempt(state: ApplyState) -> dict:
        attempt = int(state.get("attempt") or 1)
        reopen = getattr(session, "reopen_attempt", None)
        if reopen:
            reopen(attempt)
        log(f"attempt {attempt} open form")
        return {"attempt": attempt, "step": 0, "signature": "", "stuck": False}

    def gate(state: ApplyState) -> dict:
        opener = getattr(session, "open_gate", None)
        if not opener:
            return {"route": "finish", "gate_waiting": False}
        result = opener() or {}
        if result.get("opened"):
            log("gate opened the application form")
            return {
                "route": "observe",
                "step": 0,
                "signature": "",
                "stuck": False,
                "blank": [],
                "gate_waiting": False,
            }
        waiting = bool(result.get("waiting"))
        log(f"gate waiting={waiting}")
        return {"route": "finish", "gate_waiting": waiting}

    def finish(state: ApplyState) -> dict:
        pending = [
            label for label in (state.get("blank") or [])
            if "password" not in (label or "").lower() and "extension" not in (label or "").lower()
        ]
        complete = not state.get("stuck") and not pending and not state.get("gate_waiting") and bool(state.get("fields"))
        if complete:
            tab = getattr(session, "tab_open", None)
            if tab and tab():
                log("form filled; Chrome tab is still open")
                time.sleep(2)
                return {"route": "observe", "step": 0, "signature": "", "stuck": False, "blank": []}
            log("form filled; Chrome tab is closed")
            return {"route": "stop"}

        attempt = int(state.get("attempt") or 1)
        reopen = getattr(session, "reopen_attempt", None)
        if attempt < 3 and reopen and reopen(attempt + 1):
            log(f"attempt {attempt} unfinished; opening attempt {attempt + 1}")
            return {
                "route": "observe",
                "attempt": attempt + 1,
                "step": 0,
                "signature": "",
                "stuck": False,
                "blank": [],
            }
        log(f"attempt {attempt} unfinished; keeping this result")
        return {"route": "stop"}

    builder = StateGraph(ApplyState)
    builder.add_node("open_attempt", open_attempt)
    builder.add_node("observe", observe)
    builder.add_node("decide", decide_node)
    builder.add_node("guard", guard)
    builder.add_node("act", act)
    builder.add_node("gate", gate)
    builder.add_node("finish", finish)
    builder.add_edge(START, "open_attempt")
    builder.add_edge("open_attempt", "observe")
    builder.add_edge("observe", "decide")
    builder.add_edge("decide", "guard")
    builder.add_edge("guard", "act")
    builder.add_conditional_edges(
        "act",
        lambda state: "observe" if state.get("route") == "observe" else "gate",
        {"observe": "observe", "gate": "gate"},
    )
    builder.add_conditional_edges(
        "gate",
        lambda state: state.get("route") or "finish",
        {"observe": "observe", "finish": "finish"},
    )
    builder.add_conditional_edges(
        "finish",
        lambda state: state.get("route") or "stop",
        {"observe": "observe", "stop": END},
    )
    return builder.compile(checkpointer=checkpointer)
