import unittest

from agent.graph import build_apply_graph
from agent.guard import choose_action, guard_fields


class RecordingSession:
    def __init__(self, pages):
        self.pages = list(pages)
        self.fills = []
        self.clicks = []

    def observe(self):
        if len(self.pages) > 1:
            return self.pages.pop(0)
        return self.pages[0]

    def fill(self, actions):
        self.fills.append(actions)

    def click_advance(self):
        self.clicks.append("advance")

    def click_apply(self):
        self.clicks.append("apply")
        return False

    def fill_experience(self):
        self.clicks.append("experience")

    def click_named(self, name):
        self.clicks.append(name)
        return True


class ApplyGraphTests(unittest.TestCase):
    def test_saved_email_fills_both_boxes_and_submit_is_not_clicked(self):
        session = RecordingSession([{
            "fields": [
                {"key": "0", "label": "Email Address", "kind": "text", "options": []},
                {"key": "1", "label": "Retype Email Address", "kind": "text", "options": []},
                {"key": "2", "label": "Choose Password", "kind": "password", "options": []},
            ],
            "buttons": ["Create Account"],
            "screenshot": "",
        }])

        def decide(_state):
            return [
                {"key": "0", "label": "Email Address", "value": "wrong@example.com"},
                {"key": "2", "label": "Choose Password", "value": "Secret123!"},
            ]

        final = build_apply_graph(session, decide).invoke({
            "step": 0,
            "resume": "Ada Lovelace ada@example.com",
            "profile": {"email": "ada@example.com"},
            "filled": [],
            "blank": [],
            "signature": "",
        })

        typed = session.fills[0]
        self.assertEqual("ada@example.com", typed[0]["value"])
        self.assertEqual("ada@example.com", typed[1]["value"])
        self.assertTrue(all(item.get("label") != "Choose Password" or not item.get("value") for item in typed))
        self.assertNotIn("advance", session.clicks)
        self.assertIn("Email Address: ada@example.com", final["filled"])
        self.assertIn("Choose Password", final["blank"])

    def test_captcha_waits_for_a_person_before_typing(self):
        from langgraph.checkpoint.memory import MemorySaver
        from langgraph.types import Command

        session = RecordingSession([
            {
                "fields": [{"key": "0", "label": "Email", "kind": "text", "options": []}],
                "buttons": ["Next"],
                "wall": "captcha",
                "screenshot": "",
            },
            {
                "fields": [{"key": "0", "label": "Email", "kind": "text", "options": []}],
                "buttons": ["Next"],
                "wall": "",
                "screenshot": "",
            },
        ])
        graph = build_apply_graph(session, lambda _state: [], checkpointer=MemorySaver())
        config = {"configurable": {"thread_id": "captcha-test"}}
        graph.invoke({
            "step": 0,
            "resume": "Ada Lovelace ada@example.com",
            "profile": {"email": "ada@example.com"},
            "filled": [],
            "blank": [],
            "signature": "",
        }, config)
        self.assertTrue(graph.get_state(config).interrupts)
        self.assertEqual([], session.fills)
        final = graph.invoke(Command(resume=True), config)
        self.assertIn("Email: ada@example.com", final["filled"])

    def test_next_advances_once_then_stops_when_the_page_does_not_change(self):
        page = {
            "fields": [{"key": "0", "label": "City", "kind": "text", "options": []}],
            "buttons": ["Next"],
            "screenshot": "",
        }
        session = RecordingSession([page, page])
        graph = build_apply_graph(session, lambda _state: [{"key": "0", "label": "City", "value": "Paris"}])
        build = graph.invoke({
            "step": 0,
            "resume": "Lives in Bengaluru",
            "profile": {"city": "Bengaluru"},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        self.assertEqual(["advance"], session.clicks)
        self.assertIn("City: Bengaluru", build["filled"])

    def test_workday_manual_apply_fills_email_and_phone(self):
        session = RecordingSession([
            {
                "fields": [],
                "buttons": ["Apply", "Autofill with Resume", "Apply Manually", "Use My Last Application"],
                "screenshot": "",
            },
            {
                "fields": [
                    {"key": "0", "label": "Email", "kind": "text", "options": [], "required": True},
                    {"key": "1", "label": "Phone Device Type", "kind": "combobox", "options": [], "currentValue": "Select One"},
                    {"key": "2", "label": "Country Phone Code", "kind": "combobox", "options": [], "currentValue": "India (+91)"},
                    {"key": "3", "label": "Phone Number", "kind": "text", "options": [], "required": True},
                    {"key": "4", "label": "Phone Extension", "kind": "text", "options": []},
                ],
                "buttons": ["Next"],
                "screenshot": "",
            },
        ])
        final = build_apply_graph(session, lambda _state: []).invoke({
            "step": 0,
            "resume": "Ada Lovelace",
            "profile": {"email": "ada@example.com", "phone": "+91 98765 43210", "country": "India"},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        self.assertEqual("Apply Manually", session.clicks[0])
        self.assertNotIn("apply", session.clicks)
        self.assertIn("advance", session.clicks)
        typed = {item["label"]: item["value"] for item in session.fills[0]}
        self.assertEqual("ada@example.com", typed["Email"])
        self.assertEqual("Mobile", typed["Phone Device Type"])
        self.assertEqual("9876543210", typed["Phone Number"])
        self.assertNotIn("Country Phone Code", typed)
        self.assertNotIn("Phone Extension", typed)
        self.assertNotIn("Phone Extension", final["blank"])

    def test_my_information_answers_the_source_and_previous_employer(self):
        question = "Have you previously worked for Automation Anywhere? If Yes, please answer the questions below.?"
        session = RecordingSession([{
            "fields": [
                {"key": "0", "label": "How Did You Hear About Us?", "kind": "combobox", "options": [], "currentValue": "Select One"},
                {"key": "1", "label": "Yes", "kind": "radio", "group": question, "options": []},
                {"key": "2", "label": "No", "kind": "radio", "group": question, "options": []},
                {"key": "3", "label": "Country", "kind": "combobox", "options": [], "currentValue": "India"},
                {"key": "4", "label": "Given Name(s)", "kind": "text", "options": []},
            ],
            "buttons": ["Back to Job Posting", "Next"],
            "screenshot": "",
        }])
        final = build_apply_graph(session, lambda _state: []).invoke({
            "step": 0,
            "resume": "Ada Lovelace, software engineer",
            "profile": {"firstName": "Ada", "country": "India", "email": "ada@example.com"},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        typed = {item["label"]: item["value"] for item in session.fills[0]}
        self.assertEqual("LinkedIn", typed["How Did You Hear About Us?"])
        self.assertEqual("No", typed["No"])
        self.assertNotIn("Yes", typed)
        self.assertNotIn("Country", typed)
        self.assertEqual("Ada", typed["Given Name(s)"])
        self.assertIn("advance", session.clicks)
        self.assertIn("Given Name(s): Ada", final["filled"])

    def test_unreadable_application_step_does_not_click_next(self):
        session = RecordingSession([{
            "fields": [],
            "buttons": ["Back to Job Posting", "Next"],
            "screenshot": "",
        }])
        build_apply_graph(session, lambda _state: []).invoke({
            "step": 0,
            "resume": "",
            "profile": {},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        self.assertEqual([], session.clicks)
        self.assertEqual([], session.fills)

    def test_work_history_opens_each_saved_role(self):
        from agent.experience import parse_jobs

        jobs = parse_jobs(
            "Lead Developer\n"
            "Northwind\n"
            "12/2021 - 01/2026 Place: Bengaluru, India\n"
            "Led a migration.\n"
            "--------------------------------------------------\n"
            "Developer\n"
            "Contoso\n"
            "01/2018 - Present Bengaluru, India\n"
            "Built services.\n"
        )
        self.assertEqual("Lead Developer", jobs[0]["title"])
        self.assertEqual("Northwind", jobs[0]["company"])
        self.assertEqual("Bengaluru, India", jobs[0]["location"])
        self.assertEqual("12", jobs[0]["startMonth"])
        self.assertEqual("2021", jobs[0]["startYear"])
        self.assertEqual("1", jobs[0]["endMonth"])
        self.assertFalse(jobs[0]["current"])
        self.assertTrue(jobs[1]["current"])
        self.assertEqual("", jobs[1]["endYear"])

        session = RecordingSession([
            {"fields": [], "buttons": ["Add", "Next", "Back to Job Posting"], "screenshot": ""},
            {
                "fields": [{"key": "0", "label": "Job Title", "kind": "text", "currentValue": "Lead Developer"}],
                "buttons": ["Next", "Back to Job Posting"],
                "screenshot": "",
            },
        ])
        build_apply_graph(session, lambda _state: []).invoke({
            "step": 0,
            "resume": "",
            "profile": {"workHistory": "Lead Developer\nNorthwind\n01/2020 - Present Bengaluru\nBuilt it.\n"},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        self.assertEqual(["experience", "advance"], session.clicks)

    def test_site_search_is_skipped_and_apply_opens_the_form(self):
        session = RecordingSession([{
            "fields": [{"key": "0", "label": "Rechercher un job", "kind": "text", "options": []}],
            "buttons": ["Add to favorites", "Apply"],
            "screenshot": "",
        }])
        build_apply_graph(session, lambda _state: []).invoke({
            "step": 0,
            "resume": "",
            "profile": {},
            "filled": [],
            "blank": [],
            "signature": "",
        })
        self.assertIn("Apply", session.clicks)
        self.assertEqual([], session.fills)

    def test_country_code_and_terms_box_are_filled(self):
        actions = guard_fields(
            [
                {"key": "0", "label": "Country code", "kind": "select", "currentValue": "Select...", "options": []},
                {"key": "1", "label": "I agree with C5i's terms and privacy notice", "kind": "checkbox", "options": []},
                {"key": "2", "label": "I agree to receive communications from C5i", "kind": "checkbox", "options": []},
            ],
            [],
            "",
            {"country": "India"},
        )
        by_label = {item["label"]: item for item in actions}
        self.assertEqual("+91", by_label["Country code"]["value"])
        self.assertEqual("Yes", by_label["I agree with C5i's terms and privacy notice"]["value"])
        self.assertTrue(by_label["I agree to receive communications from C5i"].get("keep"))

    def test_submit_button_stops_the_graph(self):
        self.assertEqual("stop", choose_action(["Save and continue", "Submit"]))
        self.assertEqual("advance", choose_action(["Save and continue"]))
        self.assertEqual("advance", choose_action(["Sign in with Apple", "Sign in with Google", "Sign in with email"]))

    def test_langsmith_tracing_turns_on_only_for_a_langsmith_key(self):
        import os
        from agent.tracing import enable_langsmith

        os.environ.pop("LANGSMITH_API_KEY", None)
        self.assertFalse(enable_langsmith())
        self.assertEqual("false", os.environ.get("LANGSMITH_TRACING"))

        os.environ["LANGSMITH_API_KEY"] = "lsv2_test"
        try:
            self.assertTrue(enable_langsmith())
            self.assertEqual("true", os.environ.get("LANGSMITH_TRACING"))
            self.assertEqual("automate-job", os.environ.get("LANGSMITH_PROJECT"))
            from agent.tracing import _llm_tracing
            self.assertTrue(_llm_tracing)
        finally:
            os.environ.pop("LANGSMITH_API_KEY", None)
            enable_langsmith()
            from agent.tracing import _llm_tracing
            self.assertFalse(_llm_tracing)
        answers = guard_fields(
            [{"key": "0", "label": "Phone", "kind": "text", "options": []}],
            [{"key": "0", "label": "Phone", "value": "99999"}],
            "phone is not listed",
            {},
        )
        self.assertIsNone(answers[0]["value"])

    def test_host_fill_is_logged_as_langsmith_tools(self):
        import os
        import langsmith
        import agent.protocol as protocol

        recorded = []

        class FakeTrace:
            def __init__(self, name, run_type="chain", inputs=None, project_name=None):
                self.name = name
                self.record = {
                    "name": name,
                    "run_type": run_type,
                    "inputs": inputs,
                    "project": project_name,
                }
                recorded.append(self.record)

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

            def end(self, outputs=None):
                self.record["outputs"] = outputs

        original_trace = langsmith.trace
        original_exchange = protocol._exchange
        os.environ["LANGSMITH_TRACING"] = "true"
        langsmith.trace = FakeTrace
        protocol._exchange = lambda payload: {"ok": True, "image": "secret-bytes"}
        try:
            result = protocol.call_host({
                "tool": "fill",
                "actions": [
                    {"key": "0", "label": "Email", "kind": "text", "value": "ada@example.com"},
                    {"key": "1", "label": "Choose Password", "kind": "password", "value": "Secret123!"},
                ],
                "screenshot": "do-not-log",
            })
        finally:
            langsmith.trace = original_trace
            protocol._exchange = original_exchange
            os.environ["LANGSMITH_TRACING"] = "false"

        self.assertTrue(result["ok"])
        names = [item["name"] for item in recorded]
        self.assertEqual(["fill", "fill_text"], names)
        self.assertTrue(all(item["run_type"] == "tool" for item in recorded))
        self.assertEqual("automate-job", recorded[0]["project"])
        self.assertNotIn("screenshot", recorded[0]["inputs"])
        self.assertNotIn("image", recorded[0]["outputs"])
        self.assertNotIn("value", recorded[0]["inputs"]["actions"][1])


if __name__ == "__main__":
    unittest.main()
