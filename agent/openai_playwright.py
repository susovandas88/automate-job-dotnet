import json
import os
import re
from contextvars import ContextVar

from agents import Agent, ModelSettings, Runner, function_tool

from agent.llm_provider import gemini_base, gemini_key, gemini_model, openai_enabled, provider_name

from agent.guard import choose_action, guard_fields, is_password, normalize_question
from agent.protocol import call_host
from agent.runlog import describe_actions


def describe_field(field):
    parts = [f"key {field.get('key')}"]
    if field.get("label"):
        parts.append("label: " + str(field.get("label")))
    if field.get("group"):
        parts.append("group: " + str(field.get("group")))
    if field.get("placeholder"):
        parts.append("placeholder: " + str(field.get("placeholder")))
    if field.get("name"):
        parts.append("name: " + str(field.get("name")))
    if field.get("tag"):
        parts.append("tag: " + str(field.get("tag")))
    parts.append("type: " + str(field.get("kind") or field.get("type") or "text"))
    if field.get("required"):
        parts.append("required")
    options = field.get("options") or []
    if options:
        parts.append("options: " + " | ".join(str(option) for option in options))
    current = field.get("currentValue") or field.get("current_value") or ""
    if current and str(field.get("kind") or "").lower() != "password":
        parts.append("current value: " + str(current))
    if field.get("visual"):
        parts.append("label is visual; call screenshot_for_review")
    return "; ".join(parts)


def describe_fields(fields):
    lines = [describe_field(field) for field in fields or []]
    return "\n".join(lines) if lines else "No fields."

_bridge: ContextVar["PlaywrightBridge"] = ContextVar("playwright_bridge")

INSTRUCTIONS = """
Fill one company job application one page at a time. On every page follow this cycle:
1. Call get_form_fields().
2. Fill only the fields you can map confidently from the resume, saved application details, or the answers bank.
3. Call get_form_fields() again. Compare each current value with the value you intended. If a masked phone or a dropdown did not keep the value, fill that field once more.
4. Call read_page_text() and fix validation errors you can map confidently.
5. Only then call next_or_submit for Sign in with email, Next, Continue, or Review.
6. Repeat. A new answer, including Yes, can reveal more fields, so read the page again after every fill.

If a label is only an image, call screenshot_for_review. That sends the image to the vision model.
If the page is a CAPTCHA or a sign-in wall, stop. Do not solve a CAPTCHA and do not type a password.
If the form is a canvas or has no readable fields, stop for the computer-use fallback.
If a field does not match the resume, saved details, or answers bank, call report_unmapped(field_id, label, reason). Do not guess.
Use fill_text for text, email, telephone, and textarea fields.
Use select_option for a native select, matching the option text.
Use choose_radio_or_checkbox for one radio or checkbox.
Use pick_custom_dropdown for a combobox.
Use upload_file only with which set to resume or cover_letter. There is no file path argument.
Do not click Submit, Create account, Sign in with Apple, or Sign in with Google.
Do not type a password. Do not invent phone numbers, emails, years, visa status, salary, or dates.
Call finish when the page is filled or the remaining button would submit it.
""".strip()


class PlaywrightBridge:
    def __init__(self, resume, profile, log):
        self.resume = resume or ""
        self.profile = profile or {}
        self.log = log
        self.filled = []
        self.blank = []
        self.fields = []
        self.buttons = []
        self.signature = ""
        self.model_error = ""
        self.advanced = False
        self.answers = []
        self.unmapped = []
        self.intended = {}
        self.fallback = ""
        self.hint = ""
        self.url = ""
        self.wall = ""
        self.surface = ""
        self.last_image = ""

    def read_page(self):
        page = call_host({"tool": "observe", "includeScreenshot": False})
        self.fields = page.get("fields") or []
        self.buttons = page.get("buttons") or []
        self.url = page.get("url") or self.url
        self.wall = page.get("wall") or ""
        self.surface = page.get("surface") or ""
        self.last_image = page.get("screenshot") or ""
        self.log(
            f"playwright read fields=[{', '.join(field.get('label') or '' for field in self.fields) or 'none'}] "
            f"buttons=[{', '.join(self.buttons) or 'none'}]"
        )
        return page

    def observe(self):
        page = self.read_page()
        signature = "|".join(f"{field.get('key')}:{field.get('label')}" for field in self.fields)
        signature += "#" + "|".join(self.buttons)
        self.advanced = False
        stuck = signature == self.signature and self.signature != ""
        self.signature = signature
        return page, stuck

    def fill(self, actions):
        typed = [action for action in actions if action.get("value")]
        if typed:
            call_host({"tool": "fill", "actions": typed})
        self.log("playwright type " + describe_actions(actions))

    def click_advance(self):
        call_host({"tool": "click_advance"})
        self.advanced = True
        self.log("playwright open next page")

    def click_apply(self):
        result = call_host({"tool": "click_apply"})
        opened = bool(result.get("ok"))
        self.log("playwright click apply " + ("ok" if opened else "not found"))
        return opened

    def call_tool(self, payload):
        result = call_host(payload)
        self.last_image = result.get("image") or self.last_image
        message = result.get("message") or ""
        self.log("playwright " + payload.get("tool", "") + " " + message)
        if payload.get("tool") == "next_or_submit" and message.startswith("Clicked"):
            self.advanced = True
        return message

    def field(self, field_id):
        wanted = str(field_id)
        for field in self.fields:
            if str(field.get("key") or "") == wanted:
                return field
        return None

    def report(self, field_id, label, reason):
        text = (label or "Field").strip() or "Field"
        key = normalize_question(text)
        if any(normalize_question(item.get("label") or "") == key for item in self.unmapped):
            return
        self.unmapped.append({"fieldId": str(field_id or ""), "label": text, "reason": (reason or "No saved answer.").strip()})
        if text not in self.blank:
            self.blank.append(text)
        self.log("unmapped " + text + " " + (reason or ""))

    def record(self, actions):
        for action in actions:
            label = action.get("label") or "Field"
            value = action.get("value")
            if value:
                line = f"{label}: {value}"
                if line not in self.filled:
                    self.filled.append(line)
            elif label not in self.blank:
                self.blank.append(label)


def run_with_playwright_tools(resume, profile, log, answers=None):
    bridge = PlaywrightBridge(resume, profile, log)
    bridge.answers = answers or []
    token = _bridge.set(bridge)
    try:
        for _ in range(8):
            _, stuck = bridge.observe()
            if bridge.wall == "captcha":
                bridge.hint = "This page is a CAPTCHA. It needs you. The filler does not solve CAPTCHAs."
                bridge.report("page", "Security check", bridge.hint)
                log("stop because a CAPTCHA wall is on the page")
                break
            if route_canvas(bridge):
                break
            if stuck:
                log("stop because the page did not change")
                break

            seen = {str(field.get("key")) for field in bridge.fields}
            for _pass in range(3):
                intended = fill_known(bridge)
                if intended:
                    missed = verify_fields(bridge, intended)
                    if missed:
                        apply_actions(bridge, missed)
                        for action in verify_fields(bridge, missed):
                            bridge.report(action.get("key"), action.get("label"), "The page did not keep the typed value.")
                grown = {str(field.get("key")) for field in bridge.fields}
                if grown == seen:
                    break
                seen = grown
                log("re-read fields after a fill revealed more controls")

            if any(field.get("visual") for field in bridge.fields):
                review_screenshot(bridge)

            page_text = bridge.call_tool({"tool": "read_page_text"})
            hint = ats_hint(bridge.url, bridge.buttons, bridge.wall)
            if hint:
                bridge.hint = hint
            blanks = unmapped_fields(bridge)
            if blanks or page_has_errors(page_text):
                ask_agent(bridge, blanks, page_text)
            for field in unmapped_fields(bridge):
                bridge.report(field.get("key"), field.get("label"), "No matching answer in the resume, application details, or answers bank.")

            if bridge.wall in ("login", "account"):
                bridge.report("page", "Account sign-in", bridge.hint or "This page needs you to sign in. The password is not stored.")
                log("stop because the page needs you to sign in")
                break
            if not bridge.fields and choose_action(bridge.buttons) == "advance":
                bridge.click_advance()
                continue
            if not bridge.fields and auth_chooser(bridge.buttons):
                bridge.hint = ats_hint(bridge.url, bridge.buttons, "account")
                bridge.report("page", "Account sign-in", bridge.hint)
                log("stop because the page is an account or sign-in chooser")
                break
            if not bridge.fields and bridge.click_apply():
                continue
            if bridge.advanced:
                continue
            if required_still_open(bridge):
                log("stop because a required field needs you")
                break
            if choose_action(bridge.buttons) == "advance":
                bridge.click_advance()
                continue
            break
        return bridge
    finally:
        _bridge.reset(token)


def fill_known(bridge):
    guarded = guard_fields(bridge.fields, [], bridge.resume, bridge.profile, bridge.answers)
    bridge.record(guarded)
    intended = [action for action in guarded if action.get("value")]
    for action in intended:
        bridge.intended[str(action.get("key"))] = action.get("value")
    apply_actions(bridge, intended)
    return intended


def apply_actions(bridge, actions):
    text_actions = []
    for action in actions:
        if not action.get("value"):
            continue
        kind = (action.get("kind") or "").lower()
        if kind == "select":
            bridge.call_tool({"tool": "select_option", "fieldId": action.get("key"), "optionText": action["value"]})
        elif kind in ("radio", "checkbox"):
            bridge.call_tool({"tool": "choose_radio_or_checkbox", "fieldId": action.get("key"), "checked": True})
        elif kind == "combobox":
            bridge.call_tool({"tool": "pick_custom_dropdown", "fieldId": action.get("key"), "optionText": action["value"]})
        elif kind == "file":
            continue
        else:
            text_actions.append(action)
    if text_actions:
        bridge.fill(text_actions)


def verify_fields(bridge, intended):
    if not intended:
        return []
    bridge.read_page()
    current = {str(field.get("key")): field.get("currentValue") or "" for field in bridge.fields}
    missed = []
    for action in intended:
        if not values_match(action.get("value"), current.get(str(action.get("key")), "")):
            missed.append(action)
            bridge.log("verify missed " + (action.get("label") or ""))
    return missed


def values_match(intended, current):
    wanted = (intended or "").strip().lower()
    seen = (current or "").strip().lower()
    if not wanted:
        return True
    if not seen:
        return False
    if wanted in seen or seen in wanted:
        return True
    wanted_digits = "".join(character for character in wanted if character.isdigit())
    seen_digits = "".join(character for character in seen if character.isdigit())
    return len(wanted_digits) >= 4 and wanted_digits == seen_digits


def unmapped_fields(bridge):
    guarded = guard_fields(bridge.fields, [], bridge.resume, bridge.profile, bridge.answers)
    reported = {normalize_question(item.get("label") or "") for item in bridge.unmapped}
    blanks = []
    for action in guarded:
        label = action.get("label") or ""
        kind = (action.get("kind") or "").lower()
        if action.get("value") or is_password(label) or kind == "password":
            continue
        if normalize_question(label) in reported:
            continue
        blanks.append(action)
    return blanks


def required_still_open(bridge):
    reported = {normalize_question(item.get("label") or "") for item in bridge.unmapped}
    for field in bridge.fields:
        if not field.get("required"):
            continue
        label = field.get("label") or ""
        kind = (field.get("kind") or "").lower()
        if is_password(label) or kind == "password":
            continue
        if (field.get("currentValue") or "").strip():
            continue
        if normalize_question(label) in reported:
            return True
    return False


def ats_hint(url, buttons, wall):
    if wall not in ("login", "account"):
        return ""
    host = (url or "").lower()
    if "myworkdayjobs" in host or "workday" in host:
        return "Workday needs you to sign in or create the account in this window. The password is not stored. After you are signed in, click Fill with LangGraph again."
    return "This page needs you to sign in or create the account in the open window. The password is not stored. Then click Fill with LangGraph again."


def auth_chooser(buttons):
    labels = " ".join(buttons or []).lower()
    return "create account" in labels or "sign in" in labels


def text_is_known(bridge, value):
    from agent.guard import fact_sheet

    text = (value or "").strip().lower()
    if not text:
        return False
    facts = "\n".join(part for part in [bridge.resume or "", fact_sheet(bridge.profile)] if part).lower()
    return text in facts


def review_screenshot(bridge, purpose="labels"):
    result = call_host({"tool": "screenshot_for_review"})
    bridge.last_image = result.get("image") or ""
    message = result.get("message") or ""
    bridge.log("playwright screenshot_for_review")
    image = bridge.last_image
    base = os.environ.get("OPENAI_BASE_URL") or ""
    ready = gemini_key() if not openai_enabled() else os.environ.get("OPENAI_API_KEY", "").startswith("sk-")
    if not image or not ready or base.startswith("http://127.0.0.1:9"):
        return message
    prompt = (
        "List each visible form label, including labels that are pictures. Do not invent values. If this is a CAPTCHA, say CAPTCHA."
        if purpose == "labels"
        else "This form has no readable fields. Reply with JSON only: "
        "{\"action\":\"stop\"} or {\"action\":\"click\",\"x\":0,\"y\":0} or "
        "{\"action\":\"type\",\"x\":0,\"y\":0,\"value\":\"known text\"}. "
        "Never click Submit, Create account, Apple, Google, or a CAPTCHA. Never type a password. "
        "Only type text that appears in this resume:\n" + (bridge.resume or "")[:4000]
    )
    try:
        seen = describe_image(image, prompt)
        bridge.log("vision read the screenshot")
        if "captcha" in seen.lower() and purpose == "labels":
            bridge.wall = "captcha"
        return message + "\nVisual labels:\n" + seen[:1500]
    except Exception as exception:
        bridge.log("vision failed " + str(exception)[:200])
        return message


def parse_visual_action(text):
    match = re.search(r"\{.*\}", text or "", re.DOTALL)
    if not match:
        return None
    try:
        data = json.loads(match.group(0))
    except json.JSONDecodeError:
        return None
    if not isinstance(data, dict) or data.get("action") not in ("click", "type", "stop"):
        return None
    return data


def describe_image(image, prompt):
    from openai import OpenAI

    if openai_enabled():
        client = OpenAI(timeout=30, max_retries=0)
        model = os.environ.get("OPENAI_MODEL") or "gpt-4.1"
    else:
        client = OpenAI(api_key=gemini_key(), base_url=gemini_base(), timeout=30, max_retries=0)
        model = gemini_model()
    if os.environ.get("LANGSMITH_TRACING") == "true":
        try:
            from langsmith.wrappers import wrap_openai

            client = wrap_openai(client)
        except Exception:
            pass
    response = client.chat.completions.create(
        model=model,
        temperature=0.2,
        messages=[{
            "role": "user",
            "content": [
                {"type": "text", "text": prompt},
                {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + image}},
            ],
        }],
    )
    return (response.choices[0].message.content or "").strip()


def canvas_route(surface, fields):
    return surface == "canvas" and not fields


def route_canvas(bridge):
    if not canvas_route(bridge.surface, bridge.fields):
        return False
    bridge.fallback = "cua"
    bridge.log("canvas form routed to computer-use fallback")
    description = review_screenshot(bridge, "canvas")
    if bridge.wall == "captcha":
        bridge.hint = "This page is a CAPTCHA. It needs you. The filler does not solve CAPTCHAs."
        bridge.report("page", "Security check", bridge.hint)
        return True
    action = parse_visual_action(description)
    if action and action.get("action") == "type" and text_is_known(bridge, action.get("value") or ""):
        bridge.call_tool({
            "tool": "click_point",
            "x": int(action.get("x") or 0),
            "y": int(action.get("y") or 0),
            "value": action.get("value") or "",
        })
    elif action and action.get("action") == "click":
        bridge.call_tool({"tool": "click_point", "x": int(action.get("x") or 0), "y": int(action.get("y") or 0)})
    bridge.hint = "This form is drawn on a canvas, so the field tools cannot fill it. The computer-use fallback looked at the screenshot and stopped for you. Finish it in the open window."
    if description:
        bridge.hint += " " + description[:400]
    bridge.report("page", "Canvas form", "DOM tools cannot read a canvas or custom form.")
    return True


def page_has_errors(text):
    messages = (text or "").lower()
    if "messages:" not in messages:
        return False
    detail = messages.split("messages:", 1)[1].strip()
    return any(word in detail for word in ("invalid", "required", "error", "must"))


def ask_agent(bridge, blanks, page_text=""):
    labels = "\n".join(describe_field(field) for field in blanks) or "None."
    prompt = (
        f"Resume:\n{bridge.resume[:12000]}\n\n"
        f"Saved application details:\n{json.dumps(bridge.profile)}\n\n"
        f"Answers bank:\n{json.dumps(bridge.answers)}\n\n"
        f"Page text:\n{page_text}\n\n"
        f"Fields you could not map:\n{labels}\n"
        "Follow get_form_fields, fill, get_form_fields again, then read_page_text. "
        "Call report_unmapped for anything you cannot map. Do not guess. "
        "Click next only after the current values match."
    )
    name = provider_name()
    if not openai_enabled() and not gemini_key():
        bridge.model_error = "OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in agent/.env."
        bridge.log(name + " agent error " + bridge.model_error)
        return
    try:
        bridge.log(name + " agent started for blank fields")
        Runner.run_sync(build_agent(), prompt, max_turns=8)
        bridge.log(name + " agent finished")
    except Exception as exception:
        bridge.model_error = str(exception)[:300]
        bridge.log(f"{name} agent error {bridge.model_error}")


def build_agent():
    if openai_enabled():
        model = os.environ.get("OPENAI_MODEL") or "gpt-4.1"
    else:
        from agents import OpenAIChatCompletionsModel
        from openai import AsyncOpenAI

        model = OpenAIChatCompletionsModel(
            model=gemini_model(),
            openai_client=AsyncOpenAI(api_key=gemini_key(), base_url=gemini_base(), timeout=60, max_retries=0),
        )
    return Agent(
        name="Application filler",
        instructions=INSTRUCTIONS,
        model=model,
        model_settings=ModelSettings(temperature=0.2),
        tools=[
            get_form_fields,
            fill_text,
            select_option,
            choose_radio_or_checkbox,
            pick_custom_dropdown,
            upload_file,
            next_or_submit,
            read_page_text,
            screenshot_for_review,
            report_unmapped,
            finish,
        ],
    )


@function_tool
def get_form_fields() -> str:
    """Read the visible form. Returns each field id, label, type, options, and current value."""
    bridge = _bridge.get()
    page = bridge.read_page()
    fields = page.get("fields") or []
    buttons = page.get("buttons") or []
    return "Fields:\n" + describe_fields(fields) + "\nButtons: " + ", ".join(buttons)


def known_value(bridge, field, value):
    if not field or not isinstance(value, str) or not value.strip():
        return None
    guarded = guard_fields(
        [field],
        [{"key": field.get("key"), "label": field.get("label"), "value": value}],
        bridge.resume,
        bridge.profile,
        bridge.answers,
    )
    return guarded[0].get("value")


def blocked_button(button_text):
    text = (button_text or "").strip().lower()
    return "submit" in text or "apple" in text or "google" in text or "create account" in text


@function_tool
async def fill_text(field_id: str, value: str) -> str:
    """Type into a text, email, telephone, or textarea field."""
    bridge = _bridge.get()
    field = bridge.field(field_id)
    if field is None:
        return "Field " + str(field_id) + " was not found."
    kind = (field.get("kind") or "").lower()
    if is_password(field.get("label") or "") or kind == "password":
        bridge.log("playwright left the password blank")
        return "Password fields are left blank."
    kept = known_value(bridge, field, value)
    if not kept:
        return "That value is not in the resume or saved details."
    bridge.record([{**field, "value": kept}])
    return bridge.call_tool({"tool": "fill_text", "fieldId": field_id, "value": kept})


@function_tool
async def select_option(field_id: str, option_text: str) -> str:
    """For a native select. Fuzzy-match option_text to the available options."""
    bridge = _bridge.get()
    field = bridge.field(field_id)
    if field is None:
        return "Field " + str(field_id) + " was not found."
    options = [str(option) for option in field.get("options") or []]
    wanted = (option_text or "").strip().lower()
    match = next((option for option in options if option.strip().lower() == wanted), None)
    if match is None:
        match = next((option for option in options if wanted and wanted in option.lower()), None)
    kept = known_value(bridge, field, match or "")
    if match is None or not kept or kept.strip().lower() != match.strip().lower():
        return "That option is not in the resume or saved details."
    bridge.record([{**field, "value": match}])
    return bridge.call_tool({"tool": "select_option", "fieldId": field_id, "optionText": match})


@function_tool
async def choose_radio_or_checkbox(field_id: str, checked: bool) -> str:
    """Check or uncheck one radio or checkbox. field_id is that choice, not the whole group."""
    bridge = _bridge.get()
    field = bridge.field(field_id)
    if field is None:
        return "Field " + str(field_id) + " was not found."
    choice = field.get("label") or ""
    kept = known_value(bridge, field, choice)
    if checked and (not kept or kept.strip().lower() != choice.strip().lower()):
        return "That choice is not in the resume or saved details."
    if checked:
        bridge.record([{**field, "value": field.get("label") or "checked"}])
    return bridge.call_tool({"tool": "choose_radio_or_checkbox", "fieldId": field_id, "checked": bool(checked)})


@function_tool
async def pick_custom_dropdown(field_id: str, option_text: str) -> str:
    """For a combobox. Click it, type to filter, then click the matching option."""
    bridge = _bridge.get()
    field = bridge.field(field_id)
    if field is None:
        return "Field " + str(field_id) + " was not found."
    kept = known_value(bridge, field, option_text)
    if not kept or kept.strip().lower() != (option_text or "").strip().lower():
        return "That option is not in the resume or saved details."
    bridge.record([{**field, "value": option_text.strip()}])
    return bridge.call_tool({"tool": "pick_custom_dropdown", "fieldId": field_id, "optionText": option_text.strip()})


@function_tool
async def upload_file(field_id: str, which: str) -> str:
    """Attach resume or cover_letter. The file comes from a fixed allowlist, never from a path you supply."""
    bridge = _bridge.get()
    choice = (which or "").strip().lower()
    if choice not in ("resume", "cover_letter"):
        return "Upload skipped. which must be resume or cover_letter."
    return bridge.call_tool({"tool": "upload_file", "fieldId": field_id, "which": choice})


@function_tool
async def next_or_submit(button_text: str) -> str:
    """Click the named button. Submit, Create account, Apple, and Google are refused."""
    bridge = _bridge.get()
    if blocked_button(button_text):
        bridge.log("playwright refused " + (button_text or ""))
        return "Refused to click " + (button_text or "") + ". Submit stays for you."
    return bridge.call_tool({"tool": "next_or_submit", "buttonText": button_text})


@function_tool
async def read_page_text() -> str:
    """Read headings and error messages on the visible page."""
    return _bridge.get().call_tool({"tool": "read_page_text"})


@function_tool
async def screenshot_for_review() -> str:
    """Look at the page when a label is visual or the layout is unclear. Sends the image to the vision model."""
    return review_screenshot(_bridge.get())


@function_tool
async def report_unmapped(field_id: str, label: str, reason: str) -> str:
    """Record a field that does not match the resume, saved details, or answers bank. Do not guess a value."""
    bridge = _bridge.get()
    bridge.report(field_id, label, reason)
    return "Recorded. This job needs you to answer it. Do not guess."


@function_tool
def finish() -> str:
    """Stop so the user can submit the application."""
    bridge = _bridge.get()
    bridge.log("OpenAI agent finished the page")
    return "Stopped before submit."
