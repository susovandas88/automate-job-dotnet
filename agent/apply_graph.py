import json
import sys

from agent.guard import guard_fields, is_password
from agent.protocol import call_host
from agent.openai_playwright import PlaywrightBridge, _bridge, ask_agent
from agent.runlog import RunLog, redact
from agent.tracing import enable_langsmith, flush_langsmith


def note_for(bridge):
    if getattr(bridge, "hint", ""):
        return redact(bridge.hint)
    if bridge.unmapped:
        labels = ", ".join(item.get("label") or "Field" for item in bridge.unmapped)
        return redact("This job needs you. Answer these, then fill again: " + labels + ". Submit stays for you.")
    message = "The LangGraph agent filled the company form. Submit it in that tab if you want to send it."
    if bridge.model_error:
        from agent.llm_provider import provider_name

        message = provider_name() + " could not read one page. Saved application details were still typed where the labels matched. Submit it in that tab if you want to send it."
    return redact(message)


class HostSession:
    def __init__(self, bridge):
        self.bridge = bridge

    def observe(self):
        page, _stuck = self.bridge.observe()
        return page

    def fill(self, actions):
        if self.bridge.wall == "captcha":
            return
        self.bridge.fill(actions)

    def click_advance(self):
        if self.bridge.wall == "captcha":
            return
        self.bridge.click_advance()

    def click_apply(self):
        if self.bridge.wall == "captcha":
            return False
        return self.bridge.click_apply()

    def fill_experience(self):
        from agent.experience import parse_jobs

        if self.bridge.wall == "captcha":
            return
        jobs = parse_jobs(self.bridge.profile.get("workHistory") or "")
        if jobs:
            self.bridge.call_tool({"tool": "fill_experience", "jobs": jobs})

    def click_named(self, name):
        if self.bridge.wall == "captcha":
            return False
        message = self.bridge.call_tool({"tool": "click_named", "buttonText": name})
        return str(message).startswith("Clicked")

    def open_gate(self):
        if self.bridge.wall == "captcha":
            return {"opened": False, "waiting": False}
        result = call_host({"tool": "open_gate"})
        return {"opened": bool(result.get("opened")), "waiting": bool(result.get("waiting"))}

    def tab_open(self):
        result = call_host({"tool": "tab_open"})
        return bool(result.get("open"))

    def reopen_attempt(self, attempt):
        result = call_host({"tool": "reopen_attempt", "attempt": attempt})
        return bool(result.get("ok"))

    def wait_for_human(self):
        result = call_host({"tool": "wait_for_human"})
        return bool(result.get("solved"))


def result_from(state):
    class Result:
        pass

    result = Result()
    result.filled = list(dict.fromkeys(state.get("filled") or []))
    result.model_error = state.get("model_error") or ""
    result.hint = ""
    result.blank = []
    result.unmapped = []
    seen = set()
    for label in state.get("blank") or []:
        if label in seen:
            continue
        seen.add(label)
        result.blank.append(label)
        if not is_password(label):
            result.unmapped.append({
                "label": label,
                "reason": "No matching answer in the resume, application details, or answers bank.",
            })
    return result


def run_graph(graph, payload, session, log):
    from langgraph.types import Command

    config = {
        "run_name": "fill-company-form",
        "recursion_limit": 250,
        "configurable": {"thread_id": "fill-company-form"},
    }
    graph.invoke(payload, config)
    while True:
        snapshot = graph.get_state(config)
        if not snapshot.interrupts:
            return snapshot.values
        log("CAPTCHA is on the form; waiting for you")
        solved = False
        waiter = getattr(session, "wait_for_human", None)
        if waiter:
            solved = bool(waiter())
        graph.invoke(Command(resume=solved), config)


def main():
    setup = json.loads(sys.stdin.readline() or "{}")
    run_log = RunLog(setup.get("logPath"))
    tracing = enable_langsmith()
    run_log.write("LangGraph run started")
    run_log.write("LangSmith tracing on project automate-job" if tracing else "LangSmith tracing off")
    profile = dict(setup.get("profile") or {})
    profile["answers"] = setup.get("answers") or profile.get("answers") or []
    bridge = PlaywrightBridge(setup.get("resume") or "", profile, run_log.write)
    bridge.answers = profile["answers"]

    def decide(state):
        fields = state.get("fields") or []
        if not fields:
            return []
        guarded = guard_fields(fields, [], state.get("resume") or "", state.get("profile") or {})
        blanks = [
            action for action in guarded
            if not action.get("value")
            and not is_password(action.get("label") or "")
            and (action.get("kind") or "").lower() != "password"
        ]
        if blanks:
            ask_agent(bridge, blanks, "")
        return []

    from agent.graph import build_apply_graph
    from langgraph.checkpoint.memory import MemorySaver

    session = HostSession(bridge)
    graph = build_apply_graph(session, decide, run_log.write, checkpointer=MemorySaver())
    payload = {
        "step": 0,
        "resume": bridge.resume,
        "profile": profile,
        "filled": [],
        "blank": [],
        "signature": "",
    }
    token = _bridge.set(bridge)
    try:
        if tracing:
            from langchain_core.tracers.context import tracing_v2_enabled

            with tracing_v2_enabled(project_name="automate-job") as tracer:
                final = run_graph(graph, payload, session, run_log.write)
                tracer.client.flush()
        else:
            final = run_graph(graph, payload, session, run_log.write)
    finally:
        _bridge.reset(token)
        flush_langsmith()
    outcome = result_from(final)
    outcome.model_error = outcome.model_error or bridge.model_error
    if bridge.wall == "captcha":
        outcome.hint = "A CAPTCHA is showing in your Chrome window. Solve it there, then click Fill again. It needs you."
        outcome.unmapped.append({"label": "Security check", "reason": outcome.hint})
    run_log.write("finished " + note_for(outcome))
    sys.stdout.write(json.dumps({
        "tool": "done",
        "status": "Needs you" if outcome.unmapped else "Filled",
        "filled": outcome.filled,
        "blank": outcome.blank,
        "unmapped": outcome.unmapped,
        "note": note_for(outcome),
    }) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        sys.stderr.write(redact(str(exception)) + "\n")
        sys.exit(1)
