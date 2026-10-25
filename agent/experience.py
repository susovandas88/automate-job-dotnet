import re

_DATE = re.compile(
    r"(\d{1,2})/(\d{4})\s*-\s*(Present|(\d{1,2})/(\d{4}))\s*(?:Place:\s*)?(.*)$",
    re.IGNORECASE,
)


def parse_jobs(work_history):
    text = (work_history or "").replace("\r\n", "\n").strip()
    if not text:
        return []
    jobs = []
    for block in re.split(r"\n\s*-{5,}\s*\n", text):
        lines = [line.strip() for line in block.splitlines() if line.strip()]
        date_index = next((index for index, line in enumerate(lines) if _DATE.search(line)), None)
        if date_index is None or date_index < 1:
            continue
        match = _DATE.search(lines[date_index])
        if not match:
            continue
        current = match.group(3).lower() == "present"
        jobs.append({
            "title": lines[0],
            "company": lines[1] if date_index > 1 else "",
            "location": (match.group(6) or "").strip(" ,"),
            "startMonth": str(int(match.group(1))),
            "startYear": match.group(2),
            "endMonth": "" if current else str(int(match.group(4))),
            "endYear": "" if current else match.group(5),
            "current": current,
            "description": "\n".join(lines[date_index + 1 :]),
        })
    return jobs


def experience_needed(fields, buttons, profile):
    jobs = parse_jobs((profile or {}).get("workHistory") or "")
    if not jobs:
        return False
    titles = [field for field in fields or [] if "job title" in (field.get("label") or "").lower()]
    if not titles:
        return any(re.search(r"^add(\s+another)?$", (button or "").strip(), re.IGNORECASE) for button in buttons or [])
    filled = [field for field in titles if (field.get("currentValue") or "").strip()]
    return len(filled) < len(jobs)
